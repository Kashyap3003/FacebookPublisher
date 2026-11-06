using FacebookPublisher.Domain.Enums;

namespace FacebookPublisher.Domain.Entities;

/// <summary>
/// A min/max duration chosen by the user, kept in the unit they picked so the UI shows it back the same way.
/// Stored as owned columns; conversion to seconds happens only through <see cref="ToSeconds"/>.
/// </summary>
public class DelayRange
{
    public DelayRange()
    {
    }

    public DelayRange(int min, int max, DelayUnit unit)
    {
        Min = min;
        Max = max;
        Unit = unit;
    }

    public int Min { get; set; }

    public int Max { get; set; }

    public DelayUnit Unit { get; set; }

    public long MinSeconds => ToSeconds(Min, Unit);

    public long MaxSeconds => ToSeconds(Max, Unit);

    public DelayRange Clone() => new(Min, Max, Unit);

    public static long ToSeconds(int value, DelayUnit unit) => unit switch
    {
        DelayUnit.Seconds => value,
        DelayUnit.Minutes => value * 60L,
        DelayUnit.Hours => value * 3600L,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown delay unit.")
    };
}

/// <summary>
/// Batch processing and pacing configuration. Lives on <see cref="PublisherSettings"/> as the global default and is
/// copied onto each <see cref="Campaign"/> as an immutable snapshot, so changing settings never affects a campaign
/// that already exists.
/// </summary>
public class BatchPacingConfig
{
    public int MinBatchSize { get; set; } = 5;

    public int MaxBatchSize { get; set; } = 9;

    /// <summary>Wait between two groups inside the same batch.</summary>
    public DelayRange PostDelay { get; set; } = new(10, 30, DelayUnit.Seconds);

    /// <summary>Wait between two batches.</summary>
    public DelayRange BatchDelay { get; set; } = new(15, 30, DelayUnit.Minutes);

    /// <summary>After this many completed batches the extended delay replaces the normal batch delay.</summary>
    public int ExtendedDelayAfterBatches { get; set; } = 5;

    public DelayRange ExtendedDelay { get; set; } = new(3, 5, DelayUnit.Hours);

    public BatchPacingConfig Clone() => new()
    {
        MinBatchSize = MinBatchSize,
        MaxBatchSize = MaxBatchSize,
        PostDelay = PostDelay.Clone(),
        BatchDelay = BatchDelay.Clone(),
        ExtendedDelayAfterBatches = ExtendedDelayAfterBatches,
        ExtendedDelay = ExtendedDelay.Clone()
    };
}
