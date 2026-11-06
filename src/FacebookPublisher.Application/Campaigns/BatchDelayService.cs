using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Domain.Entities;

namespace FacebookPublisher.Application.Campaigns;

/// <summary>Random batch sizes and delays for campaign pacing. Separate from the runner so it can be tested on its own.</summary>
public interface IBatchDelayService
{
    int GenerateRandomBatchSize(BatchPacingConfig config);

    TimeSpan GenerateRandomPostDelay(BatchPacingConfig config);

    TimeSpan GenerateRandomBatchDelay(BatchPacingConfig config);

    TimeSpan GenerateRandomExtendedDelay(BatchPacingConfig config);
}

/// <summary>Picks inclusive random values; delays are chosen in whole seconds within the configured range.</summary>
public sealed class BatchDelayService(IRandomSource random) : IBatchDelayService
{
    public int GenerateRandomBatchSize(BatchPacingConfig config) =>
        random.NextInclusive(config.MinBatchSize, config.MaxBatchSize);

    public TimeSpan GenerateRandomPostDelay(BatchPacingConfig config) => RandomDuration(config.PostDelay);

    public TimeSpan GenerateRandomBatchDelay(BatchPacingConfig config) => RandomDuration(config.BatchDelay);

    public TimeSpan GenerateRandomExtendedDelay(BatchPacingConfig config) => RandomDuration(config.ExtendedDelay);

    private TimeSpan RandomDuration(DelayRange range)
    {
        // Validated ranges are at most 72 hours (259,200 s), so the checked conversion cannot overflow.
        var min = checked((int)range.MinSeconds);
        var max = checked((int)range.MaxSeconds);
        return TimeSpan.FromSeconds(random.NextInclusive(min, max));
    }
}
