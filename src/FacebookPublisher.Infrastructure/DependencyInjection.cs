using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Content;
using FacebookPublisher.Infrastructure.Campaigns;
using FacebookPublisher.Infrastructure.Identity;
using FacebookPublisher.Infrastructure.Persistence;
using FacebookPublisher.Infrastructure.Reporting;
using FacebookPublisher.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FacebookPublisher.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers persistence, storage and execution infrastructure. The host must also register an
    /// <see cref="ICurrentUser"/> implementation and ASP.NET Core Identity (see the Web project).
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3)));
        // Scoped context for ASP.NET Core Identity's stores. It is never owner-scoped, so it cannot see application data.
        services.TryAddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        services.AddSingleton<ISystemDbContextFactory, AppDbContextFactory>();
        services.AddScoped<IApplicationDbContextFactory, CurrentUserDbContextFactory>();
        services.AddScoped<DatabaseInitializer>();
        services.Configure<InitialAdminOptions>(configuration.GetSection(InitialAdminOptions.SectionName));
        services.AddSingleton<IUserAccounts, UserAccounts>();
        services.AddScoped<IUserAdministration, UserAdministration>();

        services.Configure<FileStorageOptions>(configuration.GetSection(FileStorageOptions.SectionName));
        services.Configure<ContentUploadOptions>(configuration.GetSection(ContentUploadOptions.SectionName));
        services.AddSingleton(sp => new LocalFileStorage(
            sp.GetRequiredService<IOptions<FileStorageOptions>>(),
            contentRootPath,
            sp.GetRequiredService<ILogger<LocalFileStorage>>()));
        services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<LocalFileStorage>());

        services.AddSingleton<IReportExporter, ReportExporter>();
        services.AddSingleton<ICampaignQueue, CampaignQueue>();
        services.AddSingleton<ICampaignExecutionTracker, CampaignExecutionTracker>();
        services.AddSingleton<ICampaignApprovalGate, CampaignApprovalGate>();
        services.AddSingleton<IRandomSource, SystemRandomSource>();
        services.AddSingleton<IAsyncDelay, TaskAsyncDelay>();
        return services;
    }
}
