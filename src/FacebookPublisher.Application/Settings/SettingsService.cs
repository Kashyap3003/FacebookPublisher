using System.ComponentModel.DataAnnotations;
using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Campaigns;
using FacebookPublisher.Application.Common;
using FacebookPublisher.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FacebookPublisher.Application.Settings;

/// <summary>Editable settings form model.</summary>
public sealed class SettingsModel
{
    /// <summary>Default batch processing and delay settings for new campaigns.</summary>
    public BatchPacingConfig Pacing { get; set; } = new();

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
            Pacing = s.Pacing.Clone(),
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
        messages.AddRange(BatchPacingValidator.Validate(model.Pacing).Select(e => e.Message));
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

        CopyPacing(model.Pacing, s.Pacing);
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

    /// <summary>Copies values into the tracked owned instance (rather than replacing it) so EF updates the columns in place.</summary>
    private static void CopyPacing(BatchPacingConfig from, BatchPacingConfig to)
    {
        to.MinBatchSize = from.MinBatchSize;
        to.MaxBatchSize = from.MaxBatchSize;
        to.PostDelay.Min = from.PostDelay.Min;
        to.PostDelay.Max = from.PostDelay.Max;
        to.PostDelay.Unit = from.PostDelay.Unit;
        to.BatchDelay.Min = from.BatchDelay.Min;
        to.BatchDelay.Max = from.BatchDelay.Max;
        to.BatchDelay.Unit = from.BatchDelay.Unit;
        to.ExtendedDelayAfterBatches = from.ExtendedDelayAfterBatches;
        to.ExtendedDelay.Min = from.ExtendedDelay.Min;
        to.ExtendedDelay.Max = from.ExtendedDelay.Max;
        to.ExtendedDelay.Unit = from.ExtendedDelay.Unit;
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
