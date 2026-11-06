using FacebookPublisher.Domain.Entities;

namespace FacebookPublisher.Application.Campaigns;

/// <summary>
/// Estimates based on the midpoint of each configured range. Batch sizes and delays are random, so these are
/// approximations shown with "≈" in the UI, never used for execution decisions.
/// </summary>
public static class PacingEstimator
{
    /// <summary>Rough browser time per group (open group, check membership, compose, post).</summary>
    public static readonly TimeSpan WorkPerGroup = TimeSpan.FromSeconds(30);

    public static double AverageBatchSize(BatchPacingConfig p) => Math.Max(1, (p.MinBatchSize + p.MaxBatchSize) / 2.0);

    public static int EstimateTotalBatches(Campaign c, int remainingGroups)
    {
        var inProgress = c.CurrentBatchNumber > 0 && c.CurrentBatchProcessed < c.CurrentBatchSize ? 1 : 0;
        var leftInCurrent = inProgress == 1 ? c.CurrentBatchSize - c.CurrentBatchProcessed : 0;
        var afterCurrent = Math.Max(0, remainingGroups - leftInCurrent);
        return c.CompletedBatches + inProgress + (int)Math.Ceiling(afterCurrent / AverageBatchSize(c.Pacing));
    }

    public static TimeSpan EstimateDuration(int groupCount, BatchPacingConfig p)
    {
        if (groupCount <= 0)
        {
            return TimeSpan.Zero;
        }

        var batches = (int)Math.Ceiling(groupCount / AverageBatchSize(p));
        var postDelays = Math.Max(0, groupCount - batches);
        var gaps = Math.Max(0, batches - 1);
        var extendedGaps = p.ExtendedDelayAfterBatches > 0 ? gaps / p.ExtendedDelayAfterBatches : 0;
        var normalGaps = gaps - extendedGaps;

        var seconds = groupCount * WorkPerGroup.TotalSeconds
            + postDelays * (p.PostDelay.MinSeconds + p.PostDelay.MaxSeconds) / 2.0
            + normalGaps * (p.BatchDelay.MinSeconds + p.BatchDelay.MaxSeconds) / 2.0
            + extendedGaps * (p.ExtendedDelay.MinSeconds + p.ExtendedDelay.MaxSeconds) / 2.0;
        return TimeSpan.FromSeconds(seconds);
    }
}
