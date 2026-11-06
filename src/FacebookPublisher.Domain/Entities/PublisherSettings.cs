namespace FacebookPublisher.Domain.Entities;

/// <summary>Single-row table holding user-editable application settings.</summary>
public class PublisherSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Default batch/delay configuration for new campaigns (each campaign keeps its own copy).</summary>
    public BatchPacingConfig Pacing { get; set; } = new();

    public bool RequireApprovalByDefault { get; set; }

    public int ApprovalTimeoutMinutes { get; set; } = 10;

    public int MaxGroupsPerCampaign { get; set; } = 50;

    /// <summary>Skip a group if identical content was published there within this many hours. 0 disables the check.</summary>
    public int DuplicateWindowHours { get; set; } = 24;

    /// <summary>Folder that holds the persistent Chromium profile (cookies of the manually logged-in session).</summary>
    public string BrowserProfilePath { get; set; } = string.Empty;

    /// <summary>Playwright channel: "chrome", "msedge", or empty for the bundled Chromium.</summary>
    public string BrowserChannel { get; set; } = "chrome";

    public bool Headless { get; set; }

    public int NavigationTimeoutSeconds { get; set; } = 45;

    public int UploadTimeoutSeconds { get; set; } = 300;

    public DateTime? SessionLastVerifiedAtUtc { get; set; }

    public bool? SessionLastVerifiedResult { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
