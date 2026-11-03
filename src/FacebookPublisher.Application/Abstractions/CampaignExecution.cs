using FacebookPublisher.Application.Automation;
using FacebookPublisher.Application.Campaigns;

namespace FacebookPublisher.Application.Abstractions;

/// <summary>FIFO queue of campaign ids consumed by the background worker.</summary>
public interface ICampaignQueue
{
    ValueTask EnqueueAsync(Guid campaignId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>Tracks the campaign currently being executed and exposes cooperative cancellation for it.</summary>
public interface ICampaignExecutionTracker
{
    Guid? ActiveCampaignId { get; }

    /// <summary>Registers the campaign as active and returns a token cancelled by user request or host shutdown.</summary>
    CancellationToken Begin(Guid campaignId, CancellationToken hostStoppingToken);

    bool RequestCancel(Guid campaignId);

    void End(Guid campaignId);
}

/// <summary>Human-in-the-loop approval for posts prepared in the browser.</summary>
public interface ICampaignApprovalGate
{
    Task<ApprovalDecision> WaitAsync(Guid targetId, TimeSpan timeout, CancellationToken cancellationToken);

    bool Resolve(Guid targetId, bool approve);

    bool IsAwaiting(Guid targetId);
}

/// <summary>Pushes live campaign progress to connected clients (SignalR).</summary>
public interface ICampaignProgressNotifier
{
    Task CampaignChangedAsync(CampaignProgressDto campaign, CancellationToken cancellationToken = default);

    Task TargetChangedAsync(Guid campaignId, TargetProgressDto target, CampaignCountsDto counts, CancellationToken cancellationToken = default);

    Task MessageAsync(Guid campaignId, CampaignMessage message, CancellationToken cancellationToken = default);
}
