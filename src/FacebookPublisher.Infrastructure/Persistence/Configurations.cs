using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FacebookPublisher.Infrastructure.Persistence;

internal sealed class FacebookGroupConfiguration : IEntityTypeConfiguration<FacebookGroup>
{
    public void Configure(EntityTypeBuilder<FacebookGroup> builder)
    {
        builder.ToTable("FacebookGroups");
        builder.Property(g => g.Name).HasMaxLength(200).IsRequired();
        builder.Property(g => g.Url).HasMaxLength(300).IsRequired();
        builder.Property(g => g.Notes).HasMaxLength(1000);
        builder.HasIndex(g => g.Url).IsUnique();
        builder.HasIndex(g => g.Name);
    }
}

internal sealed class ContentPostConfiguration : IEntityTypeConfiguration<ContentPost>
{
    public void Configure(EntityTypeBuilder<ContentPost> builder)
    {
        builder.ToTable("ContentPosts");
        builder.Property(c => c.Title).HasMaxLength(200).IsRequired();
        builder.Property(c => c.Text).HasMaxLength(20_000);
        builder.Property(c => c.ContentHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(c => c.ContentHash);
        builder.HasMany(c => c.Attachments)
            .WithOne(a => a.ContentPost)
            .HasForeignKey(a => a.ContentPostId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ContentAttachmentConfiguration : IEntityTypeConfiguration<ContentAttachment>
{
    public void Configure(EntityTypeBuilder<ContentAttachment> builder)
    {
        builder.ToTable("ContentAttachments");
        builder.Property(a => a.OriginalFileName).HasMaxLength(260).IsRequired();
        builder.Property(a => a.StoredFileName).HasMaxLength(100).IsRequired();
        builder.Property(a => a.MimeType).HasMaxLength(100).IsRequired();
        builder.Property(a => a.Sha256).HasMaxLength(64).IsRequired();
    }
}

internal sealed class CampaignConfiguration : IEntityTypeConfiguration<Campaign>
{
    public void Configure(EntityTypeBuilder<Campaign> builder)
    {
        builder.ToTable("Campaigns");
        builder.Property(c => c.Name).HasMaxLength(200).IsRequired();
        builder.Property(c => c.StatusReason).HasMaxLength(2000);
        builder.Property(c => c.RowVersion).IsRowVersion();
        builder.Ignore(c => c.IsActive);
        builder.Ignore(c => c.CanBeResumed);
        builder.Ignore(c => c.CanBeStarted);
        builder.Ignore(c => c.CanBeStopped);
        // Configuration snapshot stored with the campaign (audit + consistent execution).
        builder.OwnsOne(c => c.Pacing, PacingMapping.Configure);
        builder.Navigation(c => c.Pacing).IsRequired();
        builder.HasIndex(c => c.Status);
        builder.HasIndex(c => c.CreatedAtUtc);
        builder.HasOne(c => c.ContentPost)
            .WithMany(p => p.Campaigns)
            .HasForeignKey(c => c.ContentPostId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(c => c.Targets)
            .WithOne(t => t.Campaign)
            .HasForeignKey(t => t.CampaignId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CampaignTargetConfiguration : IEntityTypeConfiguration<CampaignTarget>
{
    public void Configure(EntityTypeBuilder<CampaignTarget> builder)
    {
        builder.ToTable("CampaignTargets");
        builder.Property(t => t.GroupName).HasMaxLength(200).IsRequired();
        builder.Property(t => t.GroupUrl).HasMaxLength(300).IsRequired();
        builder.Property(t => t.ErrorMessage).HasMaxLength(2000);
        builder.Ignore(t => t.IsProcessable);
        builder.Ignore(t => t.IsTerminal);

        // One row per group per campaign: the database itself refuses duplicate targets.
        builder.HasIndex(t => new { t.CampaignId, t.FacebookGroupId }).IsUnique();
        builder.HasIndex(t => new { t.GroupUrl, t.Status, t.PublishedAtUtc });
        builder.HasIndex(t => t.UpdatedAtUtc);
        builder.HasIndex(t => new { t.CampaignId, t.Status });

        builder.HasOne(t => t.FacebookGroup)
            .WithMany(g => g.CampaignTargets)
            .HasForeignKey(t => t.FacebookGroupId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class PublisherSettingsConfiguration : IEntityTypeConfiguration<PublisherSettings>
{
    public void Configure(EntityTypeBuilder<PublisherSettings> builder)
    {
        builder.ToTable("PublisherSettings");
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.BrowserProfilePath).HasMaxLength(500).IsRequired();
        builder.Property(s => s.BrowserChannel).HasMaxLength(40);
        builder.OwnsOne(s => s.Pacing, PacingMapping.Configure);
        builder.Navigation(s => s.Pacing).IsRequired();
    }
}

/// <summary>
/// Maps <see cref="BatchPacingConfig"/> as owned columns (Pacing_MinBatchSize, Pacing_PostDelay_Min, …) on both the
/// settings row and each campaign, so there is one model for defaults and snapshots and no extra tables.
/// </summary>
internal static class PacingMapping
{
    public static void Configure<TOwner>(OwnedNavigationBuilder<TOwner, BatchPacingConfig> pacing) where TOwner : class
    {
        pacing.Property(p => p.MinBatchSize).HasDefaultValue(5);
        pacing.Property(p => p.MaxBatchSize).HasDefaultValue(9);
        pacing.Property(p => p.ExtendedDelayAfterBatches).HasDefaultValue(5);
        ConfigureRange(pacing, p => p.PostDelay, 10, 30, DelayUnit.Seconds);
        ConfigureRange(pacing, p => p.BatchDelay, 15, 30, DelayUnit.Minutes);
        ConfigureRange(pacing, p => p.ExtendedDelay, 3, 5, DelayUnit.Hours);
    }

    private static void ConfigureRange<TOwner>(
        OwnedNavigationBuilder<TOwner, BatchPacingConfig> pacing,
        System.Linq.Expressions.Expression<Func<BatchPacingConfig, DelayRange?>> navigation,
        int min,
        int max,
        DelayUnit unit) where TOwner : class
    {
        pacing.OwnsOne(navigation, range =>
        {
            range.Property(r => r.Min).HasDefaultValue(min);
            range.Property(r => r.Max).HasDefaultValue(max);
            range.Property(r => r.Unit).HasDefaultValue(unit);
        });
        pacing.Navigation(navigation).IsRequired();
    }
}
