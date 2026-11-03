using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Automation;
using FacebookPublisher.Application.Common;
using Microsoft.Extensions.Logging;

namespace FacebookPublisher.Application.Settings;

/// <summary>User-initiated browser session operations (login window, session verification, browser restart).</summary>
public sealed class BrowserSessionService(
    IFacebookAutomation automation,
    ICampaignExecutionTracker tracker,
    SettingsService settings,
    ILogger<BrowserSessionService> logger)
{
    public bool IsBrowserOpen => automation.IsBrowserOpen;

    public async Task<SessionStatus> CheckSessionAsync(CancellationToken ct = default)
    {
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
        logger.LogInformation("Facebook session check: {IsLoggedIn} ({Message})", status.IsLoggedIn, status.Message);
        return status;
    }

    public Task OpenLoginWindowAsync(CancellationToken ct = default)
    {
        EnsureNoActiveCampaign();
        return automation.OpenLoginWindowAsync(ct);
    }

    public Task CloseBrowserAsync()
    {
        EnsureNoActiveCampaign();
        return automation.CloseBrowserAsync();
    }

    private void EnsureNoActiveCampaign()
    {
        if (tracker.ActiveCampaignId is not null)
        {
            throw new AppValidationException("A campaign is running. Wait for it to finish or cancel it first.");
        }
    }
}
