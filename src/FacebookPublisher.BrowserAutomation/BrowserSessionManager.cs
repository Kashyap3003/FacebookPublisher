using System.Collections.Concurrent;
using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Automation;
using FacebookPublisher.Application.Settings;
using FacebookPublisher.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace FacebookPublisher.BrowserAutomation;

/// <summary>
/// Owns one persistent browser context per application user. Each context uses that user's own profile folder (their
/// Facebook cookies and storage), launched with that user's browser settings. A per-user lock guarantees at most one
/// browser process per profile; users never share a context, page or profile, and a crash of one user's browser
/// only affects that user. No credentials ever pass through the application.
/// </summary>
public sealed class BrowserSessionManager(
    ISystemDbContextFactory dbFactory,
    BrowserProfileStore profiles,
    ILogger<BrowserSessionManager> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, UserSession> _sessions = new();
    private readonly SemaphoreSlim _playwrightGate = new(1, 1);
    private IPlaywright? _playwright;

    public bool IsOpen(Guid ownerId) => _sessions.TryGetValue(ownerId, out var s) && s.Context is not null;

    /// <summary>The settings the user's browser was last launched with (defaults before the first launch).</summary>
    public PublisherSettings SettingsFor(Guid ownerId) =>
        _sessions.TryGetValue(ownerId, out var s) ? s.Settings : SettingsService.CreateDefaults();

    public string ProfilePathFor(Guid ownerId) => profiles.GetProfilePath(ownerId);

    /// <summary>
    /// Returns the user's running context, launching it if needed. <paramref name="requireVisible"/> relaunches a
    /// headless browser as a visible one (needed for the Facebook login window).
    /// </summary>
    public async Task<IBrowserContext> GetContextAsync(Guid ownerId, bool requireVisible, CancellationToken ct)
    {
        var session = Session(ownerId);
        await session.Gate.WaitAsync(ct);
        try
        {
            var context = session.Context;
            if (context is not null && !(requireVisible && session.IsHeadless))
            {
                return context;
            }
            if (context is not null)
            {
                await CloseContextAsync(session);
            }

            session.Settings = await LoadSettingsAsync(ownerId, ct);
            session.IsHeadless = session.Settings.Headless && !requireVisible;
            session.Context = await LaunchAsync(session, ct);
            return session.Context;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task CloseAsync(Guid ownerId)
    {
        if (!_sessions.TryGetValue(ownerId, out var session))
        {
            return;
        }
        await session.Gate.WaitAsync();
        try
        {
            await CloseContextAsync(session);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>Disconnects Facebook for the user: closes their browser and deletes their profile folder.</summary>
    public async Task ResetAsync(Guid ownerId)
    {
        var session = Session(ownerId);
        await session.Gate.WaitAsync();
        try
        {
            await CloseContextAsync(session);
            profiles.Delete(ownerId);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var ownerId in _sessions.Keys)
        {
            await CloseAsync(ownerId);
        }
        _playwright?.Dispose();
        _playwrightGate.Dispose();
    }

    private UserSession Session(Guid ownerId)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An application user is required.", nameof(ownerId));
        }
        return _sessions.GetOrAdd(ownerId, id => new UserSession(id));
    }

    private async Task<IPlaywright> PlaywrightAsync()
    {
        if (_playwright is not null)
        {
            return _playwright;
        }
        await _playwrightGate.WaitAsync();
        try
        {
            return _playwright ??= await Playwright.CreateAsync();
        }
        finally
        {
            _playwrightGate.Release();
        }
    }

    private async Task<IBrowserContext> LaunchAsync(UserSession session, CancellationToken ct)
    {
        var playwright = await PlaywrightAsync();
        var settings = session.Settings;
        var profilePath = profiles.EnsureCreated(session.OwnerId);

        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = session.IsHeadless,
            Channel = string.IsNullOrWhiteSpace(settings.BrowserChannel) ? null : settings.BrowserChannel,
            ViewportSize = ViewportSize.NoViewport,
            Args = ["--start-maximized"]
        };

        IBrowserContext context;
        try
        {
            context = await LaunchWithRecoveryAsync(playwright, session.OwnerId, profilePath, options);
        }
        catch (PlaywrightException ex)
        {
            logger.LogError(ex, "Browser launch failed for user {OwnerId}", session.OwnerId);
            throw new AutomationHaltedException(HaltReason.BrowserUnavailable,
                $"Your browser could not be started ({FirstLine(ex.Message)}). Close any other window using your Group Publisher browser and check the browser setting.", ex);
        }

        context.SetDefaultNavigationTimeout(settings.NavigationTimeoutSeconds * 1000f);
        context.SetDefaultTimeout(15_000);
        context.Close += (_, _) =>
        {
            // The user closed the window (or the browser crashed); the next operation relaunches it.
            if (ReferenceEquals(session.Context, context))
            {
                session.Context = null;
                logger.LogInformation("Browser of user {OwnerId} closed", session.OwnerId);
            }
        };

        logger.LogInformation("Browser launched for user {OwnerId} (channel: {Channel}, headless: {Headless})",
            session.OwnerId, options.Channel ?? "chromium", session.IsHeadless);
        return context;
    }

    private async Task<IBrowserContext> LaunchWithRecoveryAsync(
        IPlaywright playwright,
        Guid ownerId,
        string profilePath,
        BrowserTypeLaunchPersistentContextOptions options)
    {
        try
        {
            return await playwright.Chromium.LaunchPersistentContextAsync(profilePath, options);
        }
        catch (PlaywrightException ex) when (options.Channel is null && ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Bundled Chromium is not installed; installing it now (one-time download)");
            var exitCode = await Task.Run(() => Microsoft.Playwright.Program.Main(["install", "chromium"]));
            if (exitCode != 0)
            {
                throw new AutomationHaltedException(HaltReason.BrowserUnavailable,
                    "Chromium could not be installed automatically. Run 'pwsh bin/Debug/net10.0/playwright.ps1 install chromium' or choose Google Chrome in Settings.", ex);
            }
            return await playwright.Chromium.LaunchPersistentContextAsync(profilePath, options);
        }
        catch (PlaywrightException ex) when (LooksLikeProfileInUse(ex) && profiles.TryClearStaleLocks(ownerId))
        {
            // A previous browser crashed and left its lock files; they were deletable, so nothing is using the profile.
            logger.LogWarning("Recovered the browser profile of user {OwnerId} from a stale lock", ownerId);
            return await playwright.Chromium.LaunchPersistentContextAsync(profilePath, options);
        }
    }

    private static bool LooksLikeProfileInUse(PlaywrightException ex) =>
        ex.Message.Contains("ProcessSingleton", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("already in use", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("user data directory", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Target page, context or browser has been closed", StringComparison.OrdinalIgnoreCase);

    private async Task CloseContextAsync(UserSession session)
    {
        var context = session.Context;
        session.Context = null;
        if (context is null)
        {
            return;
        }
        try
        {
            await context.CloseAsync();
            logger.LogInformation("Browser of user {OwnerId} closed", session.OwnerId);
        }
        catch (PlaywrightException ex)
        {
            logger.LogDebug(ex, "Browser of user {OwnerId} was already closed", session.OwnerId);
        }
    }

    /// <summary>Loads the user's own settings and performs the one-time move of a pre-multi-user profile.</summary>
    private async Task<PublisherSettings> LoadSettingsAsync(Guid ownerId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateForOwnerAsync(ownerId, ct);
        var settings = await db.Settings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            return SettingsService.CreateDefaults();
        }

        if (!string.IsNullOrWhiteSpace(settings.LegacyBrowserProfilePath))
        {
            profiles.TryMoveLegacyProfile(ownerId, settings.LegacyBrowserProfilePath);
            settings.LegacyBrowserProfilePath = null;
            await db.SaveChangesAsync(ct);
        }
        return settings;
    }

    private static string FirstLine(string message) => message.Split('\n')[0].Trim();

    private sealed class UserSession(Guid ownerId)
    {
        public Guid OwnerId { get; } = ownerId;

        /// <summary>Serialises launch/close/reset of this user's browser: one browser process per profile.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public volatile IBrowserContext? Context;

        public bool IsHeadless { get; set; }

        public PublisherSettings Settings { get; set; } = SettingsService.CreateDefaults();
    }
}
