using FacebookPublisher.Application.Automation;
using FacebookPublisher.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FacebookPublisher.BrowserAutomation;

public static class DependencyInjection
{
    public static IServiceCollection AddBrowserAutomation(this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        var hosting = configuration.GetSection(HostingOptions.SectionName).Get<HostingOptions>() ?? new HostingOptions();
        if (!hosting.AutomationEnabled)
        {
            services.AddSingleton<IFacebookAutomationFactory, DisabledFacebookAutomationFactory>();
            return services;
        }

        services.Configure<FacebookAutomationOptions>(configuration.GetSection(FacebookAutomationOptions.SectionName));
        services.AddSingleton(sp =>
        {
            var root = sp.GetRequiredService<IOptions<FacebookAutomationOptions>>().Value.BrowserProfilesRoot;
            return new BrowserProfileStore(
                Path.IsPathRooted(root) ? root : Path.Combine(contentRootPath, root),
                sp.GetRequiredService<ILogger<BrowserProfileStore>>());
        });
        services.AddSingleton<BrowserSessionManager>();
        services.AddSingleton<IFacebookAutomationFactory, PlaywrightFacebookAutomationFactory>();
        return services;
    }
}
