using FacebookPublisher.Domain.Common;
using FacebookPublisher.Domain.Enums;

namespace FacebookPublisher.Domain.Entities;

public class Campaign : BaseEntity, IOwnedEntity
{
    /// <summary>
    /// Application user who owns the campaign. Execution always uses this user's data and Facebook browser session,
    /// never the identity of whoever is currently using the UI. Campaign targets inherit ownership from here.
    /// </summary>
    public Guid OwnerId { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid ContentPostId { get; set; }

    public ContentPost? ContentPost { get; set; }

    public CampaignStatus Status { get; set; } = CampaignStatus.NotStarted;

    /// <summary>Batch and delay configuration captured when the campaign was created; never changed afterwards.</summary>
    public BatchPacingConfig Pacing { get; set; } = new();

    /// <summary>When true, the runner prepares each post and waits for the user to approve it in the dashboard.</summary>
    public bool RequireApproval { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    /// <summary>When the last run ended (completed, stopped, halted or interrupted).</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Why the campaign was halted, stopped or interrupted, if applicable.</summary>
    public string? StatusReason { get; set; }

    // ---------- Batch execution state (persisted after every step; the database is the source of truth) ----------

    /// <summary>1-based number of the batch in progress (or about to start after a delay); 0 before the first batch.</summary>
    public int CurrentBatchNumber { get; set; }

    /// <summary>Randomly chosen size of the current batch.</summary>
    public int CurrentBatchSize { get; set; }

    /// <summary>Groups of the current batch that have been processed.</summary>
    public int CurrentBatchProcessed { get; set; }

    /// <summary>Batches fully processed. Interrupted batches are not counted until they finish.</summary>
    public int CompletedBatches { get; set; }

    /// <summary>Completed batches since the last extended delay.</summary>
    public int BatchesSinceExtendedDelay { get; set; }

    /// <summary>Groups still waiting to be processed (Pending), as of the last saved step.</summary>
    public int RemainingGroupCount { get; set; }

    public Guid? LastProcessedTargetId { get; set; }

    /// <summary>Heartbeat: updated after every group and at least once a minute while waiting.</summary>
    public DateTime? LastExecutionAtUtc { get; set; }

    /// <summary>Process instance that owns the execution while Queued/Running (database lease against duplicate execution).</summary>
    public Guid? ExecutionOwnerId { get; set; }

    /// <summary>The delay the campaign is waiting in, or will finish waiting when resumed.</summary>
    public DelayKind CurrentDelayKind { get; set; }

    /// <summary>Randomly selected length of the current delay.</summary>
    public int CurrentDelaySeconds { get; set; }

    public DateTime? CurrentDelayEndsAtUtc { get; set; }

    public ICollection<CampaignTarget> Targets { get; set; } = new List<CampaignTarget>();

    /// <summary>Optimistic concurrency token; guards status transitions between the UI and the background runner.</summary>
    public byte[] RowVersion { get; set; } = [];

    public bool IsActive => Status is CampaignStatus.Queued or CampaignStatus.Running;

    public bool CanBeStarted => Status == CampaignStatus.NotStarted;

    public bool CanBeStopped => IsActive;

    /// <summary>
    /// Any campaign that is not active and still has unprocessed groups can continue. Only Pending groups are ever
    /// processed, so groups already published (or with an unknown outcome) are never posted to again.
    /// </summary>
    public bool CanBeResumed => Status is not (CampaignStatus.NotStarted or CampaignStatus.Queued or CampaignStatus.Running)
        && Targets.Any(t => t.IsProcessable || t.Status == TargetStatus.Cancelled);

    public void MarkRunning(Guid ownerId)
    {
        Status = CampaignStatus.Running;
        StartedAtUtc ??= DateTime.UtcNow;
        CompletedAtUtc = null;
        StatusReason = null;
        ExecutionOwnerId = ownerId;
        LastExecutionAtUtc = DateTime.UtcNow;
        Touch();
    }

    public void Finish(CampaignStatus status, string? reason = null)
    {
        Status = status;
        StatusReason = reason;
        CompletedAtUtc = DateTime.UtcNow;
        ExecutionOwnerId = null;
        Touch();
    }

    public void StartBatch(int batchNumber, int size)
    {
        CurrentBatchNumber = batchNumber;
        CurrentBatchSize = size;
        CurrentBatchProcessed = 0;
        Touch();
    }

    public void ScheduleDelay(DelayKind kind, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            ClearDelay();
            return;
        }
        CurrentDelayKind = kind;
        CurrentDelaySeconds = (int)duration.TotalSeconds;
        CurrentDelayEndsAtUtc = DateTime.UtcNow.Add(duration);
        Touch();
    }

    public void ClearDelay()
    {
        CurrentDelayKind = DelayKind.None;
        CurrentDelaySeconds = 0;
        CurrentDelayEndsAtUtc = null;
        Touch();
    }

    /// <summary>Recomputes counters that can be derived from the groups, so they always agree with the database.</summary>
    public void ReconcileProgress()
    {
        RemainingGroupCount = Targets.Count(t => t.IsProcessable);
        CurrentBatchProcessed = CurrentBatchNumber == 0
            ? 0
            : Targets.Count(t => t.BatchNumber == CurrentBatchNumber && !t.IsProcessable);
    }

    public CampaignStatus FinalStatus() =>
        Targets.Any(t => t.Status is TargetStatus.Failed or TargetStatus.Inaccessible or TargetStatus.OutcomeUnknown)
            ? CampaignStatus.CompletedWithErrors
            : CampaignStatus.Completed;
}
