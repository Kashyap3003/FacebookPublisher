using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Content;
using FacebookPublisher.Infrastructure.Campaigns;
using FacebookPublisher.Infrastructure.Persistence;
using FacebookPublisher.Infrastructure.Reporting;
using FacebookPublisher.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FacebookPublisher.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3)));
        services.AddSingleton<IApplicationDbContextFactory, AppDbContextFactory>();
        services.AddSingleton<DatabaseInitializer>();

        services.Configure<FileStorageOptions>(configuration.GetSection(FileStorageOptions.SectionName));
        services.Configure<ContentUploadOptions>(configuration.GetSection(ContentUploadOptions.SectionName));
        services.AddSingleton<IFileStorage>(sp => new LocalFileStorage(
            sp.GetRequiredService<IOptions<FileStorageOptions>>(),
            contentRootPath,
            sp.GetRequiredService<ILogger<LocalFileStorage>>()));

        services.AddSingleton<IReportExporter, ReportExporter>();
        services.AddSingleton<ICampaignQueue, CampaignQueue>();
        services.AddSingleton<ICampaignExecutionTracker, CampaignExecutionTracker>();
        services.AddSingleton<ICampaignApprovalGate, CampaignApprovalGate>();
        return services;
    }
}
