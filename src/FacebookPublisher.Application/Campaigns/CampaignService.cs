using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Common;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FacebookPublisher.Application.Campaigns;

/// <summary>
/// User-initiated campaign operations. Execution only happens when the user clicks Start or Resume; both put the
/// campaign on the existing queue and the runner then processes only the groups that are still Pending in the database.
/// </summary>
public sealed class CampaignService(
    IApplicationDbContextFactory dbFactory,
    ICampaignQueue queue,
    ICampaignExecutionTracker tracker,
    ICampaignApprovalGate approvalGate,
    ICampaignProgressNotifier notifier,
    ILogger<CampaignService> logger)
{
    /// <summary>A Queued/Running campaign whose heartbeat is older than this (and owned by another process) is orphaned.</summary>
    public static readonly TimeSpan OrphanAfter = TimeSpan.FromMinutes(3);

    public const string InterruptedReason =
        "The application stopped while this campaign was running. Resume it to continue with the remaining groups.";

    public const string OutcomeUnknownReason =
        "The application stopped while this post was being published, so it is not known whether Facebook accepted it. " +
        "Check the group on Facebook, then confirm below. It is never retried automatically.";

    /// <summary>Creates a campaign with a snapshot of the given batch/delay settings and optionally starts it.</summary>
    public async Task<Guid> CreateAsync(CreateCampaignRequest request, bool start, CancellationToken ct = default)
    {
        await ReconcileOrphanedCampaignsAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct)
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
        errors.AddRange(BatchPacingValidator.Validate(request.Pacing).Select(e => e.Message));

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
        if (start && content is not null && await db.Campaigns.AnyAsync(
                c => c.ContentPostId == content.Id &&
                     (c.Status == CampaignStatus.Queued || c.Status == CampaignStatus.Running), ct))
        {
            errors.Add("This content already has a queued or running campaign.");
        }

        if (errors.Count > 0)
        {
            throw new AppValidationException("The campaign cannot be created.", errors);
        }

        var campaign = new Campaign
        {
            Name = name,
            ContentPostId = request.ContentId,
            Pacing = request.Pacing.Clone(),
            RequireApproval = request.RequireApproval,
            Status = CampaignStatus.NotStarted
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
        campaign.RemainingGroupCount = campaign.Targets.Count;

        if (start)
        {
            MarkQueued(campaign);
        }

        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct);
        if (start)
        {
            await queue.EnqueueAsync(campaign.Id, ct);
        }

        logger.LogInformation("Campaign {CampaignId} '{Name}' created with {TargetCount} group(s); started: {Started}",
            campaign.Id, campaign.Name, campaign.Targets.Count, start);
        return campaign.Id;
    }

    /// <summary>Starts a campaign that was saved without starting.</summary>
    public Task StartAsync(Guid campaignId, CancellationToken ct = default) =>
        QueueForExecutionAsync(campaignId, resume: false, ct);

    /// <summary>
    /// Continues a stopped, interrupted, halted or finished campaign with its remaining Pending groups. Groups already
    /// published or with an unknown outcome are never processed again; the original batch/delay snapshot is used.
    /// </summary>
    public Task ResumeAsync(Guid campaignId, CancellationToken ct = default) =>
        QueueForExecutionAsync(campaignId, resume: true, ct);

    private async Task QueueForExecutionAsync(Guid campaignId, bool resume, CancellationToken ct)
    {
        await ReconcileOrphanedCampaignsAsync(ct);

        await using var db = await dbFactory.CreateAsync(ct);
        var campaign = await db.Campaigns.Include(c => c.Targets).Include(c => c.ContentPost)
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct)
            ?? throw new NotFoundException("Campaign", campaignId);

        if (campaign.IsActive)
        {
            throw new AppValidationException("This campaign is already queued or running.");
        }

        if (resume)
        {
            ConvertLegacyCancelledTargets(campaign);
            if (!campaign.CanBeResumed)
            {
                throw new AppValidationException(campaign.Status == CampaignStatus.NotStarted
                    ? "This campaign has not been started yet. Use Start."
                    : "There are no Pending groups left to process in this campaign.");
            }
        }
        else if (!campaign.CanBeStarted)
        {
            throw new AppValidationException("This campaign has already been started. Use Resume to continue with its remaining groups.");
        }

        if (await db.Campaigns.AnyAsync(c => c.Id != campaign.Id && c.ContentPostId == campaign.ContentPostId &&
                                             (c.Status == CampaignStatus.Queued || c.Status == CampaignStatus.Running), ct))
        {
            throw new AppValidationException("Another campaign with the same content is queued or running.");
        }

        MarkQueued(campaign);
        campaign.ReconcileProgress();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request (second click, second tab) changed the campaign first: never execute it twice.
            throw new AppValidationException("This campaign was just started or changed by another request. Refresh the page.");
        }

        await queue.EnqueueAsync(campaign.Id, ct);
        logger.LogInformation("Campaign {CampaignId} queued ({Mode}) with {Remaining} remaining group(s)",
            campaignId, resume ? "resume" : "start", campaign.RemainingGroupCount);
        await notifier.CampaignChangedAsync(CampaignProgressDto.From(campaign), ct);
    }

    /// <summary>Stops a queued or running campaign. Remaining groups stay Pending so it can be resumed later.</summary>
    public async Task StopAsync(Guid campaignId, CancellationToken ct = default)
    {
        // Ownership first: the campaign must be visible in the signed-in user's scope before anything is cancelled.
        await using (var check = await dbFactory.CreateAsync(ct))
        {
            if (!await check.Campaigns.AnyAsync(c => c.Id == campaignId, ct))
            {
                throw new NotFoundException("Campaign", campaignId);
            }
        }

        if (tracker.RequestCancel(campaignId))
        {
            logger.LogInformation("Stop requested for running campaign {CampaignId}", campaignId);
            return;
        }

        await ReconcileOrphanedCampaignsAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var campaign = await db.Campaigns.Include(c => c.Targets).Include(c => c.ContentPost)
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct)
            ?? throw new NotFoundException("Campaign", campaignId);

        if (campaign.Status != CampaignStatus.Queued)
        {
            throw new AppValidationException($"A campaign in status '{campaign.Status.ToDisplay()}' is not running.");
        }

        campaign.Finish(CampaignStatus.Stopped, "Stopped by user before it started running. Resume to continue.");
        campaign.ReconcileProgress();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The worker claimed it in the meantime; route the request to the running execution instead.
            tracker.RequestCancel(campaignId);
            return;
        }

        logger.LogInformation("Queued campaign {CampaignId} stopped before execution", campaignId);
        await notifier.CampaignChangedAsync(CampaignProgressDto.From(campaign), ct);
    }

    /// <summary>
    /// Manual verification of a group whose outcome is unknown. Confirmed → Published (excluded from every resume);
    /// not published → Pending (processed by the next Start/Resume).
    /// </summary>
    public async Task ResolveUnknownOutcomeAsync(Guid targetId, bool wasPublished, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var target = await db.CampaignTargets.FirstOrDefaultAsync(t => t.Id == targetId, ct)
            ?? throw new NotFoundException("Group", targetId);
        var campaign = await db.Campaigns.Include(c => c.Targets).Include(c => c.ContentPost)
            .FirstAsync(c => c.Id == target.CampaignId, ct);

        if (target.Status != TargetStatus.OutcomeUnknown)
        {
            throw new AppValidationException("Only groups with an unknown outcome can be verified.");
        }
        if (campaign.IsActive)
        {
            throw new AppValidationException("Stop the campaign before verifying groups.");
        }

        if (wasPublished)
        {
            target.SetStatus(TargetStatus.Published, "Marked as published after manual verification.");
        }
        else
        {
            target.BatchNumber = null;
            target.SetStatus(TargetStatus.Pending, "Returned to Pending after manual verification (not published).");
        }

        campaign.ReconcileProgress();
        if (campaign.Status is CampaignStatus.Completed or CampaignStatus.CompletedWithErrors && campaign.RemainingGroupCount == 0)
        {
            campaign.Status = campaign.FinalStatus();
        }
        campaign.Touch(); // bumps the row version so a concurrent Resume/Start cannot slip in unnoticed
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AppValidationException("The campaign changed while you were verifying. Refresh the page.");
        }

        logger.LogInformation("Target {TargetId} manually verified as {Result}", targetId, wasPublished ? "published" : "not published");
        await notifier.CampaignChangedAsync(CampaignProgressDto.From(campaign), ct);
    }

    /// <summary>
    /// Finds campaigns left Queued/Running by a previous application process (shutdown or crash) and marks them
    /// Interrupted. Groups caught mid-publish become Outcome Unknown; groups mid-check go back to Pending.
    /// Called when the user opens the app's pages or starts/resumes a campaign — never by a background job.
    /// </summary>
    public async Task<int> ReconcileOrphanedCampaignsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var me = tracker.InstanceId;
        var staleBefore = DateTime.UtcNow - OrphanAfter;
        var processStart = tracker.StartedAtUtc;

        var orphans = await db.Campaigns
            .Include(c => c.Targets)
            .Include(c => c.ContentPost)
            .Where(c => (c.Status == CampaignStatus.Queued || c.Status == CampaignStatus.Running)
                        && (c.ExecutionOwnerId == null || c.ExecutionOwnerId != me)
                        && (c.LastExecutionAtUtc == null || c.LastExecutionAtUtc < staleBefore || c.LastExecutionAtUtc < processStart))
            .ToListAsync(ct);

        foreach (var campaign in orphans)
        {
            foreach (var target in campaign.Targets)
            {
                switch (target.Status)
                {
                    case TargetStatus.Publishing:
                    case TargetStatus.AwaitingApproval:
                        target.SetStatus(TargetStatus.OutcomeUnknown, OutcomeUnknownReason);
                        break;
                    case TargetStatus.CheckingMembership:
                    case TargetStatus.Eligible:
                        target.BatchNumber = null;
                        target.SetStatus(TargetStatus.Pending);
                        break;
                }
            }
            campaign.Finish(CampaignStatus.Interrupted, InterruptedReason);
            campaign.ReconcileProgress();
        }

        if (orphans.Count == 0)
        {
            return 0;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Orphaned campaign recovery skipped: a campaign changed concurrently");
            return 0;
        }

        foreach (var campaign in orphans)
        {
            logger.LogWarning("Campaign {CampaignId} was interrupted by an application stop; marked Interrupted", campaign.Id);
            await notifier.CampaignChangedAsync(CampaignProgressDto.From(campaign), ct);
        }
        return orphans.Count;
    }

    /// <summary>Approves or rejects a prepared post. Only the owner of the campaign can decide.</summary>
    public async Task<bool> SubmitApprovalAsync(Guid targetId, bool approve, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        if (!await db.CampaignTargets.AnyAsync(t => t.Id == targetId, ct))
        {
            throw new NotFoundException("Group", targetId);
        }

        var resolved = approvalGate.Resolve(targetId, approve);
        logger.LogInformation("Approval for target {TargetId}: {Decision} (resolved: {Resolved})", targetId, approve ? "approved" : "rejected", resolved);
        return resolved;
    }

    /// <summary>True while one of the signed-in user's campaigns is executing (their browser profile is in use).</summary>
    public async Task<bool> HasRunningCampaignAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return tracker.IsOwnerBusy(db.RequireOwnerId());
    }

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

    private void MarkQueued(Campaign campaign)
    {
        campaign.Status = CampaignStatus.Queued;
        campaign.StatusReason = null;
        campaign.CompletedAtUtc = null;
        campaign.ExecutionOwnerId = tracker.InstanceId;
        campaign.LastExecutionAtUtc = DateTime.UtcNow;
        campaign.Touch();
    }

    /// <summary>
    /// Campaigns cancelled before Stop/Resume existed marked their unprocessed groups Cancelled. Those never reached
    /// Facebook and become Pending again; a group cancelled after a publish attempt is treated as Outcome Unknown.
    /// </summary>
    private static void ConvertLegacyCancelledTargets(Campaign campaign)
    {
        foreach (var target in campaign.Targets.Where(t => t.Status == TargetStatus.Cancelled))
        {
            if (target.PublishAttempts > 0)
            {
                target.SetStatus(TargetStatus.OutcomeUnknown, OutcomeUnknownReason);
            }
            else
            {
                target.BatchNumber = null;
                target.SetStatus(TargetStatus.Pending);
            }
        }
    }
}
