using FacebookPublisher.Domain.Common;
using FacebookPublisher.Domain.Enums;

namespace FacebookPublisher.Domain.Entities;

public class FacebookGroup : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Canonical URL in the form https://www.facebook.com/groups/{identifier}.</summary>
    public string Url { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;

    public MembershipStatus LastMembershipStatus { get; set; } = MembershipStatus.Unknown;

    public DateTime? LastCheckedAtUtc { get; set; }

    public ICollection<CampaignTarget> CampaignTargets { get; set; } = new List<CampaignTarget>();

    public void RecordMembership(MembershipStatus status, string? detectedName)
    {
        LastMembershipStatus = status;
        LastCheckedAtUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(detectedName) && (string.IsNullOrWhiteSpace(Name) || Name == Url))
        {
            Name = detectedName.Trim();
        }
        Touch();
    }
}
