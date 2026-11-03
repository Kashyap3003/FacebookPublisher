using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Automation;
using FacebookPublisher.Application.Common;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FacebookPublisher.Application.Groups;

public sealed record GroupDto(
    Guid Id,
    string Name,
    string Url,
    string? Notes,
    bool IsActive,
    MembershipStatus LastMembershipStatus,
    DateTime? LastCheckedAtUtc,
    DateTime CreatedAtUtc);

public sealed record SaveGroupRequest(string? Name, string Url, string? Notes, bool IsActive = true);

public sealed record BulkAddResult(int Added, IReadOnlyList<string> Duplicates, IReadOnlyList<string> Invalid, IReadOnlyList<string> Resolved);

public sealed class GroupService(
    IApplicationDbContextFactory dbFactory,
    IFacebookAutomation automation,
    ICampaignExecutionTracker tracker,
    ILogger<GroupService> logger)
{
    public async Task<IReadOnlyList<GroupDto>> ListAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var query = db.Groups.AsNoTracking();
        if (activeOnly)
        {
            query = query.Where(g => g.IsActive);
        }
        return await query
            .OrderBy(g => g.Name)
            .Select(g => new GroupDto(g.Id, g.Name, g.Url, g.Notes, g.IsActive, g.LastMembershipStatus, g.LastCheckedAtUtc, g.CreatedAtUtc))
            .ToListAsync(ct);
    }

    public static bool IsShareLink(string? text) => GroupUrlNormalizer.TryParseShareLink(text, out _);

    public async Task<GroupDto> AddAsync(SaveGroupRequest request, CancellationToken ct = default)
    {
        var (url, detectedName) = await ResolveUrlAsync(request.Url, ct);
        await using var db = await dbFactory.CreateAsync(ct);
        if (await db.Groups.AnyAsync(g => g.Url == url, ct))
        {
            throw new AppValidationException($"The group {url} is already in the list.");
        }

        var group = new FacebookGroup
        {
            Name = CleanName(string.IsNullOrWhiteSpace(request.Name) ? detectedName : request.Name, url),
            Url = url,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            IsActive = request.IsActive
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Added group {GroupId} {GroupUrl}", group.Id, group.Url);
        return ToDto(group);
    }

    /// <summary>
    /// Adds one group per line. A line may be "URL" or "Name | URL". Share links are opened in the logged-in browser
    /// to find the group's address; if Facebook requires login, the remaining share links are reported instead.
    /// </summary>
    public async Task<BulkAddResult> AddBulkAsync(string text, CancellationToken ct = default)
    {
        var duplicates = new List<string>();
        var invalid = new List<string>();
        var resolvedLinks = new List<string>();
        var added = 0;
        string? haltMessage = null;

        await using var db = await dbFactory.CreateAsync(ct);
        var existing = (await db.Groups.Select(g => g.Url).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? name = null;
            var urlPart = rawLine;
            var separator = rawLine.IndexOf('|');
            if (separator >= 0)
            {
                name = rawLine[..separator].Trim();
                urlPart = rawLine[(separator + 1)..].Trim();
            }

            string url;
            string? detectedName = null;
            if (GroupUrlNormalizer.TryNormalize(urlPart, out var normalized, out var error))
            {
                url = normalized;
            }
            else if (GroupUrlNormalizer.TryParseShareLink(urlPart, out _))
            {
                if (haltMessage is not null)
                {
                    invalid.Add($"{rawLine} — not resolved: {haltMessage}");
                    continue;
                }
                try
                {
                    (url, detectedName) = await ResolveUrlAsync(urlPart, ct);
                    resolvedLinks.Add($"{urlPart} → {url}{(detectedName is null ? "" : $" ({detectedName})")}");
                }
                catch (AutomationHaltedException ex)
                {
                    haltMessage = $"{ex.Message} Log in from Settings, then add the share links again.";
                    invalid.Add($"{rawLine} — not resolved: {haltMessage}");
                    continue;
                }
                catch (AppValidationException ex)
                {
                    invalid.Add($"{rawLine} — {ex.Message}");
                    continue;
                }
            }
            else
            {
                invalid.Add($"{rawLine} — {error}");
                continue;
            }

            if (!existing.Add(url))
            {
                duplicates.Add(url);
                continue;
            }

            db.Groups.Add(new FacebookGroup { Name = CleanName(string.IsNullOrWhiteSpace(name) ? detectedName : name, url), Url = url });
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }
        logger.LogInformation("Bulk add: {Added} added, {Resolved} share links resolved, {Duplicates} duplicates, {Invalid} invalid",
            added, resolvedLinks.Count, duplicates.Count, invalid.Count);
        return new BulkAddResult(added, duplicates, invalid, resolvedLinks);
    }

    public async Task<GroupDto> UpdateAsync(Guid id, SaveGroupRequest request, CancellationToken ct = default)
    {
        var (url, _) = await ResolveUrlAsync(request.Url, ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundException("Group", id);

        if (!string.Equals(group.Url, url, StringComparison.OrdinalIgnoreCase))
        {
            if (await db.Groups.AnyAsync(g => g.Url == url && g.Id != id, ct))
            {
                throw new AppValidationException($"The group {url} is already in the list.");
            }
            group.Url = url;
            group.LastMembershipStatus = MembershipStatus.Unknown;
            group.LastCheckedAtUtc = null;
        }

        group.Name = CleanName(request.Name, url);
        group.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        group.IsActive = request.IsActive;
        group.Touch();
        await db.SaveChangesAsync(ct);
        return ToDto(group);
    }

    public async Task SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundException("Group", id);
        group.IsActive = isActive;
        group.Touch();
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundException("Group", id);

        var inActiveCampaign = await db.CampaignTargets.AnyAsync(
            t => t.FacebookGroupId == id &&
                 (t.Campaign!.Status == CampaignStatus.Queued || t.Campaign.Status == CampaignStatus.Running), ct);
        if (inActiveCampaign)
        {
            throw new AppValidationException("This group is part of a running or queued campaign. Cancel the campaign first.");
        }

        // Campaign history keeps its snapshot of the name/URL; the FK is set to null by the database.
        db.Groups.Remove(group);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Deleted group {GroupId} {GroupUrl}", id, group.Url);
    }

    /// <summary>Opens the group in the automated browser and records whether the account can access it. Never joins.</summary>
    public async Task<MembershipCheckResult> VerifyAccessAsync(Guid id, CancellationToken ct = default)
    {
        if (tracker.ActiveCampaignId is not null)
        {
            throw new AppValidationException("A campaign is running. Access checks are disabled until it finishes.");
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundException("Group", id);

        var result = await automation.CheckMembershipAsync(group.Url, progress: null, ct);
        group.RecordMembership(result.Status, result.GroupName);
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Returns the canonical group URL, opening share links in the logged-in browser to find it.</summary>
    private async Task<(string Url, string? DetectedName)> ResolveUrlAsync(string input, CancellationToken ct)
    {
        if (GroupUrlNormalizer.TryNormalize(input, out var normalized, out var error))
        {
            return (normalized, null);
        }
        if (!GroupUrlNormalizer.TryParseShareLink(input, out var shareUrl))
        {
            throw new AppValidationException(error!);
        }
        if (tracker.ActiveCampaignId is not null)
        {
            throw new AppValidationException("Share links are opened in the automation browser, which is busy with a running campaign. Add them after it finishes.");
        }

        var resolved = await automation.ResolveLinkAsync(shareUrl, ct);
        if (resolved is null)
        {
            throw new AppValidationException("The share link did not open a Facebook group (it may be expired, private, or not a group link).");
        }
        if (!GroupUrlNormalizer.TryNormalize(resolved.Url, out normalized, out _))
        {
            throw new AppValidationException($"The share link opened {resolved.Url}, which is not a group address.");
        }
        logger.LogInformation("Resolved share link {ShareUrl} to {GroupUrl}", shareUrl, normalized);
        return (normalized, resolved.GroupName);
    }

    private static string CleanName(string? name, string url) =>
        string.IsNullOrWhiteSpace(name) ? GroupUrlNormalizer.IdentifierOf(url) : name.Trim()[..Math.Min(name.Trim().Length, 200)];

    private static GroupDto ToDto(FacebookGroup g) =>
        new(g.Id, g.Name, g.Url, g.Notes, g.IsActive, g.LastMembershipStatus, g.LastCheckedAtUtc, g.CreatedAtUtc);
}
