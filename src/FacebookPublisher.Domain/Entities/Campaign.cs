using FacebookPublisher.Domain.Common;
using FacebookPublisher.Domain.Enums;

namespace FacebookPublisher.Domain.Entities;

public class Campaign : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    public Guid ContentPostId { get; set; }

    public ContentPost? ContentPost { get; set; }

    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;

    public int MinDelaySeconds { get; set; } = 10;

    public int MaxDelaySeconds { get; set; } = 20;

    /// <summary>When true, the runner prepares each post and waits for the user to approve it in the dashboard.</summary>
    public bool RequireApproval { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Why the campaign was halted or cancelled, if applicable.</summary>
    public string? StatusReason { get; set; }

    public ICollection<CampaignTarget> Targets { get; set; } = new List<CampaignTarget>();

    /// <summary>Optimistic concurrency token; guards status transitions between the UI and the background runner.</summary>
    public byte[] RowVersion { get; set; } = [];

    public bool IsActive => Status is CampaignStatus.Queued or CampaignStatus.Running;

    /// <summary>A halted campaign (session expired, checkpoint, restart) can continue with the groups it has not processed yet.</summary>
    public bool CanBeResumed => Status == CampaignStatus.Halted && Targets.Any(t => t.IsProcessable);

    public void MarkRunning()
    {
        Status = CampaignStatus.Running;
        StartedAtUtc ??= DateTime.UtcNow;
        StatusReason = null;
        Touch();
    }

    public void Finish(CampaignStatus status, string? reason = null)
    {
        Status = status;
        StatusReason = reason;
        CompletedAtUtc = DateTime.UtcNow;
        Touch();
    }
}
