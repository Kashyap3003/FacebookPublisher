using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;

namespace FacebookPublisher.Application.Campaigns;

public sealed record CreateCampaignRequest(
    string Name,
    Guid ContentId,
    IReadOnlyList<Guid> GroupIds,
    BatchPacingConfig Pacing,
    bool RequireApproval);

public sealed record TargetProgressDto(
    Guid Id,
    Guid? GroupId,
    string GroupName,
    string GroupUrl,
    int Order,
    MembershipStatus MembershipStatus,
    TargetStatus Status,
    DateTime? PublishedAtUtc,
    string? ErrorMessage,
    DateTime UpdatedAtUtc,
    int? BatchNumber = null)
{
    public static TargetProgressDto From(CampaignTarget t) => new(
        t.Id, t.FacebookGroupId, t.GroupName, t.GroupUrl, t.Order, t.MembershipStatus, t.Status,
        t.PublishedAtUtc, t.ErrorMessage, t.UpdatedAtUtc, t.BatchNumber);
}

public sealed record CampaignCountsDto(
    int Total,
    int Published,
    int Skipped,
    int Failed,
    int Inaccessible,
    int Cancelled,
    int Remaining,
    int OutcomeUnknown = 0)
{
    public int Processed => Total - Remaining;

    public int PercentComplete => Total == 0 ? 0 : (int)Math.Round(Processed * 100.0 / Total);

    public static CampaignCountsDto From(IEnumerable<TargetStatus> statuses)
    {
        var list = statuses.ToList();
        return new CampaignCountsDto(
            list.Count,
            list.Count(s => s == TargetStatus.Published),
            list.Count(s => s is TargetStatus.SkippedNotMember or TargetStatus.SkippedDuplicate or TargetStatus.SkippedByUser),
            list.Count(s => s == TargetStatus.Failed),
            list.Count(s => s == TargetStatus.Inaccessible),
            list.Count(s => s == TargetStatus.Cancelled),
            list.Count(s => s is TargetStatus.Pending or TargetStatus.CheckingMembership or TargetStatus.Eligible
                or TargetStatus.Publishing or TargetStatus.AwaitingApproval),
            list.Count(s => s == TargetStatus.OutcomeUnknown));
    }
}

/// <summary>Persisted batch execution state, as shown in the Execution Monitor.</summary>
public sealed record BatchProgressDto(
    int CurrentBatchNumber,
    int CurrentBatchSize,
    int CurrentBatchProcessed,
    int CompletedBatches,
    int BatchesSinceExtendedDelay,
    int EstimatedTotalBatches,
    DelayKind CurrentDelayKind,
    int CurrentDelaySeconds,
    DateTime? CurrentDelayEndsAtUtc,
    DateTime? LastExecutionAtUtc)
{
    public static BatchProgressDto From(Campaign c)
    {
        var remaining = c.Targets.Count(t => t.IsProcessable);
        return new BatchProgressDto(
            c.CurrentBatchNumber,
            c.CurrentBatchSize,
            c.CurrentBatchProcessed,
            c.CompletedBatches,
            c.BatchesSinceExtendedDelay,
            PacingEstimator.EstimateTotalBatches(c, remaining),
            c.CurrentDelayKind,
            c.CurrentDelaySeconds,
            c.CurrentDelayEndsAtUtc,
            c.LastExecutionAtUtc);
    }
}

public sealed record CampaignProgressDto(
    Guid Id,
    Guid OwnerId,
    string Name,
    Guid ContentId,
    string ContentTitle,
    CampaignStatus Status,
    string? StatusReason,
    bool RequireApproval,
    BatchPacingConfig Pacing,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    bool CanStart,
    bool CanStop,
    bool CanResume,
    BatchProgressDto Batch,
    CampaignCountsDto Counts,
    IReadOnlyList<TargetProgressDto> Targets)
{
    public static CampaignProgressDto From(Campaign c) => new(
        c.Id,
        c.OwnerId,
        c.Name,
        c.ContentPostId,
        c.ContentPost?.Title ?? string.Empty,
        c.Status,
        c.StatusReason,
        c.RequireApproval,
        c.Pacing.Clone(),
        c.CreatedAtUtc,
        c.StartedAtUtc,
        c.CompletedAtUtc,
        c.CanBeStarted,
        c.CanBeStopped,
        c.CanBeResumed,
        BatchProgressDto.From(c),
        CampaignCountsDto.From(c.Targets.Select(t => t.Status)),
        c.Targets.OrderBy(t => t.Order).Select(TargetProgressDto.From).ToList());
}

public sealed record CampaignSummaryDto(
    Guid Id,
    string Name,
    string ContentTitle,
    CampaignStatus Status,
    string? StatusReason,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    CampaignCountsDto Counts);

public enum CampaignMessageLevel
{
    /// <summary>Campaign-level information (started, waiting between groups).</summary>
    Info,
    /// <summary>An individual browser step for the current group (opening composer, attaching files…).</summary>
    Step,
    Success,
    Warning,
    Error,
    ActionRequired
}

/// <summary>Live activity entry shown in the Execution Monitor. <see cref="UntilUtc"/> marks a countdown (pacing delay, review timeout).</summary>
public sealed record CampaignMessage(
    CampaignMessageLevel Level,
    string Text,
    Guid? TargetId = null,
    DateTime? UntilUtc = null,
    string? GroupName = null)
{
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}
