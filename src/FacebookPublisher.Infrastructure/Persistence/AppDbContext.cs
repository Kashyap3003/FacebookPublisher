using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FacebookPublisher.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IApplicationDbContext
{
    public DbSet<FacebookGroup> Groups => Set<FacebookGroup>();

    public DbSet<ContentPost> Contents => Set<ContentPost>();

    public DbSet<ContentAttachment> Attachments => Set<ContentAttachment>();

    public DbSet<Campaign> Campaigns => Set<Campaign>();

    public DbSet<CampaignTarget> CampaignTargets => Set<CampaignTarget>();

    public DbSet<PublisherSettings> Settings => Set<PublisherSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored as readable strings, which keeps the database and exported data self-explanatory.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
    }
}

public sealed class AppDbContextFactory(IDbContextFactory<AppDbContext> inner) : IApplicationDbContextFactory
{
    public async Task<IApplicationDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
        await inner.CreateDbContextAsync(cancellationToken);
}
