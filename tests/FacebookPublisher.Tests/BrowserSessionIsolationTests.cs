using FacebookPublisher.Application.Automation;
using FacebookPublisher.BrowserAutomation;
using FacebookPublisher.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace FacebookPublisher.Tests;

/// <summary>Per-user browser profile folders (no browser needed).</summary>
public sealed class BrowserProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gp-profiles-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    private BrowserProfileStore NewStore() => new(_root, NullLogger<BrowserProfileStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Each_user_gets_a_distinct_stable_profile_folder_named_by_user_id()
    {
        var store = NewStore();

        var a = store.GetProfilePath(_a);
        var b = store.GetProfilePath(_b);

        Assert.NotEqual(a, b);
        Assert.Equal(Path.Combine(store.Root, _a.ToString("N")), a);
        Assert.Equal(a, NewStore().GetProfilePath(_a)); // same location after an application restart
        Assert.Throws<ArgumentException>(() => store.GetProfilePath(Guid.Empty));
    }

    [Fact]
    public void Disconnecting_one_user_deletes_only_that_users_profile()
    {
        var store = NewStore();
        File.WriteAllText(Path.Combine(store.EnsureCreated(_a), "Cookies"), "a");
        File.WriteAllText(Path.Combine(store.EnsureCreated(_b), "Cookies"), "b");

        store.Delete(_a);

        Assert.False(store.Exists(_a));
        Assert.Equal("b", File.ReadAllText(Path.Combine(store.GetProfilePath(_b), "Cookies")));
    }

    [Fact]
    public void Stale_lock_files_are_cleared_but_a_lock_held_by_a_running_browser_is_respected()
    {
        var store = NewStore();
        var profile = store.EnsureCreated(_a);
        File.WriteAllText(Path.Combine(profile, "SingletonLock"), "stale");
        Assert.True(store.TryClearStaleLocks(_a));
        Assert.False(File.Exists(Path.Combine(profile, "SingletonLock")));

        // A running browser keeps its lock file open; it must not be deleted and the profile must not be reused.
        using var held = new FileStream(Path.Combine(profile, "lockfile"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.False(store.TryClearStaleLocks(_a));
    }

    [Fact]
    public void The_pre_multi_user_profile_moves_once_to_its_owner_and_never_from_another_users_profile()
    {
        var store = NewStore();
        var legacy = Path.Combine(Path.GetTempPath(), "gp-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "Cookies"), "facebook-session");

        Assert.True(store.TryMoveLegacyProfile(_a, legacy));
        Assert.Equal("facebook-session", File.ReadAllText(Path.Combine(store.GetProfilePath(_a), "Cookies")));
        Assert.False(Directory.Exists(legacy));
        Assert.False(store.TryMoveLegacyProfile(_a, legacy)); // already moved

        // A "legacy" path pointing at another user's profile is refused.
        Assert.False(store.TryMoveLegacyProfile(_b, store.GetProfilePath(_a)));
        Assert.True(store.Exists(_a));
        Assert.False(store.Exists(_b));
    }
}

/// <summary>Runs only when Google Chrome is installed (the browser channel the application uses by default).</summary>
public sealed class ChromeFactAttribute : FactAttribute
{
    public ChromeFactAttribute()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            "/usr/bin/google-chrome", "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
        ];
        if (!candidates.Any(File.Exists))
        {
            Skip = "Google Chrome is not installed.";
        }
    }
}

/// <summary>
/// Real (headless) Chrome sessions for two users, through the same <see cref="BrowserSessionManager"/> the application
/// uses. Nothing contacts Facebook: a cookie for example.com stands in for a login session.
/// </summary>
public sealed class BrowserSessionIsolationTests : IAsyncLifetime
{
    private static readonly string[] Origin = ["https://example.com"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gp-chrome-" + Guid.NewGuid().ToString("N"));
    private readonly CampaignRunnerTests.TestDbFactory _db = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        foreach (var user in new[] { _a, _b })
        {
            await using var db = await _db.CreateForOwnerAsync(user);
            db.Settings.Add(new PublisherSettings { OwnerId = user, Headless = true, BrowserChannel = "chrome" });
            await db.SaveChangesAsync();
        }
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Chrome may still be releasing files; the temp folder is cleaned up by the OS.
        }
        return Task.CompletedTask;
    }

    private BrowserSessionManager NewManager() =>
        new(_db, new BrowserProfileStore(_root, NullLogger<BrowserProfileStore>.Instance), NullLogger<BrowserSessionManager>.Instance);

    [ChromeFact]
    public async Task Each_user_has_their_own_persistent_session_that_survives_a_restart_and_is_never_shared()
    {
        await using (var manager = NewManager())
        {
            var contextA = await manager.GetContextAsync(_a, requireVisible: false, CancellationToken.None);
            var contextB = await manager.GetContextAsync(_b, requireVisible: false, CancellationToken.None);
            Assert.NotSame(contextA, contextB);

            await contextA.AddCookiesAsync([new Cookie
            {
                Name = "session", Value = "user-a", Url = Origin[0],
                Expires = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()
            }]);
            Assert.Empty(await contextB.CookiesAsync(Origin)); // B never sees A's session

            // The same user asking twice at the same time gets the one browser for their profile.
            var concurrent = await Task.WhenAll(
                manager.GetContextAsync(_a, false, CancellationToken.None),
                manager.GetContextAsync(_a, false, CancellationToken.None));
            Assert.Same(contextA, concurrent[0]);
            Assert.Same(contextA, concurrent[1]);
        }

        // Application restart: a new manager over the same profile folders.
        await using (var restarted = NewManager())
        {
            var contextA = await restarted.GetContextAsync(_a, false, CancellationToken.None);
            var contextB = await restarted.GetContextAsync(_b, false, CancellationToken.None);
            Assert.Contains(await contextA.CookiesAsync(Origin), c => c.Name == "session" && c.Value == "user-a");
            Assert.DoesNotContain(await contextB.CookiesAsync(Origin), c => c.Name == "session");
            Assert.NotEqual(restarted.ProfilePathFor(_a), restarted.ProfilePathFor(_b));
        }
    }

    [ChromeFact]
    public async Task A_profile_already_open_elsewhere_is_refused_and_a_crashed_browser_does_not_affect_other_users()
    {
        await using var first = NewManager();
        var contextA = await first.GetContextAsync(_a, false, CancellationToken.None);
        var contextB = await first.GetContextAsync(_b, false, CancellationToken.None);

        // A second instance (e.g. another app process) must not open the same profile while it is in use.
        await using (var second = NewManager())
        {
            await Assert.ThrowsAsync<AutomationHaltedException>(() => second.GetContextAsync(_a, false, CancellationToken.None));
        }
        var page = await contextA.NewPageAsync(); // the original session is unaffected
        await page.CloseAsync();

        // A's browser goes away (crash or window closed): B keeps working and A relaunches on next use.
        await contextA.CloseAsync();
        await WaitUntilAsync(() => !first.IsOpen(_a));
        Assert.True(first.IsOpen(_b));
        var pageB = await contextB.NewPageAsync();
        await pageB.CloseAsync();
        var relaunched = await first.GetContextAsync(_a, false, CancellationToken.None);
        Assert.NotSame(contextA, relaunched);
    }

    [ChromeFact]
    public async Task Disconnect_deletes_only_that_users_profile()
    {
        await using var manager = NewManager();
        await manager.GetContextAsync(_a, false, CancellationToken.None);
        await manager.GetContextAsync(_b, false, CancellationToken.None);

        await manager.ResetAsync(_a);

        Assert.False(manager.IsOpen(_a));
        Assert.False(Directory.Exists(manager.ProfilePathFor(_a)));
        Assert.True(manager.IsOpen(_b));
        Assert.True(Directory.Exists(manager.ProfilePathFor(_b)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }
        Assert.True(condition());
    }
}
