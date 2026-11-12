using System.Net;
using System.Text.RegularExpressions;
using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Campaigns;
using FacebookPublisher.Application.Common;
using FacebookPublisher.Application.Content;
using FacebookPublisher.Application.Groups;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Infrastructure.Campaigns;
using FacebookPublisher.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FacebookPublisher.Tests;

/// <summary>
/// The real web application (Program.cs) in-process against its own LocalDB database. Only one host is created for the
/// whole test run (Serilog's bootstrap logger can be initialised once per process), shared through this collection.
/// </summary>
public sealed partial class WebAppFixture : IAsyncLifetime
{
    public const string AdminEmail = "admin@groupublisher.test";
    public const string AdminPassword = "Admin-Passw0rd-Test";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "gp-web-" + Guid.NewGuid().ToString("N"));

    public SqlTestDatabase Db { get; private set; } = null!;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Db = await SqlTestDatabase.CreateAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:DefaultConnection", Db.ConnectionString);
            builder.UseSetting("Hosting:AutomationEnabled", "false"); // never launches a browser
            builder.UseSetting("InitialAdmin:Email", AdminEmail);
            builder.UseSetting("InitialAdmin:Password", AdminPassword);
            builder.UseSetting("Storage:RootPath", Path.Combine(_temp, "uploads"));
            builder.UseSetting("Serilog:WriteTo:1:Args:path", Path.Combine(_temp, "logs", "test-.log"));
            builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        });
        _ = Factory.Server; // starts the host: migrations, roles and the initial administrator
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public HttpClient NewClient() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public async Task<Guid> CreateUserAsync(string email, string password, bool admin = false)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, LockoutEnabled = true };
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(user, AppRoles.User);
        if (admin)
        {
            await users.AddToRoleAsync(user, AppRoles.Admin);
        }
        return user.Id;
    }

    public async Task SetDisabledInDatabaseAsync(Guid userId, bool disabled)
    {
        await using var db = await Db.CreateContextAsync();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.IsDisabled = disabled;
        await db.SaveChangesAsync();
    }

    /// <summary>Posts the real login form (with its antiforgery token) and returns the redirect response.</summary>
    public static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password, string returnUrl = "/")
    {
        var page = await client.GetStringAsync($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        var token = TokenRegex().Match(page).Groups[1].Value;
        return await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token),
            ["email"] = email,
            ["password"] = password,
            ["returnUrl"] = returnUrl
        }));
    }

    public static string AntiforgeryTokenFrom(string html) => WebUtility.HtmlDecode(TokenRegex().Match(html).Groups[1].Value);

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();
}

[CollectionDefinition(Name)]
public sealed class WebAppCollection : ICollectionFixture<WebAppFixture>
{
    public const string Name = "Web application";
}

[Collection(WebAppCollection.Name)]
public sealed class WebAuthenticationTests(WebAppFixture app)
{
    [Theory]
    [InlineData("/")]
    [InlineData("/groups")]
    [InlineData("/content")]
    [InlineData("/campaigns/new")]
    [InlineData("/monitor")]
    [InlineData("/reports")]
    [InlineData("/settings")]
    [InlineData("/admin/users")]
    [InlineData("/media/00000000-0000-0000-0000-000000000001")]
    [InlineData("/reports/00000000-0000-0000-0000-000000000001/export/csv")]
    public async Task Unauthenticated_requests_are_sent_to_the_login_page(string path)
    {
        var response = await app.NewClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task Hub_and_blazor_connections_require_sign_in()
    {
        var client = app.NewClient();
        var hub = await client.PostAsync("/hubs/campaigns/negotiate?negotiateVersion=1", null);
        var circuit = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);

        Assert.NotEqual(HttpStatusCode.OK, hub.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, circuit.StatusCode);
    }

    [Fact]
    public async Task Initial_administrator_from_configuration_can_sign_in_and_reach_user_management()
    {
        var client = app.NewClient();

        var login = await WebAppFixture.LoginAsync(client, WebAppFixture.AdminEmail, WebAppFixture.AdminPassword);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location!.OriginalString);

