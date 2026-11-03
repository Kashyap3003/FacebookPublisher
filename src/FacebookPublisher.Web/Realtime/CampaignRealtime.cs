using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Campaigns;
using Microsoft.AspNetCore.SignalR;

namespace FacebookPublisher.Web.Realtime;

/// <summary>
/// SignalR hub for external clients (dashboards, scripts). Events: CampaignChanged(progress),
/// TargetChanged(campaignId, target, counts), Message(campaignId, message).
/// </summary>
public sealed class CampaignHub : Hub
{
    public const string Path = "/hubs/campaigns";
}

/// <summary>
/// In-process fan-out for Blazor Server components. Components already receive UI updates over the circuit's
/// SignalR connection, so they subscribe here rather than opening a second connection back to this server.
/// </summary>
public sealed class CampaignEventBroker(ILogger<CampaignEventBroker> logger)
{
    public event Func<CampaignProgressDto, Task>? CampaignChanged;

    public event Func<Guid, TargetProgressDto, CampaignCountsDto, Task>? TargetChanged;

    public event Func<Guid, CampaignMessage, Task>? MessageReceived;

    internal Task PublishCampaignAsync(CampaignProgressDto campaign) =>
        InvokeAllAsync(CampaignChanged, handler => handler(campaign));

    internal Task PublishTargetAsync(Guid campaignId, TargetProgressDto target, CampaignCountsDto counts) =>
        InvokeAllAsync(TargetChanged, handler => handler(campaignId, target, counts));

    internal Task PublishMessageAsync(Guid campaignId, CampaignMessage message) =>
        InvokeAllAsync(MessageReceived, handler => handler(campaignId, message));

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
        await SafeSendAsync("CampaignChanged", [campaign], cancellationToken);
    }

    public async Task TargetChangedAsync(Guid campaignId, TargetProgressDto target, CampaignCountsDto counts, CancellationToken cancellationToken = default)
    {
        await broker.PublishTargetAsync(campaignId, target, counts);
        await SafeSendAsync("TargetChanged", [campaignId, target, counts], cancellationToken);
    }

    public async Task MessageAsync(Guid campaignId, CampaignMessage message, CancellationToken cancellationToken = default)
    {
        // Appended synchronously, before any await, so entries keep the order in which they were reported.
        activityLog.Append(campaignId, message);
        await broker.PublishMessageAsync(campaignId, message);
        await SafeSendAsync("Message", [campaignId, message], cancellationToken);
    }

    private async Task SafeSendAsync(string method, object?[] args, CancellationToken ct)
    {
        try
        {
            await hub.Clients.All.SendCoreAsync(method, args, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast {Method} over SignalR", method);
        }
    }
}
