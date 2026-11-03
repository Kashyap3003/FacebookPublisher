using FacebookPublisher.Application.Settings;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FacebookPublisher.Infrastructure.Persistence;

public sealed class DatabaseInitializer(IDbContextFactory<AppDbContext> dbFactory, ILogger<DatabaseInitializer> logger)
{
    /// <summary>
    /// Applies migrations, seeds default settings and recovers campaigns interrupted by a crash or restart.
    /// Returns the ids of campaigns that should be re-queued.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);

        if (!await db.Settings.AnyAsync(ct))
        {
            db.Settings.Add(SettingsService.CreateDefaults());
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Default settings created");
        }

        var interrupted = await db.Campaigns
            .Include(c => c.Targets)
            .Where(c => c.Status == CampaignStatus.Running)
            .ToListAsync(ct);

        foreach (var campaign in interrupted)
        {
            foreach (var target in campaign.Targets)
            {
                switch (target.Status)
                {
                    // The Post button may or may not have been clicked: never retry automatically.
                    case TargetStatus.Publishing:
                    case TargetStatus.AwaitingApproval:
                        target.SetStatus(TargetStatus.Failed,
                            "The application stopped while this post was being published. Check the group on Facebook to confirm whether it was created.");
                        break;
                    case TargetStatus.CheckingMembership:
                    case TargetStatus.Eligible:
                        target.SetStatus(TargetStatus.Pending);
                        break;
                }
            }
            campaign.Finish(CampaignStatus.Halted, "The application stopped while this campaign was running. Resume it to continue with the remaining groups.");
            logger.LogWarning("Campaign {CampaignId} was interrupted and has been marked as halted", campaign.Id);
        }

        if (interrupted.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return await db.Campaigns
            .Where(c => c.Status == CampaignStatus.Queued)
            .OrderBy(c => c.CreatedAtUtc)
            .Select(c => c.Id)
            .ToListAsync(ct);
    }
}
