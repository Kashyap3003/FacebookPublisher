using FacebookPublisher.Application;
using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Common;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using FacebookPublisher.BrowserAutomation;
using FacebookPublisher.Infrastructure;
using FacebookPublisher.Infrastructure.Persistence;
using FacebookPublisher.Web.BackgroundServices;
using FacebookPublisher.Web.Components;
using FacebookPublisher.Web.Endpoints;
using FacebookPublisher.Web.Realtime;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

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
    var hosting = builder.Configuration.GetSection(HostingOptions.SectionName).Get<HostingOptions>() ?? new HostingOptions();
    if (hosting.RequireLogin)
    {
        if (string.IsNullOrWhiteSpace(hosting.LoginUsername) || string.IsNullOrWhiteSpace(hosting.LoginPassword))
        {
            throw new InvalidOperationException("Hosting:RequireLogin is true, but Hosting:LoginUsername / Hosting:LoginPassword are not configured.");
        }

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = AccountEndpoints.LoginPath;
                options.LogoutPath = AccountEndpoints.LogoutPath;
                options.Cookie.Name = "fbpublisher.auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.SlidingExpiration = true;
            });
        // Everything (pages, Blazor circuit, SignalR hub, media and exports) requires sign-in unless marked AllowAnonymous.
        builder.Services.AddAuthorization(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    }

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);
    builder.Services.AddBrowserAutomation(builder.Configuration);

    builder.Services.AddSingleton<CampaignEventBroker>();
    builder.Services.AddSingleton<CampaignActivityLog>();
    builder.Services.AddSingleton<ICampaignProgressNotifier, CampaignProgressNotifier>();
    builder.Services.AddHostedService<CampaignWorker>();

    var app = builder.Build();

    // Migrate the database and seed settings. No campaign is resumed or re-queued automatically: execution only
    // starts when the user clicks Start or Resume.
    await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }
    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseHttpsRedirection();
    app.UseSerilogRequestLogging();

    if (hosting.RequireLogin)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAccountEndpoints();
    }

    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapFileEndpoints();
    app.MapHub<CampaignHub>(CampaignHub.Path);
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
