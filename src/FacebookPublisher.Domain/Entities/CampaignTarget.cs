using FacebookPublisher.Domain.Common;
using FacebookPublisher.Domain.Enums;

namespace FacebookPublisher.Domain.Entities;

/// <summary>One group within one campaign. Group name/URL are snapshotted so history survives group deletion.</summary>
public class CampaignTarget : BaseEntity
{
    public Guid CampaignId { get; set; }

    public Campaign? Campaign { get; set; }

    public Guid? FacebookGroupId { get; set; }

    public FacebookGroup? FacebookGroup { get; set; }

    public string GroupName { get; set; } = string.Empty;

    public string GroupUrl { get; set; } = string.Empty;

    public int Order { get; set; }

    public MembershipStatus MembershipStatus { get; set; } = MembershipStatus.Unknown;

    public TargetStatus Status { get; set; } = TargetStatus.Pending;

    public DateTime? PublishedAtUtc { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>Number of times a Post submission was started. Never exceeds 1 for a successful target.</summary>
    public int PublishAttempts { get; set; }

    /// <summary>Targets the runner may still work on. Terminal states are never re-processed, which prevents duplicate posts.</summary>
    public bool IsProcessable => Status is TargetStatus.Pending or TargetStatus.CheckingMembership or TargetStatus.Eligible;

    public bool IsTerminal => !IsProcessable && Status is not (TargetStatus.Publishing or TargetStatus.AwaitingApproval);

    public void SetStatus(TargetStatus status, string? error = null)
    {
        Status = status;
        ErrorMessage = error;
        if (status == TargetStatus.Published)
        {
            PublishedAtUtc = DateTime.UtcNow;
        }
        Touch();
    }
}
