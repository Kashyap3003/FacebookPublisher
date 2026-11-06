using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Campaigns;
using FacebookPublisher.Application.Common;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace FacebookPublisher.Application.Reports;

public sealed record ReasonCountDto(string Category, string Reason, int Count);

public sealed record CampaignReportDto(
    Guid CampaignId,
    string CampaignName,
    string ContentTitle,
    CampaignStatus Status,
    string? StatusReason,
    bool RequireApproval,
    BatchPacingConfig Pacing,
    int CompletedBatches,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    CampaignCountsDto Counts,
    IReadOnlyList<ReasonCountDto> Reasons,
    IReadOnlyList<TargetProgressDto> Targets,
    DateTime GeneratedAtUtc);

public sealed record ActivityDto(Guid CampaignId, string CampaignName, string GroupName, string GroupUrl, TargetStatus Status, string? Message, DateTime TimestampUtc);

public sealed record DashboardDto(
    int TotalGroups,
    int ActiveGroups,
    int MemberGroups,
    int TotalContents,
    int TotalCampaigns,
    int TotalPublished,
    int TotalFailed,
    int PublishedLast7Days,
    double SuccessRate,
    CampaignSummaryDto? ActiveCampaign,
    bool? SessionLastVerifiedResult,
    DateTime? SessionLastVerifiedAtUtc,
    IReadOnlyList<ActivityDto> RecentActivity);

public sealed class ReportService(IApplicationDbContextFactory dbFactory)
{
    public async Task<CampaignReportDto> GetCampaignReportAsync(Guid campaignId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var campaign = await db.Campaigns.AsNoTracking()
            .Include(c => c.ContentPost)
            .Include(c => c.Targets)
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct)
            ?? throw new NotFoundException("Campaign", campaignId);

        var progress = CampaignProgressDto.From(campaign);

        var reasons = campaign.Targets
            .Where(t => t.Status is TargetStatus.SkippedNotMember or TargetStatus.SkippedDuplicate or TargetStatus.SkippedByUser
                or TargetStatus.Failed or TargetStatus.Inaccessible or TargetStatus.Cancelled or TargetStatus.OutcomeUnknown)
            .GroupBy(t => new { Category = t.Status.ToDisplay(), Reason = t.ErrorMessage ?? "(no details)" })
            .Select(g => new ReasonCountDto(g.Key.Category, g.Key.Reason, g.Count()))
            .OrderBy(r => r.Category)
            .ThenByDescending(r => r.Count)
            .ToList();

        return new CampaignReportDto(
            campaign.Id,
            campaign.Name,
            progress.ContentTitle,
            campaign.Status,
            campaign.StatusReason,
            campaign.RequireApproval,
            campaign.Pacing.Clone(),
            campaign.CompletedBatches,
            campaign.CreatedAtUtc,
            campaign.StartedAtUtc,
            campaign.CompletedAtUtc,
            progress.Counts,
            reasons,
            progress.Targets,
            DateTime.UtcNow);
    }

    public async Task<DashboardDto> GetDashboardAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);

        var totalGroups = await db.Groups.CountAsync(ct);
        var activeGroups = await db.Groups.CountAsync(g => g.IsActive, ct);
        var memberGroups = await db.Groups.CountAsync(g => g.LastMembershipStatus == MembershipStatus.Member, ct);
        var totalContents = await db.Contents.CountAsync(ct);
        var totalCampaigns = await db.Campaigns.CountAsync(ct);
        var totalPublished = await db.CampaignTargets.CountAsync(t => t.Status == TargetStatus.Published, ct);
        var totalFailed = await db.CampaignTargets.CountAsync(t => t.Status == TargetStatus.Failed, ct);
        var weekAgo = DateTime.UtcNow.AddDays(-7);
        var publishedLast7Days = await db.CampaignTargets.CountAsync(t => t.Status == TargetStatus.Published && t.PublishedAtUtc >= weekAgo, ct);

        var active = await db.Campaigns.AsNoTracking()
            .Where(c => c.Status == CampaignStatus.Running || c.Status == CampaignStatus.Queued)
            .OrderByDescending(c => c.Status == CampaignStatus.Running)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => new
            {
                c.Id, c.Name, ContentTitle = c.ContentPost!.Title, c.Status, c.StatusReason, c.CreatedAtUtc, c.StartedAtUtc, c.CompletedAtUtc,
                Statuses = c.Targets.Select(t => t.Status).ToList()
            })
            .FirstOrDefaultAsync(ct);

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == PublisherSettings.SingletonId, ct);

        var recent = await db.CampaignTargets.AsNoTracking()
            .Where(t => t.Status != TargetStatus.Pending)
            .OrderByDescending(t => t.UpdatedAtUtc)
            .Take(12)
            .Select(t => new ActivityDto(t.CampaignId, t.Campaign!.Name, t.GroupName, t.GroupUrl, t.Status, t.ErrorMessage, t.UpdatedAtUtc))
            .ToListAsync(ct);

        var attempted = totalPublished + totalFailed;
        return new DashboardDto(
            totalGroups,
            activeGroups,
            memberGroups,
            totalContents,
            totalCampaigns,
            totalPublished,
            totalFailed,
            publishedLast7Days,
            attempted == 0 ? 0 : Math.Round(totalPublished * 100.0 / attempted, 1),
            active is null
                ? null
                : new CampaignSummaryDto(active.Id, active.Name, active.ContentTitle, active.Status, active.StatusReason,
                    active.CreatedAtUtc, active.StartedAtUtc, active.CompletedAtUtc, CampaignCountsDto.From(active.Statuses)),
            settings?.SessionLastVerifiedResult,
            settings?.SessionLastVerifiedAtUtc,
            recent);
    }
}
