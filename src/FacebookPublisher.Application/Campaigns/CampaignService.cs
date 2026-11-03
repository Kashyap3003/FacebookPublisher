using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Common;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FacebookPublisher.Application.Campaigns;

public sealed class CampaignService(
    IApplicationDbContextFactory dbFactory,
    ICampaignQueue queue,
    ICampaignExecutionTracker tracker,
    ICampaignApprovalGate approvalGate,
    ICampaignProgressNotifier notifier,
    ILogger<CampaignService> logger)
{
    public const int MinimumDelaySeconds = 5;

    public async Task<Guid> CreateAndStartAsync(CreateCampaignRequest request, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == PublisherSettings.SingletonId, ct)
            ?? new PublisherSettings();

        var groupIds = request.GroupIds.Distinct().ToList();
        var errors = new List<string>();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 200)
        {
            errors.Add("Campaign name is required and must be at most 200 characters.");
        }
        if (groupIds.Count == 0)
        {
            errors.Add("Select at least one group.");
        }
        if (groupIds.Count > settings.MaxGroupsPerCampaign)
        {
            errors.Add($"A campaign can target at most {settings.MaxGroupsPerCampaign} groups (see Settings).");
        }
        if (request.MinDelaySeconds < MinimumDelaySeconds)
        {
            errors.Add($"Minimum delay must be at least {MinimumDelaySeconds} seconds.");
        }
        if (request.MaxDelaySeconds < request.MinDelaySeconds || request.MaxDelaySeconds > 3600)
        {
            errors.Add("Maximum delay must be between the minimum delay and 3600 seconds.");
        }

        var content = await db.Contents.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ContentId, ct);
        if (content is null)
        {
            errors.Add("Select the content to publish.");
        }

        var groups = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToListAsync(ct);
        if (groups.Count != groupIds.Count)
        {
            errors.Add("One or more selected groups no longer exist. Refresh the page.");
        }

        // Guards against double-clicks or a second tab re-submitting the same campaign.
        if (content is not null && await db.Campaigns.AnyAsync(
                c => c.ContentPostId == content.Id &&
                     (c.Status == CampaignStatus.Queued || c.Status == CampaignStatus.Running), ct))
        {
            errors.Add("This content already has a queued or running campaign.");
        }

        if (errors.Count > 0)
        {
            throw new AppValidationException("The campaign cannot be started.", errors);
        }

        var campaign = new Campaign
        {
            Name = name,
            ContentPostId = request.ContentId,
            MinDelaySeconds = request.MinDelaySeconds,
            MaxDelaySeconds = request.MaxDelaySeconds,
            RequireApproval = request.RequireApproval,
            Status = CampaignStatus.Queued
        };

        var order = 0;
        foreach (var group in groupIds.Select(id => groups.First(g => g.Id == id)))
        {
            campaign.Targets.Add(new CampaignTarget
            {
                FacebookGroupId = group.Id,
                GroupName = group.Name,
                GroupUrl = group.Url,
                Order = order++,
                MembershipStatus = group.LastMembershipStatus
            });
        }

        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(campaign.Id, ct);

        logger.LogInformation("Campaign {CampaignId} '{Name}' queued with {TargetCount} group(s)", campaign.Id, campaign.Name, campaign.Targets.Count);
        return campaign.Id;
    }

    public async Task CancelAsync(Guid campaignId, CancellationToken ct = default)
    {
        if (tracker.RequestCancel(campaignId))
        {
            logger.LogInformation("Cancellation requested for running campaign {CampaignId}", campaignId);
            return;
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var campaign = await db.Campaigns.Include(c => c.Targets).Include(c => c.ContentPost)
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct)
            ?? throw new NotFoundException("Campaign", campaignId);

        if (campaign.Status is not (CampaignStatus.Queued or CampaignStatus.Halted))
        {
            throw new AppValidationException($"A campaign in status '{campaign.Status.ToDisplay()}' cannot be cancelled.");
        }

        foreach (var target in campaign.Targets.Where(t => t.IsProcessable))
        {
            target.SetStatus(TargetStatus.Cancelled, "Campaign was cancelled before this group was processed.");
        }
        campaign.Finish(CampaignStatus.Cancelled, "Cancelled by user.");

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The worker picked it up in the meantime; route the request to it instead.
            tracker.RequestCancel(campaignId);
            return;
        }

        logger.LogInformation("Campaign {CampaignId} cancelled before execution", campaignId);
        await notifier.CampaignChangedAsync(CampaignProgressDto.From(campaign), ct);
    }

    /// <summary>Re-queues a halted campaign. Already-processed groups are never re-published.</summary>
    public async Task ResumeAsync(Guid campaignId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var campaign = await db.Campaigns.Include(c => c.Targets).Include(c => c.ContentPost)
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct)
            ?? throw new NotFoundException("Campaign", campaignId);

        if (!campaign.CanBeResumed)
        {
            throw new AppValidationException("Only halted campaigns with unprocessed groups can be resumed.");
        }

        campaign.Status = CampaignStatus.Queued;
        campaign.StatusReason = null;
        campaign.CompletedAtUtc = null;
        campaign.Touch();
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(campaign.Id, ct);

        logger.LogInformation("Campaign {CampaignId} resumed", campaignId);
        await notifier.CampaignChangedAsync(CampaignProgressDto.From(campaign), ct);
    }

    public bool SubmitApproval(Guid targetId, bool approve)
    {
        var resolved = approvalGate.Resolve(targetId, approve);
        logger.LogInformation("Approval for target {TargetId}: {Decision} (resolved: {Resolved})", targetId, approve ? "approved" : "rejected", resolved);
        return resolved;
    }

    public bool IsAwaitingApproval(Guid targetId) => approvalGate.IsAwaiting(targetId);

    public async Task<CampaignProgressDto?> GetProgressAsync(Guid campaignId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var campaign = await db.Campaigns.AsNoTracking()
            .Include(c => c.ContentPost)
            .Include(c => c.Targets)
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct);
        return campaign is null ? null : CampaignProgressDto.From(campaign);
    }

    public async Task<Guid?> GetMostRecentActiveOrLatestIdAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var active = await db.Campaigns.AsNoTracking()
            .Where(c => c.Status == CampaignStatus.Running || c.Status == CampaignStatus.Queued)
            .OrderBy(c => c.CreatedAtUtc)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
        return active ?? await db.Campaigns.AsNoTracking()
            .OrderByDescending(c => c.CreatedAtUtc)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<CampaignSummaryDto>> ListAsync(int take = 200, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var campaigns = await db.Campaigns.AsNoTracking()
            .OrderByDescending(c => c.CreatedAtUtc)
            .Take(take)
            .Select(c => new
            {
                c.Id,
                c.Name,
                ContentTitle = c.ContentPost!.Title,
                c.Status,
                c.StatusReason,
                c.CreatedAtUtc,
                c.StartedAtUtc,
                c.CompletedAtUtc,
                Statuses = c.Targets.Select(t => t.Status).ToList()
            })
            .ToListAsync(ct);

        return campaigns.Select(c => new CampaignSummaryDto(
                c.Id, c.Name, c.ContentTitle, c.Status, c.StatusReason, c.CreatedAtUtc, c.StartedAtUtc, c.CompletedAtUtc,
                CampaignCountsDto.From(c.Statuses)))
            .ToList();
    }
}
