using FacebookPublisher.Application;
using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Common;
using FacebookPublisher.BrowserAutomation;
using FacebookPublisher.Infrastructure;
using FacebookPublisher.Infrastructure.Identity;
using FacebookPublisher.Infrastructure.Persistence;
using FacebookPublisher.Web.BackgroundServices;
using FacebookPublisher.Web.Components;
using FacebookPublisher.Web.Endpoints;
using FacebookPublisher.Web.Identity;
using FacebookPublisher.Web.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Serilog;

if (Log.Logger is not Serilog.Extensions.Hosting.ReloadableLogger)
{
    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console()
        .CreateBootstrapLogger();
}

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();
    builder.Services.AddSignalR();
    builder.Services.Configure<HostingOptions>(builder.Configuration.GetSection(HostingOptions.SectionName));

    // ---------- Group Publisher accounts (ASP.NET Core Identity) ----------
    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
    builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = IdentityConstants.ApplicationScheme;
            options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        })
        .AddIdentityCookies();
    builder.Services.ConfigureApplicationCookie(options =>
    {
        options.LoginPath = AccountEndpoints.LoginPath;
        options.LogoutPath = AccountEndpoints.LogoutPath;
        options.AccessDeniedPath = "/access-denied";
        options.Cookie.Name = "GroupPublisher.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
    });
    // Disabled accounts, password resets and role changes take effect within a minute on existing cookies.
    builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));
    builder.Services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 10;
            options.Password.RequireNonAlphanumeric = false;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.SignIn.RequireConfirmedAccount = false;
        })
        .AddRoles<IdentityRole<Guid>>()
        .AddEntityFrameworkStores<AppDbContext>()
        .AddSignInManager()
        .AddDefaultTokenProviders()
        .AddClaimsPrincipalFactory<AppUserClaimsPrincipalFactory>();
    // Every endpoint (pages, Blazor circuit, SignalR hub, media and exports) requires a signed-in user unless it is
    // explicitly marked AllowAnonymous (only the login page and sign-out).
    builder.Services.AddAuthorization(options =>
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUser, CurrentUser>();

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);
    builder.Services.AddBrowserAutomation(builder.Configuration, builder.Environment.ContentRootPath);

    builder.Services.AddSingleton<CampaignEventBroker>();
    builder.Services.AddSingleton<CampaignActivityLog>();
    builder.Services.AddSingleton<ICampaignProgressNotifier, CampaignProgressNotifier>();
    builder.Services.Configure<CampaignWorkerOptions>(builder.Configuration.GetSection(CampaignWorkerOptions.SectionName));
    builder.Services.AddHostedService<CampaignWorker>();

    var app = builder.Build();

    // Migrate the database, ensure roles and the first administrator, and move pre-multi-user files.
    // No campaign is resumed or re-queued automatically: execution only starts when a user clicks Start or Resume.
    await using (var scope = app.Services.CreateAsyncScope())
    {
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }
    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseHttpsRedirection();
    app.UseSerilogRequestLogging();

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapAccountEndpoints();
    app.MapFileEndpoints();
    app.MapHub<CampaignHub>(CampaignHub.Path);
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Entry point type, exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
