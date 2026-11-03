using FacebookPublisher.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FacebookPublisher.Application.Abstractions;

public interface IApplicationDbContext : IAsyncDisposable, IDisposable
{
    DbSet<FacebookGroup> Groups { get; }

    DbSet<ContentPost> Contents { get; }

    DbSet<ContentAttachment> Attachments { get; }

    DbSet<Campaign> Campaigns { get; }

    DbSet<CampaignTarget> CampaignTargets { get; }

    DbSet<PublisherSettings> Settings { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates short-lived contexts. Blazor Server circuits are long-lived and concurrent, so services
/// create a context per operation instead of sharing a scoped one.
/// </summary>
public interface IApplicationDbContextFactory
{
    Task<IApplicationDbContext> CreateAsync(CancellationToken cancellationToken = default);
}
