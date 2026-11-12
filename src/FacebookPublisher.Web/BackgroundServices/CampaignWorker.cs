using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Campaigns;
using Microsoft.Extensions.Options;

namespace FacebookPublisher.Web.BackgroundServices;

public sealed class CampaignWorkerOptions
{
    public const string SectionName = "Campaigns";

    /// <summary>
    /// How many campaigns (of different users) may execute at the same time. Each running campaign has its own browser
    /// window, so keep this in line with what the PC can handle.
    /// </summary>
    public int MaxConcurrentCampaigns { get; set; } = 10;
}

/// <summary>
/// Executes campaigns the users queued with Start or Resume. Campaigns of different users run at the same time, each
/// with its owner's own browser profile; a user's own campaigns run one after another (one browser per profile).
/// The owner is always read from the campaign record. Nothing is queued automatically at start-up.
/// </summary>
public sealed class CampaignWorker(
    ICampaignQueue queue,
    ICampaignExecutionTracker tracker,
    IServiceScopeFactory scopeFactory,
    IOptions<CampaignWorkerOptions> options,
    ILogger<CampaignWorker> logger) : BackgroundService
{
    private readonly Lock _lock = new();

    /// <summary>Owners with a campaign in progress, and the campaigns of theirs waiting behind it.</summary>
    private readonly Dictionary<Guid, Queue<Guid>> _owners = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var maxConcurrent = Math.Max(1, options.Value.MaxConcurrentCampaigns);
        using var slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        var running = new List<Task>();
        logger.LogInformation("Campaign worker started (up to {Max} campaigns of different users at the same time)", maxConcurrent);

        try
        {
            await foreach (var campaignId in queue.ReadAllAsync(stoppingToken))
            {
                var ownerId = await GetOwnerAsync(campaignId, stoppingToken);
                if (ownerId is null)
                {
                    logger.LogWarning("Queued campaign {CampaignId} no longer exists", campaignId);
                    continue;
                }

                lock (_lock)
                {
                    if (_owners.TryGetValue(ownerId.Value, out var waiting))
                    {
                        // The owner's browser profile is in use: run this campaign after their current one.
                        waiting.Enqueue(campaignId);
                        logger.LogInformation("Campaign {CampaignId} waits for user {OwnerId}'s running campaign", campaignId, ownerId);
                        continue;
                    }
                    _owners[ownerId.Value] = new Queue<Guid>();
                }

                running.RemoveAll(t => t.IsCompleted);
                running.Add(RunOwnerAsync(ownerId.Value, campaignId, slots, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Running campaigns observe the stopping token and save their state (Interrupted) before finishing.
        await Task.WhenAll(running);
        logger.LogInformation("Campaign worker stopped");
    }

    private async Task RunOwnerAsync(Guid ownerId, Guid firstCampaignId, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        var campaignId = firstCampaignId;
        try
        {
            await slots.WaitAsync(stoppingToken);
            try
            {
                while (true)
                {
                    await RunCampaignAsync(ownerId, campaignId, stoppingToken);
                    lock (_lock)
                    {
                        if (!_owners[ownerId].TryDequeue(out campaignId))
                        {
                            _owners.Remove(ownerId);
                            return;
                        }
                    }
                }
            }
            finally
            {
                slots.Release();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown while waiting for a slot: the campaigns stay Queued and are marked Interrupted when opened.
            lock (_lock)
            {
                _owners.Remove(ownerId);
            }
        }
    }

    private async Task RunCampaignAsync(Guid ownerId, Guid campaignId, CancellationToken stoppingToken)
    {
        CancellationToken token;
        try
        {
            token = tracker.Begin(campaignId, ownerId, stoppingToken);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Campaign {CampaignId} could not start", campaignId);
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<CampaignRunner>();
            await runner.RunAsync(campaignId, token, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Unhandled error while running campaign {CampaignId} of user {OwnerId}", campaignId, ownerId);
        }
        finally
        {
            tracker.End(campaignId);
        }
    }

    private async Task<Guid?> GetOwnerAsync(Guid campaignId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CampaignRunner>().GetOwnerIdAsync(campaignId, ct);
    }
}
