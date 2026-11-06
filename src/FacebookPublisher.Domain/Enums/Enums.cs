namespace FacebookPublisher.Domain.Enums;

public enum AttachmentType
{
    Image = 0,
    Video = 1,
    Pdf = 2
}

public enum MembershipStatus
{
    Unknown = 0,
    Member = 1,
    NotMember = 2,
    Inaccessible = 3
}

public enum CampaignStatus
{
    /// <summary>Created but never started.</summary>
    NotStarted = 0,
    /// <summary>The user clicked Start/Resume; waiting for the campaign worker.</summary>
    Queued = 1,
    Running = 2,
    Completed = 3,
    CompletedWithErrors = 4,
    /// <summary>Stopped by the system because Facebook required manual intervention (session expired, checkpoint, restriction).</summary>
    Halted = 5,
    /// <summary>Legacy status from before Stop/Resume; such campaigns can still be resumed.</summary>
    Cancelled = 6,
    /// <summary>Stopped by the user; remaining groups stay Pending.</summary>
    Stopped = 7,
    /// <summary>The application stopped (shutdown, crash) while the campaign was queued or running.</summary>
    Interrupted = 8
}

/// <summary>Units offered by the batch/delay settings. Each delay field allows only a subset (see the validator).</summary>
public enum DelayUnit
{
    Seconds = 0,
    Minutes = 1,
    Hours = 2
}

/// <summary>The kind of wait a campaign is in (or will continue with when resumed).</summary>
public enum DelayKind
{
    None = 0,
    BetweenPosts = 1,
    BetweenBatches = 2,
    Extended = 3
}

public enum TargetStatus
{
    Pending = 0,
    CheckingMembership = 1,
    Eligible = 2,
    Publishing = 3,
    AwaitingApproval = 4,
    Published = 5,
    SkippedNotMember = 6,
    SkippedDuplicate = 7,
    SkippedByUser = 8,
    Failed = 9,
    Inaccessible = 10,
    /// <summary>Legacy status from before Stop/Resume.</summary>
    Cancelled = 11,
    /// <summary>The application stopped while this group was Publishing; Facebook may or may not have accepted the post. Never retried automatically.</summary>
    OutcomeUnknown = 12
}
