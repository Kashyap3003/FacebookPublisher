using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Campaigns;

namespace FacebookPublisher.Web.BackgroundServices;

/// <summary>
/// Executes queued campaigns one at a time. There is a single browser profile, so campaigns are never run in parallel.
/// </summary>
public sealed class CampaignWorker(
    ICampaignQueue queue,
    ICampaignExecutionTracker tracker,
    IServiceScopeFactory scopeFactory,
    ILogger<CampaignWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Campaign worker started");
        try
        {
            await foreach (var campaignId in queue.ReadAllAsync(stoppingToken))
            {
                var token = tracker.Begin(campaignId, stoppingToken);
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var runner = scope.ServiceProvider.GetRequiredService<CampaignRunner>();
                    await runner.RunAsync(campaignId, token, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Unhandled error while running campaign {CampaignId}", campaignId);
                }
                finally
                {
                    tracker.End(campaignId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        logger.LogInformation("Campaign worker stopped");
    }
}
