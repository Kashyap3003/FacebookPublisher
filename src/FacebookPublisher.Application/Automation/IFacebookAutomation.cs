using FacebookPublisher.Domain.Enums;

namespace FacebookPublisher.Application.Automation;

/// <summary>
/// Hands out the browser automation of one application user. Each user has an independent persistent browser
/// profile (their own Facebook login); an instance never touches another user's profile.
/// </summary>
public interface IFacebookAutomationFactory
{
    IFacebookAutomation ForUser(Guid ownerId);
}

/// <summary>
/// Browser automation boundary for one application user. Implementations drive a browser profile that user logged
/// into manually; they never handle credentials and must throw <see cref="AutomationHaltedException"/> whenever
/// Facebook signals that automation should stop.
/// </summary>
public interface IFacebookAutomation
{
    /// <summary>The application user whose browser profile this instance drives.</summary>
    Guid OwnerId { get; }

    bool IsBrowserOpen { get; }

    Task<SessionStatus> CheckSessionAsync(CancellationToken cancellationToken);

    /// <summary>Opens a visible browser window on the Facebook login page so the user can sign in themselves.</summary>
    Task OpenLoginWindowAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens a link (e.g. a facebook.com/share/g/… share link) in the logged-in browser and returns the address it
    /// redirects to, plus the group name when the destination is a group. Returns null if it never reaches a group page.
    /// </summary>
    Task<ResolvedLink?> ResolveLinkAsync(string url, CancellationToken cancellationToken);

    /// <param name="progress">Receives a short description of each browser step, for live display.</param>
    Task<MembershipCheckResult> CheckMembershipAsync(string groupUrl, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Prepares the post in the group composer and submits it. When <paramref name="approval"/> is supplied it is
    /// awaited after the composer is filled and before the Post button is clicked.
    /// </summary>
    Task<PublishResult> PublishAsync(
        string groupUrl,
        PublishRequest request,
        Func<CancellationToken, Task<ApprovalDecision>>? approval,
        IProgress<string>? progress,
        CancellationToken cancellationToken);

    /// <summary>Closes the browser so it is relaunched with current settings on next use.</summary>
    Task CloseBrowserAsync();

    /// <summary>Disconnects Facebook: closes the browser and deletes this user's browser profile (cookies, storage).</summary>
    Task ResetSessionAsync();
}

public sealed record SessionStatus(bool IsLoggedIn, string Message, DateTime CheckedAtUtc);

public sealed record ResolvedLink(string Url, string? GroupName);

public sealed record MembershipCheckResult(MembershipStatus Status, string? GroupName, string? Detail);

public sealed record PublishFile(string FullPath, string FileName, AttachmentType Type);

public sealed record PublishRequest(string? Text, IReadOnlyList<PublishFile> Files, TimeSpan UploadTimeout);

public enum ApprovalDecision
{
    Approved,
    Rejected,
    TimedOut
}

public enum PublishOutcome
{
    Published,
    Failed,
    Rejected
}

public sealed record PublishResult(PublishOutcome Outcome, string? Message)
{
    public static PublishResult Success(string? note = null) => new(PublishOutcome.Published, note);

    public static PublishResult Failure(string message) => new(PublishOutcome.Failed, message);

    public static PublishResult RejectedByUser() => new(PublishOutcome.Rejected, "Post was rejected by the user during review.");
}

public enum HaltReason
{
    SessionExpired,
    Checkpoint,
    Restricted,
    BrowserUnavailable
}

/// <summary>Facebook (or the browser) requires manual intervention; the campaign must stop immediately.</summary>
public sealed class AutomationHaltedException(HaltReason reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public HaltReason Reason { get; } = reason;
}
