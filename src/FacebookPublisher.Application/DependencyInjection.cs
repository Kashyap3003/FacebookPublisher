using FacebookPublisher.Application.Campaigns;
using FacebookPublisher.Application.Content;
using FacebookPublisher.Application.Groups;
using FacebookPublisher.Application.Reports;
using FacebookPublisher.Application.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace FacebookPublisher.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<GroupService>();
        services.AddScoped<ContentService>();
        services.AddScoped<CampaignService>();
        services.AddScoped<CampaignRunner>();
        services.AddSingleton<IBatchDelayService, BatchDelayService>();
        services.AddScoped<ReportService>();
        services.AddScoped<SettingsService>();
        services.AddScoped<BrowserSessionService>();
        return services;
    }
}
