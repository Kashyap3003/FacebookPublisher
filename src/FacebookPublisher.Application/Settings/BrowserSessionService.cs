using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Automation;
using FacebookPublisher.Application.Common;
using Microsoft.Extensions.Logging;

namespace FacebookPublisher.Application.Settings;

/// <summary>
/// The signed-in user's Facebook connection: login window, session verification, browser restart and disconnect.
/// Always operates on the signed-in user's own browser profile; Facebook credentials never pass through the app.
/// </summary>
public sealed class BrowserSessionService(
    IFacebookAutomationFactory automationFactory,
    ICurrentUser currentUser,
    ICampaignExecutionTracker tracker,
    SettingsService settings,
    ILogger<BrowserSessionService> logger)
{
    public async Task<bool> IsBrowserOpenAsync(CancellationToken ct = default) =>
        (await AutomationAsync(ct)).IsBrowserOpen;

    public async Task<SessionStatus> CheckSessionAsync(CancellationToken ct = default)
    {
        var automation = await AutomationAsync(ct);
        SessionStatus status;
        try
        {
            status = await automation.CheckSessionAsync(ct);
        }
        catch (AutomationHaltedException ex)
        {
            status = new SessionStatus(false, ex.Message, DateTime.UtcNow);
        }

        await settings.RecordSessionCheckAsync(status.IsLoggedIn, status.CheckedAtUtc, ct);
        logger.LogInformation("Facebook session check for user {OwnerId}: {IsLoggedIn}", automation.OwnerId, status.IsLoggedIn);
        return status;
    }

    public async Task OpenLoginWindowAsync(CancellationToken ct = default)
    {
        var automation = await AutomationAsync(ct);
        EnsureNoActiveCampaign(automation.OwnerId);
        await automation.OpenLoginWindowAsync(ct);
    }

    public async Task CloseBrowserAsync(CancellationToken ct = default)
    {
        var automation = await AutomationAsync(ct);
        EnsureNoActiveCampaign(automation.OwnerId);
        await automation.CloseBrowserAsync();
    }

    /// <summary>Disconnects Facebook for the signed-in user by deleting their browser profile.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var automation = await AutomationAsync(ct);
        EnsureNoActiveCampaign(automation.OwnerId);
        await automation.ResetSessionAsync();
        await settings.RecordSessionCheckAsync(null, null, ct);
        logger.LogInformation("Facebook session of user {OwnerId} disconnected (browser profile deleted)", automation.OwnerId);
    }

    private async Task<IFacebookAutomation> AutomationAsync(CancellationToken ct) =>
        automationFactory.ForUser(await currentUser.GetUserIdAsync(ct));

    private void EnsureNoActiveCampaign(Guid ownerId)
    {
        if (tracker.IsOwnerBusy(ownerId))
        {
            throw new AppValidationException("One of your campaigns is running and using your browser. Wait for it to finish or stop it first.");
        }
    }
}
