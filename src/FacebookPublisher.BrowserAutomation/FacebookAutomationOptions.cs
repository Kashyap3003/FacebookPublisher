namespace FacebookPublisher.BrowserAutomation;

/// <summary>
/// Facebook changes its markup frequently and localises its UI, so every selector and phrase used for detection
/// is configuration. Defaults target the English (US) web UI. Selectors use Playwright's CSS dialect (supports :has-text).
/// </summary>
public sealed class FacebookAutomationOptions
{
    public const string SectionName = "FacebookAutomation";

    public string BaseUrl { get; set; } = "https://www.facebook.com/";

    public string LoginUrl { get; set; } = "https://www.facebook.com/login";

    /// <summary>
    /// Folder holding one persistent browser profile per application user ({root}/{userId}). Absolute, or relative to
    /// the application content root. Keep it outside wwwroot: profiles contain Facebook session cookies.
    /// </summary>
    public string BrowserProfilesRoot { get; set; } = "App_Data/BrowserProfiles";

    /// <summary>Cookie set by Facebook only for a logged-in session.</summary>
    public string SessionCookieName { get; set; } = "c_user";

    /// <summary>How long to wait for a group page to reveal its membership state.</summary>
    public int PageProbeSeconds { get; set; } = 20;

    public int ComposerOpenSeconds { get; set; } = 20;

    /// <summary>How long to wait for the composer to close after clicking Post.</summary>
    public int PostConfirmSeconds { get; set; } = 90;

    public FacebookSelectors Selectors { get; set; } = new();

    public FacebookPhrases Phrases { get; set; } = new();
}

public sealed class FacebookSelectors
{
    public string[] JoinButton { get; set; } =
    [
        "div[role='main'] div[role='button'][aria-label='Join group']",
        "div[role='main'] div[role='button']:has-text('Join group')"
    ];

    public string[] PendingRequestButton { get; set; } =
    [
        "div[role='main'] div[role='button']:has-text('Cancel request')"
    ];

    public string[] MemberIndicator { get; set; } =
    [
        "div[role='main'] div[role='button']:has-text('Joined')"
    ];

    public string[] ComposerTrigger { get; set; } =
    [
        "div[role='main'] div[role='button']:has-text('Write something')",
        "div[role='main'] div[role='button']:has-text('Create a public post')",
        "div[role='main'] div[role='button']:has-text('Create post')"
    ];

    public string ComposerDialog { get; set; } = "div[role='dialog']:has(div[role='textbox'][contenteditable='true'])";

    public string ComposerTextbox { get; set; } = "div[role='textbox'][contenteditable='true']";

    public string[] PhotoVideoButton { get; set; } =
    [
        "div[role='button'][aria-label='Photo/video']",
        "div[role='button'][aria-label='Photo/Video']"
    ];

    public string[] FileButton { get; set; } =
    [
        "div[role='button'][aria-label='File']",
        "div[role='button'][aria-label='Add file']",
        "div[role='button']:has-text('Add File')"
    ];

    public string[] MoreOptionsButton { get; set; } =
    [
        "div[role='button'][aria-label='More']"
    ];

    public string MediaFileInput { get; set; } = "input[type='file'][accept*='image'], input[type='file'][accept*='video']";

    public string DocumentFileInput { get; set; } = "input[type='file']:not([accept*='image']):not([accept*='video'])";

    public string[] PostButton { get; set; } =
    [
        "div[role='button'][aria-label='Post']"
    ];

    public string[] DiscardButton { get; set; } =
    [
        "div[role='dialog'] div[role='button']:has-text('Discard')",
        "div[role='dialog'] div[role='button']:has-text('Leave')"
    ];
}

public sealed class FacebookPhrases
{
    public string[] Inaccessible { get; set; } =
    [
        "This content isn't available",
        "This page isn't available",
        "Content not found",
        "content isn't available right now"
    ];

    /// <summary>Any of these visible on the page halts the campaign immediately.</summary>
    public string[] Restriction { get; set; } =
    [
        "You're Temporarily Blocked",
        "You can't post right now",
        "Your account has been restricted",
        "We limit how often you can post",
        "We suspect automated behavior",
        "temporarily restricted",
        "Help us confirm it's you",
        "Confirm your identity",
        "Your account has been locked"
    ];

    /// <summary>Shown after posting in groups where admins review posts; the post still counts as submitted.</summary>
    public string[] PendingAdminApproval { get; set; } =
    [
        "Your post is pending",
        "pending admin approval",
        "submitted for admin approval"
    ];

    public string[] CheckpointUrlFragments { get; set; } = ["/checkpoint", "two_step_verification", "/confirmemail"];

    public string[] LoginUrlFragments { get; set; } = ["/login", "login.php", "/recover"];
}
