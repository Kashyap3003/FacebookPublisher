using FacebookPublisher.Domain.Common;

namespace FacebookPublisher.Domain.Entities;

/// <summary>One row per application user holding that user's settings (keyed by <see cref="OwnerId"/>).</summary>
public class PublisherSettings : IOwnedEntity
{
    /// <summary>The application user these settings belong to (primary key).</summary>
    public Guid OwnerId { get; set; }

    /// <summary>Default batch/delay configuration for new campaigns (each campaign keeps its own copy).</summary>
    public BatchPacingConfig Pacing { get; set; } = new();

    public bool RequireApprovalByDefault { get; set; }

    public int ApprovalTimeoutMinutes { get; set; } = 10;

    public int MaxGroupsPerCampaign { get; set; } = 50;

    /// <summary>Skip a group if identical content was published there within this many hours. 0 disables the check.</summary>
    public int DuplicateWindowHours { get; set; } = 24;

    /// <summary>
    /// Browser profile folder used before multi-user support. The profile is now derived from the user id and is not
    /// editable; a legacy folder recorded here is moved to the owner's profile location once and then cleared.
    /// </summary>
    public string? LegacyBrowserProfilePath { get; set; }

    /// <summary>Playwright channel: "chrome", "msedge", or empty for the bundled Chromium.</summary>
    public string BrowserChannel { get; set; } = "chrome";

    public bool Headless { get; set; }

    public int NavigationTimeoutSeconds { get; set; } = 45;

    public int UploadTimeoutSeconds { get; set; } = 300;

    public DateTime? SessionLastVerifiedAtUtc { get; set; }

    public bool? SessionLastVerifiedResult { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
