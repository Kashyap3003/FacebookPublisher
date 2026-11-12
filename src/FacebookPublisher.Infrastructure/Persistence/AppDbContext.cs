using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Domain.Common;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FacebookPublisher.Infrastructure.Persistence;

/// <summary>
/// Application database (ASP.NET Core Identity + Group Publisher data).
/// <para>
/// Each instance is restricted to one owner: global query filters hide every other user's groups, content,
/// attachments, campaigns, campaign groups and settings, and <see cref="SaveChanges(bool)"/> stamps new records with
/// the owner and rejects writes to another owner's records. A context that was never scoped (for example the one
/// ASP.NET Core Identity uses) sees no application data at all — isolation fails closed. Only the system factory can
/// create an unscoped context, for lookups such as "which user owns this campaign".
/// </para>
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IApplicationDbContext
{
    // Read by the global query filters below; EF Core evaluates them per context instance.
    private Guid _ownerId = Guid.Empty;
    private bool _unscoped;

    public Guid? OwnerId => _unscoped || _ownerId == Guid.Empty ? null : _ownerId;

    public DbSet<FacebookGroup> Groups => Set<FacebookGroup>();

    public DbSet<ContentPost> Contents => Set<ContentPost>();

    public DbSet<ContentAttachment> Attachments => Set<ContentAttachment>();

    public DbSet<Campaign> Campaigns => Set<Campaign>();

    public DbSet<CampaignTarget> CampaignTargets => Set<CampaignTarget>();

    public DbSet<PublisherSettings> Settings => Set<PublisherSettings>();

    internal AppDbContext ScopeTo(Guid ownerId)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An owner id is required.", nameof(ownerId));
        }
        _ownerId = ownerId;
        _unscoped = false;
        return this;
    }

    internal AppDbContext AsUnscoped()
    {
        _ownerId = Guid.Empty;
        _unscoped = true;
        return this;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        modelBuilder.Entity<FacebookGroup>().HasQueryFilter(g => _unscoped || g.OwnerId == _ownerId);
        modelBuilder.Entity<ContentPost>().HasQueryFilter(c => _unscoped || c.OwnerId == _ownerId);
        modelBuilder.Entity<ContentAttachment>().HasQueryFilter(a => _unscoped || a.ContentPost!.OwnerId == _ownerId);
        modelBuilder.Entity<Campaign>().HasQueryFilter(c => _unscoped || c.OwnerId == _ownerId);
        modelBuilder.Entity<CampaignTarget>().HasQueryFilter(t => _unscoped || t.Campaign!.OwnerId == _ownerId);
        modelBuilder.Entity<PublisherSettings>().HasQueryFilter(s => _unscoped || s.OwnerId == _ownerId);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored as readable strings, which keeps the database and exported data self-explanatory.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceOwnership();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceOwnership();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Stamps the owner on new records and refuses any write that touches another owner's data.</summary>
    private void EnforceOwnership()
    {
        if (_unscoped)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries<IOwnedEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (_ownerId == Guid.Empty)
                    {
                        throw new UnauthorizedAccessException("Cannot create application data without a signed-in owner.");
                    }
                    if (entry.Entity.OwnerId == Guid.Empty)
                    {
                        entry.Entity.OwnerId = _ownerId;
                    }
                    else if (entry.Entity.OwnerId != _ownerId)
                    {
                        throw new UnauthorizedAccessException("Cannot create data on behalf of another user.");
                    }
                    break;
                case EntityState.Modified:
                case EntityState.Deleted:
                    var original = entry.Property(e => e.OwnerId).OriginalValue;
                    if (original != _ownerId || entry.Entity.OwnerId != _ownerId)
                    {
                        throw new UnauthorizedAccessException("Cannot modify another user's data.");
                    }
                    break;
            }
        }

        // Children inherit ownership: a new attachment or campaign group must belong to a parent of this owner.
        foreach (var entry in ChangeTracker.Entries<ContentAttachment>().Where(e => e.State == EntityState.Added))
        {
            var parent = entry.Entity.ContentPost ?? Contents.Local.FirstOrDefault(c => c.Id == entry.Entity.ContentPostId);
            if (parent is null || parent.OwnerId != _ownerId)
            {
                throw new UnauthorizedAccessException("Attachments can only be added to your own content.");
            }
        }
        foreach (var entry in ChangeTracker.Entries<CampaignTarget>().Where(e => e.State == EntityState.Added))
        {
            var parent = entry.Entity.Campaign ?? Campaigns.Local.FirstOrDefault(c => c.Id == entry.Entity.CampaignId);
            if (parent is null || parent.OwnerId != _ownerId)
            {
                throw new UnauthorizedAccessException("Groups can only be added to your own campaigns.");
            }
        }
    }
}

/// <summary>Creates owner-scoped or (for system lookups only) unscoped contexts.</summary>
public sealed class AppDbContextFactory(IDbContextFactory<AppDbContext> inner) : ISystemDbContextFactory
{
    public async Task<IApplicationDbContext> CreateForOwnerAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        var context = await inner.CreateDbContextAsync(cancellationToken);
        return context.ScopeTo(ownerId);
    }

    public async Task<IApplicationDbContext> CreateUnscopedAsync(CancellationToken cancellationToken = default)
    {
        var context = await inner.CreateDbContextAsync(cancellationToken);
        return context.AsUnscoped();
    }
}

/// <summary>Contexts for user-facing services, scoped to the signed-in user resolved on the server.</summary>
public sealed class CurrentUserDbContextFactory(ISystemDbContextFactory system, ICurrentUser currentUser) : IApplicationDbContextFactory
{
    public async Task<IApplicationDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
        await system.CreateForOwnerAsync(await currentUser.GetUserIdAsync(cancellationToken), cancellationToken);
}
