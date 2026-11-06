using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Automation;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FacebookPublisher.Application.Campaigns;

/// <summary>
/// Executes one campaign (the application's only execution engine). Groups are processed in randomly sized batches
/// with random delays between posts, between batches, and an extended break after every N completed batches.
/// <para>
/// The database is the source of truth: only Pending groups are processed, every group's result and the batch
/// counters are saved after each step, and the pending delay is persisted so a stopped campaign can be resumed
/// without skipping or repeating work. A failing group never stops the others; a Facebook restriction, checkpoint or
/// expired session halts the whole campaign.
/// </para>
/// </summary>
public sealed class CampaignRunner(
    IApplicationDbContextFactory dbFactory,
    IFacebookAutomation automation,
    IFileStorage storage,
    ICampaignApprovalGate approvalGate,
    ICampaignProgressNotifier notifier,
    IBatchDelayService batchDelays,
    IAsyncDelay asyncDelay,
    ICampaignExecutionTracker tracker,
    ILogger<CampaignRunner> logger)
{
    /// <summary>Long waits are split into chunks so the heartbeat (execution lease) stays fresh.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

    public async Task RunAsync(Guid campaignId, CancellationToken cancellationToken, CancellationToken hostStoppingToken)
    {
        await using var db = await dbFactory.CreateAsync(CancellationToken.None);

        var campaign = await db.Campaigns
            .Include(c => c.ContentPost!).ThenInclude(p => p.Attachments)
            .Include(c => c.Targets)
            .FirstOrDefaultAsync(c => c.Id == campaignId, CancellationToken.None);

        if (campaign is null || campaign.Status != CampaignStatus.Queued)
        {
            logger.LogInformation("Campaign {CampaignId} is not queued (status: {Status}); skipping", campaignId, campaign?.Status);
            return;
        }
        if (campaign.ExecutionOwnerId is { } owner && owner != tracker.InstanceId)
        {
            logger.LogWarning("Campaign {CampaignId} is leased by another application instance; skipping", campaignId);
            return;
        }

        // Claim the execution lease. The row version makes this atomic: a concurrent claim or Stop makes it fail.
        campaign.MarkRunning(tracker.InstanceId);
        campaign.ReconcileProgress();
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Campaign {CampaignId} changed before it could start (stopped or already claimed); skipping", campaignId);
            return;
        }

        using var _ = logger.BeginScope(new Dictionary<string, object> { ["CampaignId"] = campaignId });
        var pacing = campaign.Pacing;
        var remainingAtStart = campaign.Targets.Count(t => t.IsProcessable);
        logger.LogInformation("Campaign '{Name}' started with {Remaining} of {Total} group(s) remaining", campaign.Name, remainingAtStart, campaign.Targets.Count);
        await NotifyCampaignAsync(campaign);

        var resumed = campaign.Targets.Any(t => !t.IsProcessable);
        await EmitAsync(campaign, CampaignMessageLevel.Info,
            (resumed
                ? $"Campaign resumed: {remainingAtStart} remaining group(s); {campaign.Targets.Count - remainingAtStart} already processed and skipped. "
                : $"Campaign started: {remainingAtStart} group(s). ")
            + DurationText.Summary(pacing) + ". "
            + (campaign.RequireApproval ? "Each post is reviewed before publishing." : "Fully automatic posting."));

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == PublisherSettings.SingletonId, CancellationToken.None)
            ?? new PublisherSettings();

        try
        {
            var pacingErrors = BatchPacingValidator.Validate(pacing);
            if (pacingErrors.Count > 0)
            {
                throw new InvalidOperationException("Invalid batch/delay configuration: " + string.Join(" ", pacingErrors.Select(e => e.Message)));
            }

            var request = BuildRequest(campaign.ContentPost!, settings);

            await EmitAsync(campaign, CampaignMessageLevel.Step, "Starting the browser and checking the Facebook session…");
            var session = await automation.CheckSessionAsync(cancellationToken);
            if (!session.IsLoggedIn)
            {
                throw new AutomationHaltedException(HaltReason.SessionExpired, session.Message);
            }
            await EmitAsync(campaign, CampaignMessageLevel.Success, "Facebook session is active.");

            var processedThisRun = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A batch whose last group was saved but whose rollover was not (crash between two saves).
                if (campaign.CurrentBatchSize > 0 && campaign.CurrentBatchProcessed >= campaign.CurrentBatchSize
                    && campaign.Targets.Any(t => t.IsProcessable) && campaign.CurrentDelayKind == DelayKind.None)
                {
                    await CompleteBatchAndScheduleNextAsync(campaign, pacing);
                    await SaveCampaignAsync(db, campaign);
                }

                // Resume policy: finish the remaining part of a delay that was interrupted; skip it if already elapsed.
                await WaitForScheduledDelayAsync(db, campaign, cancellationToken);

                var next = campaign.Targets.Where(t => t.IsProcessable).OrderBy(t => t.Order).FirstOrDefault();
                if (next is null)
                {
                    break;
                }

                // No batch open yet (first run, or the previous run completed its final batch): open one.
                if (campaign.CurrentBatchNumber == 0 || campaign.CurrentBatchSize == 0)
                {
                    campaign.StartBatch(Math.Max(1, campaign.CurrentBatchNumber), batchDelays.GenerateRandomBatchSize(pacing));
                    await SaveCampaignAsync(db, campaign);
                    await EmitAsync(campaign, CampaignMessageLevel.Info, $"Batch {campaign.CurrentBatchNumber} started: {campaign.CurrentBatchSize} group(s).");
                }
                else if (campaign.CurrentBatchProcessed == 0)
                {
                    await EmitAsync(campaign, CampaignMessageLevel.Info, $"Batch {campaign.CurrentBatchNumber} started: {campaign.CurrentBatchSize} group(s).");
                }

                processedThisRun++;
                await EmitAsync(campaign, CampaignMessageLevel.Info,
                    $"Group {processedThisRun} of {remainingAtStart} (batch {campaign.CurrentBatchNumber}, group {campaign.CurrentBatchProcessed + 1} of {campaign.CurrentBatchSize}): {next.GroupName}",
                    next);

                next.BatchNumber = campaign.CurrentBatchNumber;
                await ProcessTargetAsync(db, campaign, next, request, settings, cancellationToken);
                await AfterGroupProcessedAsync(db, campaign, next, pacing);
            }

            campaign.ClearDelay();
            campaign.Finish(campaign.FinalStatus());
            logger.LogInformation("Campaign finished with status {Status}", campaign.Status);

            var counts = CampaignCountsDto.From(campaign.Targets.Select(t => t.Status));
            await EmitAsync(campaign, campaign.Status == CampaignStatus.Completed ? CampaignMessageLevel.Success : CampaignMessageLevel.Warning,
                $"Campaign finished after {campaign.CompletedBatches} batch(es): {counts.Published} published, {counts.Skipped} skipped, " +
                $"{counts.Inaccessible} inaccessible, {counts.Failed} failed, {counts.OutcomeUnknown} unknown outcome.");
        }
        catch (AutomationHaltedException ex)
        {
            logger.LogWarning(ex, "Campaign halted: {Reason}", ex.Reason);
            campaign.Finish(CampaignStatus.Halted, HaltMessage(ex));
            await EmitAsync(campaign, CampaignMessageLevel.ActionRequired, $"Campaign halted. {HaltMessage(ex)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Groups not yet processed stay Pending; published groups and batch counters are already saved.
            if (hostStoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Campaign interrupted by application shutdown");
                campaign.Finish(CampaignStatus.Interrupted,
                    "The application stopped while this campaign was running. Resume it to continue with the remaining groups.");
            }
            else
            {
                logger.LogInformation("Campaign stopped by user");
                campaign.Finish(CampaignStatus.Stopped, "Stopped by user. Resume to continue with the remaining groups.");
                await EmitAsync(campaign, CampaignMessageLevel.Warning,
                    $"Campaign stopped. {campaign.Targets.Count(t => t.IsProcessable)} group(s) remain Pending; press Resume to continue.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Campaign failed unexpectedly");
            campaign.Finish(CampaignStatus.Halted, $"Unexpected error: {ex.Message}");
            await EmitAsync(campaign, CampaignMessageLevel.Error, $"Campaign stopped because of an unexpected error: {ex.Message}");
        }

        campaign.ReconcileProgress();
        await db.SaveChangesAsync(CancellationToken.None);
        await NotifyCampaignAsync(campaign);
    }

    /// <summary>Updates batch counters after one group and schedules the next wait (post, batch or extended delay).</summary>
    private async Task AfterGroupProcessedAsync(IApplicationDbContext db, Campaign campaign, CampaignTarget target, BatchPacingConfig pacing)
    {
        campaign.LastProcessedTargetId = target.Id;
        campaign.ReconcileProgress();

        if (campaign.RemainingGroupCount == 0)
        {
            // Final (possibly partial) batch: count it and close it, but never wait after the last group. Closing it
            // (next batch number, size 0) means a later resume opens a new batch instead of re-counting this one.
            campaign.CompletedBatches++;
            campaign.BatchesSinceExtendedDelay++;
            campaign.StartBatch(campaign.CurrentBatchNumber + 1, 0);
            campaign.ClearDelay();
            await SaveCampaignAsync(db, campaign);
            return;
        }

        if (campaign.CurrentBatchProcessed >= campaign.CurrentBatchSize)
        {
            await CompleteBatchAndScheduleNextAsync(campaign, pacing);
        }
        else
        {
            campaign.ScheduleDelay(DelayKind.BetweenPosts, batchDelays.GenerateRandomPostDelay(pacing));
        }
        await SaveCampaignAsync(db, campaign);
    }

    /// <summary>
    /// Counts the finished batch, decides between the extended break (takes precedence) and the normal batch delay,
    /// and prepares the next batch, all in one save so a crash can never count a batch twice.
    /// </summary>
    private async Task CompleteBatchAndScheduleNextAsync(Campaign campaign, BatchPacingConfig pacing)
    {
        var finished = campaign.CurrentBatchNumber;
        campaign.CompletedBatches++;
        campaign.BatchesSinceExtendedDelay++;

        var extended = campaign.BatchesSinceExtendedDelay >= pacing.ExtendedDelayAfterBatches;
        var delay = extended ? batchDelays.GenerateRandomExtendedDelay(pacing) : batchDelays.GenerateRandomBatchDelay(pacing);
        if (extended)
        {
            campaign.BatchesSinceExtendedDelay = 0;
        }

        campaign.StartBatch(finished + 1, batchDelays.GenerateRandomBatchSize(pacing));
        campaign.ScheduleDelay(extended ? DelayKind.Extended : DelayKind.BetweenBatches, delay);

        logger.LogInformation("Batch {Batch} completed; {Kind} of {Delay} before batch {Next} ({Size} groups)",
            finished, extended ? "extended break" : "batch delay", delay, finished + 1, campaign.CurrentBatchSize);
        await EmitAsync(campaign, CampaignMessageLevel.Success,
            $"Batch {finished} completed ({campaign.CompletedBatches} completed in total). " +
            (extended
                ? $"Extended break after {pacing.ExtendedDelayAfterBatches} batches: {DurationText.Format(delay)}."
                : $"Break between batches: {DurationText.Format(delay)}.") +
            $" Next: batch {finished + 1} with {campaign.CurrentBatchSize} group(s).");
    }

    /// <summary>Waits out the persisted delay (if any) in heartbeat-sized chunks; cancellation interrupts it immediately.</summary>
    private async Task WaitForScheduledDelayAsync(IApplicationDbContext db, Campaign campaign, CancellationToken ct)
    {
        if (campaign.CurrentDelayKind == DelayKind.None || campaign.CurrentDelayEndsAtUtc is not { } endsAt)
        {
            return;
        }

        var remaining = endsAt - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            var kind = campaign.CurrentDelayKind;
            var interrupted = remaining.TotalSeconds < campaign.CurrentDelaySeconds - 1;
            await EmitAsync(campaign, CampaignMessageLevel.Info,
                $"{DurationText.Describe(kind)}: waiting {DurationText.Format(remaining)}" +
                (interrupted ? $" (remaining part of the {DurationText.Format(TimeSpan.FromSeconds(campaign.CurrentDelaySeconds))} delay)." : "."),
                until: endsAt);
            await NotifyCampaignAsync(campaign);

            while (remaining > TimeSpan.Zero)
            {
                var chunk = remaining < HeartbeatInterval ? remaining : HeartbeatInterval;
                await asyncDelay.DelayAsync(chunk, ct);
                remaining -= chunk;
                campaign.LastExecutionAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }

        campaign.ClearDelay();
        await SaveCampaignAsync(db, campaign);
    }

    private async Task ProcessTargetAsync(
        IApplicationDbContext db,
        Campaign campaign,
        CampaignTarget target,
        PublishRequest request,
        PublisherSettings settings,
        CancellationToken ct)
    {
        using var _ = logger.BeginScope(new Dictionary<string, object> { ["TargetId"] = target.Id, ["GroupUrl"] = target.GroupUrl });
        var group = target.FacebookGroupId is { } groupId ? await db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, CancellationToken.None) : null;
        var steps = new StepReporter(text => EmitAsync(campaign, CampaignMessageLevel.Step, text, target));

        // 1. Membership verification
        await UpdateTargetAsync(db, campaign, target, TargetStatus.CheckingMembership);
        MembershipCheckResult membership;
        try
        {
            membership = await automation.CheckMembershipAsync(target.GroupUrl, steps, ct);
        }
        catch (Exception ex) when (ex is AutomationHaltedException or OperationCanceledException)
        {
            // Nothing was posted: the group goes back to Pending and will be processed on resume.
            target.BatchNumber = null;
            await UpdateTargetAsync(db, campaign, target, TargetStatus.Pending);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Membership check failed");
            await UpdateTargetAsync(db, campaign, target, TargetStatus.Failed, $"Membership check failed: {ex.Message}");
            await EmitAsync(campaign, CampaignMessageLevel.Error, $"Membership check failed: {ex.Message}", target);
            return;
        }

        target.MembershipStatus = membership.Status;
        if (!string.IsNullOrWhiteSpace(membership.GroupName))
        {
            target.GroupName = membership.GroupName.Trim();
        }
        group?.RecordMembership(membership.Status, membership.GroupName);

        switch (membership.Status)
        {
            case MembershipStatus.NotMember:
                var notMember = membership.Detail ?? "The account is not a member of this group. Groups are never joined automatically.";
                await UpdateTargetAsync(db, campaign, target, TargetStatus.SkippedNotMember, notMember);
                await EmitAsync(campaign, CampaignMessageLevel.Warning, $"Skipped — not a member. {notMember}", target);
                return;
            case MembershipStatus.Inaccessible:
                var inaccessible = membership.Detail ?? "The group is unavailable, deleted, or not visible to this account.";
                await UpdateTargetAsync(db, campaign, target, TargetStatus.Inaccessible, inaccessible);
                await EmitAsync(campaign, CampaignMessageLevel.Error, $"Inaccessible. {inaccessible}", target);
                return;
            case MembershipStatus.Unknown:
                var unknown = membership.Detail ?? "Could not determine membership. The Facebook page layout may have changed; check the selectors in appsettings.json.";
                await UpdateTargetAsync(db, campaign, target, TargetStatus.Failed, unknown);
                await EmitAsync(campaign, CampaignMessageLevel.Error, $"Failed. {unknown}", target);
                return;
        }

        await UpdateTargetAsync(db, campaign, target, TargetStatus.Eligible);
        await EmitAsync(campaign, CampaignMessageLevel.Success, "Membership confirmed — eligible for publishing.", target);

        // 2. Duplicate protection: identical content already published to this group recently.
        if (settings.DuplicateWindowHours > 0)
        {
            var since = DateTime.UtcNow.AddHours(-settings.DuplicateWindowHours);
            var hash = campaign.ContentPost!.ContentHash;
            var duplicate = await db.CampaignTargets.AsNoTracking().AnyAsync(t =>
                t.Id != target.Id &&
                t.GroupUrl == target.GroupUrl &&
                t.Status == TargetStatus.Published &&
                t.PublishedAtUtc >= since &&
                t.Campaign!.ContentPost!.ContentHash == hash, CancellationToken.None);

            if (duplicate)
            {
                var message = $"The same content was already published to this group within the last {settings.DuplicateWindowHours} hour(s).";
                await UpdateTargetAsync(db, campaign, target, TargetStatus.SkippedDuplicate, message);
                await EmitAsync(campaign, CampaignMessageLevel.Warning, $"Skipped — duplicate. {message}", target);
                return;
            }
        }

        // 3. Publishing. The attempt is persisted before Facebook is touched; if the application stops after this
        //    point the group becomes "Outcome Unknown" and is never retried automatically.
        target.PublishAttempts++;
        await UpdateTargetAsync(db, campaign, target, TargetStatus.Publishing);

        Func<CancellationToken, Task<ApprovalDecision>>? approval = null;
        if (campaign.RequireApproval)
        {
            approval = async token =>
            {
                var timeout = TimeSpan.FromMinutes(settings.ApprovalTimeoutMinutes);
                await UpdateTargetAsync(db, campaign, target, TargetStatus.AwaitingApproval);
                await EmitAsync(campaign, CampaignMessageLevel.ActionRequired,
                    $"Review the prepared post for \"{target.GroupName}\" in the browser window, then Approve or Reject it here.",
                    target, DateTime.UtcNow.Add(timeout));

                var decision = await approvalGate.WaitAsync(target.Id, timeout, token);
                if (decision == ApprovalDecision.Approved)
                {
                    await UpdateTargetAsync(db, campaign, target, TargetStatus.Publishing);
                }
                return decision;
            };
        }

        PublishResult result;
        try
        {
            result = await automation.PublishAsync(target.GroupUrl, request, approval, steps, ct);
        }
        catch (AutomationHaltedException ex)
        {
            await UpdateTargetAsync(db, campaign, target, TargetStatus.Failed,
                $"Stopped: {ex.Message} Check the group on Facebook to confirm whether the post was created.");
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The automation observes cancellation only before clicking Post (the confirmation wait ignores it),
            // so a cancelled publish never reached Facebook: return the group to Pending for the next resume.
            target.BatchNumber = null;
            await UpdateTargetAsync(db, campaign, target, TargetStatus.Pending);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Publishing failed");
            await UpdateTargetAsync(db, campaign, target, TargetStatus.Failed, ex.Message);
            await EmitAsync(campaign, CampaignMessageLevel.Error, $"Publishing failed: {ex.Message}", target);
            return;
        }

        switch (result.Outcome)
        {
            case PublishOutcome.Published:
                logger.LogInformation("Published to {GroupName}", target.GroupName);
                await UpdateTargetAsync(db, campaign, target, TargetStatus.Published, result.Message);
                await EmitAsync(campaign, CampaignMessageLevel.Success,
                    result.Message is null ? "Published successfully." : $"Published successfully. {result.Message}", target);
                break;
            case PublishOutcome.Rejected:
                await UpdateTargetAsync(db, campaign, target, TargetStatus.SkippedByUser, result.Message);
                await EmitAsync(campaign, CampaignMessageLevel.Warning, "Skipped — rejected during review.", target);
                break;
            default:
                logger.LogWarning("Publishing failed: {Message}", result.Message);
                await UpdateTargetAsync(db, campaign, target, TargetStatus.Failed, result.Message);
                await EmitAsync(campaign, CampaignMessageLevel.Error, $"Publishing failed: {result.Message}", target);
                break;
        }
    }

    private PublishRequest BuildRequest(ContentPost content, PublisherSettings settings)
    {
        var files = new List<PublishFile>();
        foreach (var attachment in content.Attachments.OrderBy(a => a.SortOrder))
        {
            if (!storage.Exists(attachment.StoredFileName))
            {
                throw new InvalidOperationException($"Attachment '{attachment.OriginalFileName}' is missing from storage.");
            }
            files.Add(new PublishFile(storage.GetFullPath(attachment.StoredFileName), attachment.OriginalFileName, attachment.Type));
        }
        return new PublishRequest(content.Text, files, TimeSpan.FromSeconds(settings.UploadTimeoutSeconds));
    }

    /// <summary>Saves a group's status immediately (plus the heartbeat) and pushes it to the monitor.</summary>
    private async Task UpdateTargetAsync(IApplicationDbContext db, Campaign campaign, CampaignTarget target, TargetStatus status, string? message = null)
    {
        target.SetStatus(status, message);
        campaign.LastExecutionAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        await notifier.TargetChangedAsync(
            campaign.Id,
            TargetProgressDto.From(target),
            CampaignCountsDto.From(campaign.Targets.Select(t => t.Status)),
            CancellationToken.None);
    }

    private async Task SaveCampaignAsync(IApplicationDbContext db, Campaign campaign)
    {
        campaign.LastExecutionAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        await NotifyCampaignAsync(campaign);
    }

    private Task EmitAsync(Campaign campaign, CampaignMessageLevel level, string text, CampaignTarget? target = null, DateTime? until = null) =>
        notifier.MessageAsync(campaign.Id, new CampaignMessage(level, text, target?.Id, until, target?.GroupName), CancellationToken.None);

    private Task NotifyCampaignAsync(Campaign campaign) =>
        notifier.CampaignChangedAsync(CampaignProgressDto.From(campaign), CancellationToken.None);

    private static string HaltMessage(AutomationHaltedException ex) => ex.Reason switch
    {
        HaltReason.SessionExpired => $"Facebook session is not active: {ex.Message} Log in again from Settings, then resume the campaign.",
        HaltReason.Checkpoint => $"Facebook requires a security check: {ex.Message} Complete it manually in the browser before resuming.",
        HaltReason.Restricted => $"Facebook indicated a restriction: {ex.Message} Automation was stopped; do not resume until the restriction is lifted.",
        HaltReason.BrowserUnavailable => $"The browser could not be used: {ex.Message}",
        _ => ex.Message
    };

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/> (unlike <see cref="Progress{T}"/> it does not post to a captured context),
    /// so steps are recorded in the order the automation reports them.
    /// </summary>
    private sealed class StepReporter(Func<string, Task> report) : IProgress<string>
    {
        public void Report(string value) => _ = report(value);
    }
}
