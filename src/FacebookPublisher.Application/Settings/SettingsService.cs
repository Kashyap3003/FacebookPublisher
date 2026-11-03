using System.ComponentModel.DataAnnotations;
using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Common;
using FacebookPublisher.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FacebookPublisher.Application.Settings;

/// <summary>Editable settings form model.</summary>
public sealed class SettingsModel
{
    [Range(5, 3600, ErrorMessage = "Minimum delay must be between 5 and 3600 seconds.")]
    public int DefaultMinDelaySeconds { get; set; }

    [Range(5, 3600, ErrorMessage = "Maximum delay must be between 5 and 3600 seconds.")]
    public int DefaultMaxDelaySeconds { get; set; }

    public bool RequireApprovalByDefault { get; set; }

    [Range(1, 120, ErrorMessage = "Approval timeout must be between 1 and 120 minutes.")]
    public int ApprovalTimeoutMinutes { get; set; }

    [Range(1, 200, ErrorMessage = "Maximum groups per campaign must be between 1 and 200.")]
    public int MaxGroupsPerCampaign { get; set; }

    [Range(0, 720, ErrorMessage = "Duplicate window must be between 0 and 720 hours.")]
    public int DuplicateWindowHours { get; set; }

    [Required(ErrorMessage = "Browser profile folder is required.")]
    public string BrowserProfilePath { get; set; } = string.Empty;

    public string BrowserChannel { get; set; } = string.Empty;

    public bool Headless { get; set; }

    [Range(10, 300, ErrorMessage = "Navigation timeout must be between 10 and 300 seconds.")]
    public int NavigationTimeoutSeconds { get; set; }

    [Range(30, 3600, ErrorMessage = "Upload timeout must be between 30 and 3600 seconds.")]
    public int UploadTimeoutSeconds { get; set; }

    public DateTime? SessionLastVerifiedAtUtc { get; set; }

    public bool? SessionLastVerifiedResult { get; set; }
}

public sealed class SettingsService(IApplicationDbContextFactory dbFactory, ILogger<SettingsService> logger)
{
    public static readonly IReadOnlyList<(string Value, string Label)> BrowserChannels =
    [
        ("chrome", "Google Chrome (installed)"),
        ("msedge", "Microsoft Edge (installed)"),
        ("", "Bundled Chromium (downloaded by Playwright)")
    ];

    public async Task<PublisherSettings> GetEntityAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == PublisherSettings.SingletonId, ct)
            ?? CreateDefaults();
    }

    public async Task<SettingsModel> GetAsync(CancellationToken ct = default)
    {
        var s = await GetEntityAsync(ct);
        return new SettingsModel
        {
            DefaultMinDelaySeconds = s.DefaultMinDelaySeconds,
            DefaultMaxDelaySeconds = s.DefaultMaxDelaySeconds,
            RequireApprovalByDefault = s.RequireApprovalByDefault,
            ApprovalTimeoutMinutes = s.ApprovalTimeoutMinutes,
            MaxGroupsPerCampaign = s.MaxGroupsPerCampaign,
            DuplicateWindowHours = s.DuplicateWindowHours,
            BrowserProfilePath = s.BrowserProfilePath,
            BrowserChannel = s.BrowserChannel,
            Headless = s.Headless,
            NavigationTimeoutSeconds = s.NavigationTimeoutSeconds,
            UploadTimeoutSeconds = s.UploadTimeoutSeconds,
            SessionLastVerifiedAtUtc = s.SessionLastVerifiedAtUtc,
            SessionLastVerifiedResult = s.SessionLastVerifiedResult
        };
    }

    public async Task SaveAsync(SettingsModel model, CancellationToken ct = default)
    {
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), errors, validateAllProperties: true);
        var messages = errors.Select(e => e.ErrorMessage ?? "Invalid value.").ToList();
        if (model.DefaultMaxDelaySeconds < model.DefaultMinDelaySeconds)
        {
            messages.Add("Maximum delay must be greater than or equal to the minimum delay.");
        }
        if (SettingsService.BrowserChannels.All(c => c.Value != (model.BrowserChannel ?? string.Empty)))
        {
            messages.Add("Unknown browser channel.");
        }
        if (messages.Count > 0)
        {
            throw new AppValidationException("Settings are not valid.", messages);
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var s = await db.Settings.FirstOrDefaultAsync(x => x.Id == PublisherSettings.SingletonId, ct);
        if (s is null)
        {
            s = CreateDefaults();
            db.Settings.Add(s);
        }

        s.DefaultMinDelaySeconds = model.DefaultMinDelaySeconds;
        s.DefaultMaxDelaySeconds = model.DefaultMaxDelaySeconds;
        s.RequireApprovalByDefault = model.RequireApprovalByDefault;
        s.ApprovalTimeoutMinutes = model.ApprovalTimeoutMinutes;
        s.MaxGroupsPerCampaign = model.MaxGroupsPerCampaign;
        s.DuplicateWindowHours = model.DuplicateWindowHours;
        s.BrowserProfilePath = model.BrowserProfilePath.Trim();
        s.BrowserChannel = model.BrowserChannel ?? string.Empty;
        s.Headless = model.Headless;
        s.NavigationTimeoutSeconds = model.NavigationTimeoutSeconds;
        s.UploadTimeoutSeconds = model.UploadTimeoutSeconds;
        s.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Settings updated");
    }

    public async Task RecordSessionCheckAsync(bool isLoggedIn, DateTime checkedAtUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var s = await db.Settings.FirstOrDefaultAsync(x => x.Id == PublisherSettings.SingletonId, ct);
        if (s is null)
        {
            return;
        }
        s.SessionLastVerifiedAtUtc = checkedAtUtc;
        s.SessionLastVerifiedResult = isLoggedIn;
        await db.SaveChangesAsync(ct);
    }

    public static PublisherSettings CreateDefaults() => new()
    {
        BrowserProfilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FacebookPublisher",
            "BrowserProfile"),
        BrowserChannel = "chrome",
        RequireApprovalByDefault = false
    };
}
