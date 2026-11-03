using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Automation;
using FacebookPublisher.Application.Settings;
using FacebookPublisher.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace FacebookPublisher.BrowserAutomation;

/// <summary>
/// Owns the single persistent browser context. The profile folder keeps the cookies of a session the user logged
/// into manually, so no credentials ever pass through the application.
/// </summary>
public sealed class BrowserSessionManager(
    IApplicationDbContextFactory dbFactory,
    ILogger<BrowserSessionManager> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private volatile IBrowserContext? _context;
    private bool _isHeadless;

    public bool IsOpen => _context is not null;

    public PublisherSettings Settings { get; private set; } = SettingsService.CreateDefaults();

    /// <summary>Returns the running context, launching it if needed. <paramref name="requireVisible"/> relaunches a headless browser as headed.</summary>
    public async Task<IBrowserContext> GetContextAsync(bool requireVisible, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var context = _context;
            if (context is not null && !(requireVisible && _isHeadless))
            {
                return context;
            }

            if (context is not null)
            {
                await CloseContextAsync();
            }

            Settings = await LoadSettingsAsync(ct);
            _isHeadless = Settings.Headless && !requireVisible;
            _context = await LaunchAsync(Settings, _isHeadless);
            return _context;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await CloseContextAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _playwright?.Dispose();
        _gate.Dispose();
    }

    private async Task<IBrowserContext> LaunchAsync(PublisherSettings settings, bool headless)
    {
        _playwright ??= await Playwright.CreateAsync();
        Directory.CreateDirectory(settings.BrowserProfilePath);

        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = headless,
            Channel = string.IsNullOrWhiteSpace(settings.BrowserChannel) ? null : settings.BrowserChannel,
            ViewportSize = ViewportSize.NoViewport,
            Args = ["--start-maximized"]
        };

        IBrowserContext context;
        try
        {
            context = await _playwright.Chromium.LaunchPersistentContextAsync(settings.BrowserProfilePath, options);
        }
        catch (PlaywrightException ex) when (options.Channel is null && ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Bundled Chromium is not installed; installing it now (one-time download)");
            var exitCode = await Task.Run(() => Microsoft.Playwright.Program.Main(["install", "chromium"]));
            if (exitCode != 0)
            {
                throw new AutomationHaltedException(HaltReason.BrowserUnavailable,
                    "Chromium could not be installed automatically. Run 'pwsh bin/Debug/net10.0/playwright.ps1 install chromium' or choose Microsoft Edge in Settings.", ex);
            }
            context = await _playwright.Chromium.LaunchPersistentContextAsync(settings.BrowserProfilePath, options);
        }
        catch (PlaywrightException ex)
        {
            logger.LogError(ex, "Browser launch failed");
            throw new AutomationHaltedException(HaltReason.BrowserUnavailable,
                $"The browser could not be started ({FirstLine(ex.Message)}). Close any other browser window using the profile folder and check the browser setting.", ex);
        }

        context.SetDefaultNavigationTimeout(settings.NavigationTimeoutSeconds * 1000f);
        context.SetDefaultTimeout(15_000);
        context.Close += (_, _) =>
        {
            // The user closed the window (or the browser crashed); the next operation relaunches it.
            if (ReferenceEquals(_context, context))
            {
                _context = null;
                logger.LogInformation("Browser context closed");
            }
        };

        logger.LogInformation("Browser launched (channel: {Channel}, headless: {Headless}, profile: {Profile})",
            options.Channel ?? "chromium", headless, settings.BrowserProfilePath);
        return context;
    }

    private async Task CloseContextAsync()
    {
        var context = _context;
        _context = null;
        if (context is null)
        {
            return;
        }
        try
        {
            await context.CloseAsync();
            logger.LogInformation("Browser closed");
        }
        catch (PlaywrightException ex)
        {
            logger.LogDebug(ex, "Browser was already closed");
        }
    }

    private async Task<PublisherSettings> LoadSettingsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == PublisherSettings.SingletonId, ct)
            ?? SettingsService.CreateDefaults();
    }

    private static string FirstLine(string message) => message.Split('\n')[0].Trim();
}
