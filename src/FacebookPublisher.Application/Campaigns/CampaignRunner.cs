using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Automation;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FacebookPublisher.Application.Campaigns;

/// <summary>
/// Executes one campaign: verifies the session, then for each group checks membership, prevents duplicates,
/// publishes (optionally after user approval) and waits the configured delay. A failing group never stops the
/// others; a Facebook restriction/checkpoint/expired session halts the whole campaign.
/// Every stage is reported to the live activity feed through <see cref="ICampaignProgressNotifier.MessageAsync"/>.
/// </summary>
public sealed class CampaignRunner(
    IApplicationDbContextFactory dbFactory,
    IFacebookAutomation automation,
    IFileStorage storage,
    ICampaignApprovalGate approvalGate,
    ICampaignProgressNotifier notifier,
    ILogger<CampaignRunner> logger)
{
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

        campaign.MarkRunning();
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Campaign {CampaignId} changed before it could start (probably cancelled); skipping", campaignId);
            return;
        }

        using var _ = logger.BeginScope(new Dictionary<string, object> { ["CampaignId"] = campaignId });
        logger.LogInformation("Campaign '{Name}' started with {TargetCount} group(s)", campaign.Name, campaign.Targets.Count);
        await NotifyCampaignAsync(campaign);

        var pending = campaign.Targets.Where(t => t.IsProcessable).OrderBy(t => t.Order).ToList();
        await EmitAsync(campaign, CampaignMessageLevel.Info,
            $"Campaign started: {pending.Count} group(s) to process, {campaign.MinDelaySeconds}–{campaign.MaxDelaySeconds}s between groups, " +
            (campaign.RequireApproval ? "each post reviewed before publishing." : "fully automatic posting."));

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == PublisherSettings.SingletonId, CancellationToken.None)
            ?? new PublisherSettings();

        try
        {
            var request = BuildRequest(campaign.ContentPost!, settings);

            await EmitAsync(campaign, CampaignMessageLevel.Step, "Starting the browser and checking the Facebook session…");
            var session = await automation.CheckSessionAsync(cancellationToken);
            if (!session.IsLoggedIn)
            {
                throw new AutomationHaltedException(HaltReason.SessionExpired, session.Message);
            }
            await EmitAsync(campaign, CampaignMessageLevel.Success, "Facebook session is active.");

            for (var i = 0; i < pending.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (i > 0)
                {
                    await DelayAsync(campaign, cancellationToken);
                }

                var target = pending[i];
                await EmitAsync(campaign, CampaignMessageLevel.Info, $"Group {i + 1} of {pending.Count}: {target.GroupName}", target);
                await ProcessTargetAsync(db, campaign, target, request, settings, cancellationToken);
            }

            var hasErrors = campaign.Targets.Any(t => t.Status is TargetStatus.Failed or TargetStatus.Inaccessible);
            campaign.Finish(hasErrors ? CampaignStatus.CompletedWithErrors : CampaignStatus.Completed);
            logger.LogInformation("Campaign finished with status {Status}", campaign.Status);

            var counts = CampaignCountsDto.From(campaign.Targets.Select(t => t.Status));
            await EmitAsync(campaign, hasErrors ? CampaignMessageLevel.Warning : CampaignMessageLevel.Success,
                $"Campaign finished: {counts.Published} published, {counts.Skipped} skipped, {counts.Inaccessible} inaccessible, {counts.Failed} failed.");
        }
        catch (AutomationHaltedException ex)
        {
            logger.LogWarning(ex, "Campaign halted: {Reason}", ex.Reason);
            campaign.Finish(CampaignStatus.Halted, HaltMessage(ex));
            await EmitAsync(campaign, CampaignMessageLevel.ActionRequired, $"Campaign halted. {HaltMessage(ex)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (hostStoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Campaign interrupted by application shutdown");
                campaign.Finish(CampaignStatus.Halted, "The application stopped while the campaign was running. Resume it to continue with the remaining groups.");
            }
            else
            {
                foreach (var target in campaign.Targets.Where(t => t.IsProcessable))
                {
                    target.SetStatus(TargetStatus.Cancelled, "Campaign was cancelled before this group was processed.");
                }
                logger.LogInformation("Campaign cancelled by user");
                campaign.Finish(CampaignStatus.Cancelled, "Cancelled by user.");
                await EmitAsync(campaign, CampaignMessageLevel.Warning, "Campaign cancelled. Remaining groups were not processed.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Campaign failed unexpectedly");
            campaign.Finish(CampaignStatus.Halted, $"Unexpected error: {ex.Message}");
            await EmitAsync(campaign, CampaignMessageLevel.Error, $"Campaign stopped because of an unexpected error: {ex.Message}");
        }

        await db.SaveChangesAsync(CancellationToken.None);
        await NotifyCampaignAsync(campaign);
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
        catch (AutomationHaltedException)
        {
            await UpdateTargetAsync(db, campaign, target, TargetStatus.Pending);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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

        // 3. Publishing. The attempt is persisted before Facebook is touched, so a crash can never lead to a silent re-post.
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
            await UpdateTargetAsync(db, campaign, target, TargetStatus.Cancelled,
                "Cancelled while publishing. Check the group on Facebook to confirm whether the post was created.");
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

    private async Task DelayAsync(Campaign campaign, CancellationToken ct)
    {
        var seconds = Random.Shared.Next(campaign.MinDelaySeconds, campaign.MaxDelaySeconds + 1);
        await EmitAsync(campaign, CampaignMessageLevel.Info, $"Waiting {seconds}s before the next group.", until: DateTime.UtcNow.AddSeconds(seconds));
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
    }

    private async Task UpdateTargetAsync(IApplicationDbContext db, Campaign campaign, CampaignTarget target, TargetStatus status, string? message = null)
    {
        target.SetStatus(status, message);
        await db.SaveChangesAsync(CancellationToken.None);
        await notifier.TargetChangedAsync(
            campaign.Id,
            TargetProgressDto.From(target),
            CampaignCountsDto.From(campaign.Targets.Select(t => t.Status)),
            CancellationToken.None);
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
