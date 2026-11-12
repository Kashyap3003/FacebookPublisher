using FacebookPublisher.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FacebookPublisher.Application.Abstractions;

/// <summary>
/// Application data access. Every context is scoped to one owner: queries only return that owner's records and
/// writes to another owner's records are rejected. An unscoped context (see <see cref="ISystemDbContextFactory"/>)
/// exists only for system work such as finding a campaign's owner.
/// </summary>
public interface IApplicationDbContext : IAsyncDisposable, IDisposable
{
    /// <summary>The owner this context is restricted to; null for an unscoped system context.</summary>
    Guid? OwnerId { get; }

    DbSet<FacebookGroup> Groups { get; }

    DbSet<ContentPost> Contents { get; }

    DbSet<ContentAttachment> Attachments { get; }

    DbSet<Campaign> Campaigns { get; }

    DbSet<CampaignTarget> CampaignTargets { get; }

    DbSet<PublisherSettings> Settings { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates contexts scoped to the <b>signed-in user</b> (resolved server-side from the authenticated identity).
/// Used by every user-facing service. Blazor Server circuits are long-lived and concurrent, so services create a
/// context per operation instead of sharing one.
/// </summary>
public interface IApplicationDbContextFactory
{
    Task<IApplicationDbContext> CreateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Contexts for background work that has no signed-in user (campaign execution, browser sessions).
/// The owner always comes from persisted data such as <see cref="Campaign.OwnerId"/>.
/// </summary>
public interface ISystemDbContextFactory
{
    Task<IApplicationDbContext> CreateForOwnerAsync(Guid ownerId, CancellationToken cancellationToken = default);

    /// <summary>Unrestricted context. Use only for system lookups (e.g. which user owns a campaign).</summary>
    Task<IApplicationDbContext> CreateUnscopedAsync(CancellationToken cancellationToken = default);
}

public static class ApplicationDbContextExtensions
{
    public static Guid RequireOwnerId(this IApplicationDbContext db) =>
        db.OwnerId ?? throw new InvalidOperationException("This operation requires a context scoped to an application user.");
}
