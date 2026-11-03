using FacebookPublisher.Application.Automation;

namespace FacebookPublisher.BrowserAutomation;

/// <summary>
/// Used when Hosting:AutomationEnabled is false (e.g. the online demo on a server without a desktop or a Facebook
/// login). Every browser operation stops the campaign with a clear explanation instead of launching a browser.
/// </summary>
public sealed class DisabledFacebookAutomation : IFacebookAutomation
{
    public const string Message =
        "Facebook automation is disabled in this online demo. Run the application on your own PC to publish to Facebook.";

    public bool IsBrowserOpen => false;

    public Task<SessionStatus> CheckSessionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SessionStatus(false, Message, DateTime.UtcNow));

    public Task OpenLoginWindowAsync(CancellationToken cancellationToken) => throw Disabled();

    public Task<ResolvedLink?> ResolveLinkAsync(string url, CancellationToken cancellationToken) => throw Disabled();

    public Task<MembershipCheckResult> CheckMembershipAsync(string groupUrl, IProgress<string>? progress, CancellationToken cancellationToken) =>
        throw Disabled();

    public Task<PublishResult> PublishAsync(string groupUrl, PublishRequest request, Func<CancellationToken, Task<ApprovalDecision>>? approval,
        IProgress<string>? progress, CancellationToken cancellationToken) => throw Disabled();

    public Task CloseBrowserAsync() => Task.CompletedTask;

    private static AutomationHaltedException Disabled() => new(HaltReason.BrowserUnavailable, Message);
}
