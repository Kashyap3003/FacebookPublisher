using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;

namespace FacebookPublisher.Application.Campaigns;

public sealed record CreateCampaignRequest(
    string Name,
    Guid ContentId,
    IReadOnlyList<Guid> GroupIds,
    int MinDelaySeconds,
    int MaxDelaySeconds,
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
    DateTime UpdatedAtUtc)
{
    public static TargetProgressDto From(CampaignTarget t) => new(
        t.Id, t.FacebookGroupId, t.GroupName, t.GroupUrl, t.Order, t.MembershipStatus, t.Status,
        t.PublishedAtUtc, t.ErrorMessage, t.UpdatedAtUtc);
}

public sealed record CampaignCountsDto(int Total, int Published, int Skipped, int Failed, int Inaccessible, int Cancelled, int Remaining)
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
                or TargetStatus.Publishing or TargetStatus.AwaitingApproval));
    }
}

public sealed record CampaignProgressDto(
    Guid Id,
    string Name,
    Guid ContentId,
    string ContentTitle,
    CampaignStatus Status,
    string? StatusReason,
    bool RequireApproval,
    int MinDelaySeconds,
    int MaxDelaySeconds,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    bool CanResume,
    CampaignCountsDto Counts,
    IReadOnlyList<TargetProgressDto> Targets)
{
    public static CampaignProgressDto From(Campaign c) => new(
        c.Id,
        c.Name,
        c.ContentPostId,
        c.ContentPost?.Title ?? string.Empty,
        c.Status,
        c.StatusReason,
        c.RequireApproval,
        c.MinDelaySeconds,
        c.MaxDelaySeconds,
        c.CreatedAtUtc,
        c.StartedAtUtc,
        c.CompletedAtUtc,
        c.CanBeResumed,
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
