using FacebookPublisher.Application.Automation;
using FacebookPublisher.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FacebookPublisher.BrowserAutomation;

public static class DependencyInjection
{
    public static IServiceCollection AddBrowserAutomation(this IServiceCollection services, IConfiguration configuration)
    {
        var hosting = configuration.GetSection(HostingOptions.SectionName).Get<HostingOptions>() ?? new HostingOptions();
        if (!hosting.AutomationEnabled)
        {
            services.AddSingleton<IFacebookAutomation, DisabledFacebookAutomation>();
            return services;
        }

        services.Configure<FacebookAutomationOptions>(configuration.GetSection(FacebookAutomationOptions.SectionName));
        services.AddSingleton<BrowserSessionManager>();
        services.AddSingleton<IFacebookAutomation, PlaywrightFacebookAutomation>();
        return services;
    }
}
