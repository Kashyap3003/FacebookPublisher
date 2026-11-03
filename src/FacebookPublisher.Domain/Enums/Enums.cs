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
    Draft = 0,
    Queued = 1,
    Running = 2,
    Completed = 3,
    CompletedWithErrors = 4,
    /// <summary>Stopped by the system because Facebook required manual intervention (session expired, checkpoint, restriction).</summary>
    Halted = 5,
    Cancelled = 6
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
    Cancelled = 11
}
