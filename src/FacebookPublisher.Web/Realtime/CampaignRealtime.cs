using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Campaigns;
using Microsoft.AspNetCore.SignalR;

namespace FacebookPublisher.Web.Realtime;

/// <summary>
/// SignalR hub for external clients (dashboards, scripts). Requires a signed-in user, and each user only receives
/// events of their own campaigns. Events: CampaignChanged(progress), TargetChanged(campaignId, target, counts),
/// Message(campaignId, message).
/// </summary>
[Microsoft.AspNetCore.Authorization.Authorize]
public sealed class CampaignHub : Hub
{
    public const string Path = "/hubs/campaigns";
}

/// <summary>
/// In-process fan-out for Blazor Server components. Components already receive UI updates over the circuit's
/// SignalR connection, so they subscribe here rather than opening a second connection back to this server.
/// Every event carries the campaign owner; subscribers must ignore events of other users.
/// </summary>
public sealed class CampaignEventBroker(ILogger<CampaignEventBroker> logger)
{
    public event Func<CampaignProgressDto, Task>? CampaignChanged;

    /// <summary>(ownerId, campaignId, target, counts)</summary>
    public event Func<Guid, Guid, TargetProgressDto, CampaignCountsDto, Task>? TargetChanged;

    /// <summary>(ownerId, campaignId, message)</summary>
    public event Func<Guid, Guid, CampaignMessage, Task>? MessageReceived;

    internal Task PublishCampaignAsync(CampaignProgressDto campaign) =>
        InvokeAllAsync(CampaignChanged, handler => handler(campaign));

    internal Task PublishTargetAsync(Guid ownerId, Guid campaignId, TargetProgressDto target, CampaignCountsDto counts) =>
        InvokeAllAsync(TargetChanged, handler => handler(ownerId, campaignId, target, counts));

    internal Task PublishMessageAsync(Guid ownerId, Guid campaignId, CampaignMessage message) =>
        InvokeAllAsync(MessageReceived, handler => handler(ownerId, campaignId, message));

    private async Task InvokeAllAsync<THandler>(THandler? handlers, Func<THandler, Task> invoke) where THandler : Delegate
    {
        if (handlers is null)
        {
            return;
        }
        foreach (var handler in handlers.GetInvocationList().Cast<THandler>())
        {
            try
            {
                await invoke(handler);
            }
            catch (Exception ex)
            {
                // A disconnected component must never break campaign execution.
                logger.LogDebug(ex, "Campaign event subscriber failed");
            }
        }
    }
}

/// <summary>
/// Keeps the most recent live activity per campaign in memory so the Execution Monitor can show history when it is
/// opened (or reloaded) in the middle of a run. Target statuses themselves are persisted in the database.
/// </summary>
public sealed class CampaignActivityLog
{
    private const int MaxEntriesPerCampaign = 1000;
    private const int MaxCampaigns = 20;

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, List<CampaignMessage>> _entries = [];
    private readonly LinkedList<Guid> _recent = new();

    public void Append(Guid campaignId, CampaignMessage message)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(campaignId, out var list))
            {
                list = [];
                _entries[campaignId] = list;
                _recent.AddLast(campaignId);
                if (_recent.Count > MaxCampaigns)
                {
                    _entries.Remove(_recent.First!.Value);
                    _recent.RemoveFirst();
                }
            }

            list.Add(message);
            if (list.Count > MaxEntriesPerCampaign)
            {
                list.RemoveRange(0, list.Count - MaxEntriesPerCampaign);
            }
        }
    }

    /// <summary>Entries in chronological order.</summary>
    public IReadOnlyList<CampaignMessage> Get(Guid campaignId)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(campaignId, out var list) ? list.ToList() : [];
        }
    }
}

public sealed class CampaignProgressNotifier(
    IHubContext<CampaignHub> hub,
    CampaignEventBroker broker,
    CampaignActivityLog activityLog,
    ILogger<CampaignProgressNotifier> logger) : ICampaignProgressNotifier
{
    public async Task CampaignChangedAsync(CampaignProgressDto campaign, CancellationToken cancellationToken = default)
    {
        await broker.PublishCampaignAsync(campaign);
        await SafeSendAsync(campaign.OwnerId, "CampaignChanged", [campaign], cancellationToken);
    }

    public async Task TargetChangedAsync(Guid ownerId, Guid campaignId, TargetProgressDto target, CampaignCountsDto counts, CancellationToken cancellationToken = default)
    {
        await broker.PublishTargetAsync(ownerId, campaignId, target, counts);
        await SafeSendAsync(ownerId, "TargetChanged", [campaignId, target, counts], cancellationToken);
    }

    public async Task MessageAsync(Guid ownerId, Guid campaignId, CampaignMessage message, CancellationToken cancellationToken = default)
    {
        // Appended synchronously, before any await, so entries keep the order in which they were reported.
        activityLog.Append(campaignId, message);
        await broker.PublishMessageAsync(ownerId, campaignId, message);
        await SafeSendAsync(ownerId, "Message", [campaignId, message], cancellationToken);
    }

    /// <summary>Delivers to the campaign owner's connections only (SignalR user id = NameIdentifier claim).</summary>
    private async Task SafeSendAsync(Guid ownerId, string method, object?[] args, CancellationToken ct)
    {
        try
        {
            await hub.Clients.User(ownerId.ToString()).SendCoreAsync(method, args, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast {Method} over SignalR", method);
        }
    }
}
