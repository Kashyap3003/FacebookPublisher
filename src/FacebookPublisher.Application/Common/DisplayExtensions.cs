using FacebookPublisher.Domain.Enums;

namespace FacebookPublisher.Application.Common;

public static class DisplayExtensions
{
    public static string ToDisplay(this TargetStatus status) => status switch
    {
        TargetStatus.Pending => "Pending",
        TargetStatus.CheckingMembership => "Checking Membership",
        TargetStatus.Eligible => "Eligible",
        TargetStatus.Publishing => "Publishing",
        TargetStatus.AwaitingApproval => "Awaiting Approval",
        TargetStatus.Published => "Published Successfully",
        TargetStatus.SkippedNotMember => "Skipped (Not a Member)",
        TargetStatus.SkippedDuplicate => "Skipped (Duplicate)",
        TargetStatus.SkippedByUser => "Skipped (Rejected)",
        TargetStatus.Failed => "Failed",
        TargetStatus.Inaccessible => "Inaccessible",
        TargetStatus.Cancelled => "Cancelled",
        TargetStatus.OutcomeUnknown => "Outcome Unknown",
        _ => status.ToString()
    };

    public static string ToDisplay(this MembershipStatus status) => status switch
    {
        MembershipStatus.Unknown => "Unknown",
        MembershipStatus.Member => "Member",
        MembershipStatus.NotMember => "Not a Member",
        MembershipStatus.Inaccessible => "Inaccessible",
        _ => status.ToString()
    };

    public static string ToDisplay(this CampaignStatus status) => status switch
    {
        CampaignStatus.NotStarted => "Not Started",
        CampaignStatus.CompletedWithErrors => "Completed with Errors",
        CampaignStatus.Halted => "Halted (action required)",
        _ => status.ToString()
    };

    public static string ToFileSize(this long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }
}
