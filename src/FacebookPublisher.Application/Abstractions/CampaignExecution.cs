using FacebookPublisher.Application.Automation;
using FacebookPublisher.Application.Campaigns;

namespace FacebookPublisher.Application.Abstractions;

/// <summary>FIFO queue of campaign ids consumed by the background worker.</summary>
public interface ICampaignQueue
{
    ValueTask EnqueueAsync(Guid campaignId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Tracks campaigns being executed in this process (several users' campaigns can run at the same time, at most one
/// per user) and exposes cooperative cancellation for each.
/// </summary>
public interface ICampaignExecutionTracker
{
    /// <summary>Identifies this application process. Stored on campaigns it owns (database execution lease).</summary>
    Guid InstanceId { get; }

    /// <summary>When this process started; leases written before it belong to a previous process.</summary>
    DateTime StartedAtUtc { get; }

    /// <summary>
    /// Registers the campaign as running for its owner and returns a token cancelled by Stop or host shutdown.
    /// Throws if the owner already has a running campaign (one browser profile per user).
    /// </summary>
    CancellationToken Begin(Guid campaignId, Guid ownerId, CancellationToken hostStoppingToken);

    bool RequestCancel(Guid campaignId);

    /// <summary>Stops every running campaign of the user (used when an account is disabled).</summary>
    int RequestCancelForOwner(Guid ownerId);

    void End(Guid campaignId);

    bool IsRunning(Guid campaignId);

    /// <summary>True while one of the user's campaigns is executing (their browser profile is in use).</summary>
    bool IsOwnerBusy(Guid ownerId);

    IReadOnlyCollection<Guid> RunningCampaignIds { get; }
}

/// <summary>Inclusive random integers. Injected so tests can make batch sizes and delays deterministic.</summary>
public interface IRandomSource
{
    int NextInclusive(int minInclusive, int maxInclusive);
}

/// <summary>Cancellable asynchronous wait (Task.Delay). Injected so tests do not wait for real minutes or hours.</summary>
public interface IAsyncDelay
{
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>Human-in-the-loop approval for posts prepared in the browser.</summary>
public interface ICampaignApprovalGate
{
    Task<ApprovalDecision> WaitAsync(Guid targetId, TimeSpan timeout, CancellationToken cancellationToken);

    bool Resolve(Guid targetId, bool approve);

    bool IsAwaiting(Guid targetId);
}

/// <summary>
/// Pushes live campaign progress to clients. Every event carries the campaign owner so it is delivered only to that
/// user (SignalR user targeting and owner filtering in the UI).
/// </summary>
public interface ICampaignProgressNotifier
{
    Task CampaignChangedAsync(CampaignProgressDto campaign, CancellationToken cancellationToken = default);

    Task TargetChangedAsync(Guid ownerId, Guid campaignId, TargetProgressDto target, CampaignCountsDto counts, CancellationToken cancellationToken = default);

    Task MessageAsync(Guid ownerId, Guid campaignId, CampaignMessage message, CancellationToken cancellationToken = default);
}
