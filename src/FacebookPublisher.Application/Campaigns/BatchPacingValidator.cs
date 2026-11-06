using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;

namespace FacebookPublisher.Application.Campaigns;

public sealed record PacingError(string Field, string Message);

/// <summary>
/// Shared server/UI validation for batch and delay settings. The UI runs it on every change (instant feedback) and the
/// services run it again before saving or starting, so invalid values can never reach the campaign runner.
/// </summary>
public static class BatchPacingValidator
{
    public const string BatchSizeField = "BatchSize";
    public const string PostDelayField = "PostDelay";
    public const string BatchDelayField = "BatchDelay";
    public const string ExtendedDelayField = "ExtendedDelay";
    public const string ExtendedAfterField = "ExtendedAfter";

    public const int MaxBatchSize = 100;
    public const int MaxExtendedDelayAfterBatches = 1000;

    /// <summary>Lower bound for the wait between two posts; protects the account from rapid repeated posting.</summary>
    public const int MinimumPostDelaySeconds = 5;

    /// <summary>Upper bound for any entered number, checked before unit conversion so conversions cannot overflow.</summary>
    public const int MaxEnteredValue = 100_000;

    public static readonly TimeSpan MaxPostDelay = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxBatchDelay = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxExtendedDelay = TimeSpan.FromHours(72);

    public static readonly IReadOnlyList<DelayUnit> PostDelayUnits = [DelayUnit.Seconds, DelayUnit.Minutes];
    public static readonly IReadOnlyList<DelayUnit> BatchDelayUnits = [DelayUnit.Minutes, DelayUnit.Hours];
    public static readonly IReadOnlyList<DelayUnit> ExtendedDelayUnits = [DelayUnit.Minutes, DelayUnit.Hours];

    public static IReadOnlyList<PacingError> Validate(BatchPacingConfig? config)
    {
        var errors = new List<PacingError>();
        if (config is null)
        {
            errors.Add(new PacingError(BatchSizeField, "Batch and delay settings are missing."));
            return errors;
        }

        if (config.MinBatchSize < 1)
        {
            errors.Add(new PacingError(BatchSizeField, "Minimum batch size must be greater than zero."));
        }
        if (config.MaxBatchSize > MaxBatchSize)
        {
            errors.Add(new PacingError(BatchSizeField, $"Maximum batch size cannot exceed {MaxBatchSize}."));
        }
        if (config.MaxBatchSize < config.MinBatchSize)
        {
            errors.Add(new PacingError(BatchSizeField, "Maximum batch size must be greater than or equal to the minimum."));
        }

        ValidateRange(errors, PostDelayField, "Delay between posts", config.PostDelay, PostDelayUnits,
            TimeSpan.FromSeconds(MinimumPostDelaySeconds), MaxPostDelay);
        ValidateRange(errors, BatchDelayField, "Delay between batches", config.BatchDelay, BatchDelayUnits,
            TimeSpan.Zero, MaxBatchDelay);
        ValidateRange(errors, ExtendedDelayField, "Extended delay", config.ExtendedDelay, ExtendedDelayUnits,
            TimeSpan.Zero, MaxExtendedDelay);

        if (config.ExtendedDelayAfterBatches < 1 || config.ExtendedDelayAfterBatches > MaxExtendedDelayAfterBatches)
        {
            errors.Add(new PacingError(ExtendedAfterField,
                $"\"Apply extended delay after\" must be between 1 and {MaxExtendedDelayAfterBatches} batches."));
        }

        return errors;
    }

    private static void ValidateRange(
        List<PacingError> errors,
        string field,
        string label,
        DelayRange? range,
        IReadOnlyList<DelayUnit> allowedUnits,
        TimeSpan minimum,
        TimeSpan maximum)
    {
        if (range is null)
        {
            errors.Add(new PacingError(field, $"{label} is missing."));
            return;
        }
        if (!allowedUnits.Contains(range.Unit))
        {
            errors.Add(new PacingError(field, $"{label}: unit must be {string.Join(" or ", allowedUnits.Select(u => u.ToString().ToLowerInvariant()))}."));
            return;
        }
        if (range.Min < 0 || range.Max < 0)
        {
            errors.Add(new PacingError(field, $"{label}: values must be zero or greater."));
            return;
        }
        if (range.Min > MaxEnteredValue || range.Max > MaxEnteredValue)
        {
            errors.Add(new PacingError(field, $"{label}: values are too large."));
            return;
        }
        if (range.Max < range.Min)
        {
            errors.Add(new PacingError(field, $"{label}: maximum must be greater than or equal to the minimum."));
        }
        if (range.MinSeconds < minimum.TotalSeconds)
        {
            errors.Add(new PacingError(field, $"{label}: minimum must be at least {DurationText.Format(minimum)}."));
        }
        if (range.MaxSeconds > maximum.TotalSeconds)
        {
            errors.Add(new PacingError(field, $"{label}: maximum cannot exceed {DurationText.Format(maximum)}."));
        }
    }
}