        var users = await client.GetAsync("/admin/users");
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        Assert.Contains("User Management", await users.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Successful_login_opens_the_users_dashboard_and_logout_ends_the_session()
    {
        var email = $"user-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email, "Correct-Passw0rd");
        var client = app.NewClient();

        var login = await WebAppFixture.LoginAsync(client, email, "Correct-Passw0rd", "/groups");
        Assert.Equal("/groups", login.Headers.Location!.OriginalString);
        var groups = await client.GetAsync("/groups");
        Assert.Equal(HttpStatusCode.OK, groups.StatusCode);
        var html = await groups.Content.ReadAsStringAsync();
        Assert.Contains("Facebook Groups", html);
        Assert.Contains(email, html); // signed-in identity shown in the layout

        var logout = await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebAppFixture.AntiforgeryTokenFrom(html)
        }));
        Assert.Equal("/login", logout.Headers.Location!.OriginalString);
        var afterLogout = await client.GetAsync("/groups");
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Invalid_password_is_rejected_and_repeated_failures_lock_the_account()
    {
        var email = $"user-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email, "Correct-Passw0rd");
        var client = app.NewClient();

        var wrong = await WebAppFixture.LoginAsync(client, email, "wrong-password");
        Assert.Contains("error=invalid", wrong.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);

        for (var i = 0; i < 4; i++)
        {
            await WebAppFixture.LoginAsync(client, email, "wrong-password");
        }
        var locked = await WebAppFixture.LoginAsync(client, email, "Correct-Passw0rd");
        Assert.Contains("error=locked", locked.Headers.Location!.OriginalString);

        var unknown = await WebAppFixture.LoginAsync(client, "nobody@example.test", "whatever-123");
        Assert.Contains("error=invalid", unknown.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Disabled_accounts_cannot_sign_in_or_keep_using_an_existing_session()
    {
        var email = $"user-{Guid.NewGuid():N}@example.test";
        var userId = await app.CreateUserAsync(email, "Correct-Passw0rd");
        var client = app.NewClient();
        await WebAppFixture.LoginAsync(client, email, "Correct-Passw0rd");
        var campaignId = await SeedCampaignAsync(userId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/reports/{campaignId}/export/csv")).StatusCode);

        await app.SetDisabledInDatabaseAsync(userId, true);

        // Existing cookie: data access is refused immediately (the cookie itself is rejected within a minute).
        Assert.Contains((await client.GetAsync($"/reports/{campaignId}/export/csv")).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Redirect });
        var relogin = await WebAppFixture.LoginAsync(app.NewClient(), email, "Correct-Passw0rd");
        Assert.Contains("error=disabled", relogin.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Regular_users_cannot_open_user_management()
    {
        var email = $"user-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(email, "Correct-Passw0rd");
        var client = app.NewClient();
        await WebAppFixture.LoginAsync(client, email, "Correct-Passw0rd");

        var response = await client.GetAsync("/admin/users");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/access-denied", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task Changing_ids_in_requests_does_not_reach_another_users_files_or_reports()
    {
        var userA = await app.CreateUserAsync($"a-{Guid.NewGuid():N}@example.test", "Correct-Passw0rd");
        var emailB = $"b-{Guid.NewGuid():N}@example.test";
        await app.CreateUserAsync(emailB, "Correct-Passw0rd");
        var campaignOfA = await SeedCampaignAsync(userA);
        var attachmentOfA = await SeedAttachmentAsync(userA);

        var clientB = app.NewClient();
        await WebAppFixture.LoginAsync(clientB, emailB, "Correct-Passw0rd");

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/reports/{campaignOfA}/export/csv")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/reports/{campaignOfA}/export/xlsx")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/media/{attachmentOfA}")).StatusCode);
        var monitor = await (await clientB.GetAsync($"/monitor/{campaignOfA}")).Content.ReadAsStringAsync();
        Assert.Contains("Campaign not found", monitor);
        Assert.DoesNotContain("Isolation campaign", monitor);
    }

    [Fact]
    public async Task User_management_service_is_admin_only_and_protects_the_last_administrator()
    {
        var regular = await app.CreateUserAsync($"r-{Guid.NewGuid():N}@example.test", "Correct-Passw0rd");
        var adminId = await AdminIdAsync();

        var asRegular = Administration(regular, isAdmin: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => asRegular.ListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            asRegular.CreateAsync(new CreateUserRequest("x@example.test", null, "Correct-Passw0rd", true)));

        var asAdmin = Administration(adminId, isAdmin: true);
        var created = await asAdmin.CreateAsync(new CreateUserRequest($"new-{Guid.NewGuid():N}@example.test", "New User", "Correct-Passw0rd", false));
        Assert.Contains(await asAdmin.ListAsync(), u => u.Id == created.Id && !u.IsAdmin && u.DisplayName == "New User");
        await Assert.ThrowsAsync<AppValidationException>(() => asAdmin.SetDisabledAsync(adminId, true));
        await Assert.ThrowsAsync<AppValidationException>(() => asAdmin.UpdateAsync(adminId, null, isAdmin: false));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            asAdmin.CreateAsync(new CreateUserRequest($"weak-{Guid.NewGuid():N}@example.test", null, "short", false)));

        // Disabling stops the user's running campaigns and blocks sign-in.
        var tracker = app.Factory.Services.GetRequiredService<ICampaignExecutionTracker>();
        var runningCampaign = Guid.NewGuid();
        var token = tracker.Begin(runningCampaign, created.Id, CancellationToken.None);
        await asAdmin.SetDisabledAsync(created.Id, true);
        Assert.True(token.IsCancellationRequested);
        tracker.End(runningCampaign);
        var login = await WebAppFixture.LoginAsync(app.NewClient(), created.Email, "Correct-Passw0rd");
        Assert.Contains("error=disabled", login.Headers.Location!.OriginalString);
    }

    // ---------- helpers ----------

    private UserAdministration Administration(Guid caller, bool isAdmin) => new(
        app.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
        new FakeCurrentUser(caller, isAdmin),
        app.Factory.Services.GetRequiredService<ICampaignExecutionTracker>(),
        NullLogger<UserAdministration>.Instance);

    private async Task<Guid> AdminIdAsync()
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByEmailAsync(WebAppFixture.AdminEmail))!.Id;
    }

    private async Task<Guid> SeedCampaignAsync(Guid user)
    {
        var groups = new GroupService(app.Db.For(user), new FakeAutomationFactory(), new FakeCurrentUser(user),
            new CampaignExecutionTracker(), NullLogger<GroupService>.Instance);
        var group = await groups.AddAsync(new SaveGroupRequest(null, $"https://www.facebook.com/groups/{Guid.NewGuid():N}", null));
        var content = new ContentService(app.Db.For(user), new CampaignRunnerTests.FakeStorage(),
            Microsoft.Extensions.Options.Options.Create(new ContentUploadOptions()), NullLogger<ContentService>.Instance);
        var contentId = await content.CreateAsync(new CreateContentRequest($"Post {Guid.NewGuid():N}", "hello", []));
        var campaigns = new CampaignService(app.Db.For(user), new RecordingQueue(), new CampaignExecutionTracker(),
            new CampaignApprovalGate(), new CampaignRunnerTests.RecordingNotifier(), NullLogger<CampaignService>.Instance);
        return await campaigns.CreateAsync(new CreateCampaignRequest("Isolation campaign", contentId, [group.Id], new BatchPacingConfig(), false), start: false);
    }

    private async Task<Guid> SeedAttachmentAsync(Guid user)
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var content = new ContentService(app.Db.For(user), storage,
            Microsoft.Extensions.Options.Options.Create(new ContentUploadOptions()), NullLogger<ContentService>.Instance);
        var contentId = await content.CreateAsync(new CreateContentRequest($"Post {Guid.NewGuid():N}", null,
            [new UploadFile("photo.png", 3, () => new MemoryStream([1, 2, 3]))]));
        return (await content.GetAsync(contentId)).Attachments.Single().Id;
    }
}
