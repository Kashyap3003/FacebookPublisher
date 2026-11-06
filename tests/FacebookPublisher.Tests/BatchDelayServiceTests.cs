using FacebookPublisher.Application.Campaigns;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;
using FacebookPublisher.Infrastructure.Campaigns;

namespace FacebookPublisher.Tests;

public class BatchDelayServiceTests
{
    private static readonly BatchPacingConfig Defaults = new();

    [Fact]
    public void Defaults_match_the_requested_configuration()
    {
        Assert.Equal((5, 9), (Defaults.MinBatchSize, Defaults.MaxBatchSize));
        Assert.Equal((10, 30, DelayUnit.Seconds), (Defaults.PostDelay.Min, Defaults.PostDelay.Max, Defaults.PostDelay.Unit));
        Assert.Equal((15, 30, DelayUnit.Minutes), (Defaults.BatchDelay.Min, Defaults.BatchDelay.Max, Defaults.BatchDelay.Unit));
        Assert.Equal((3, 5, DelayUnit.Hours), (Defaults.ExtendedDelay.Min, Defaults.ExtendedDelay.Max, Defaults.ExtendedDelay.Unit));
        Assert.Equal(5, Defaults.ExtendedDelayAfterBatches);
    }

    [Fact]
    public void Bounds_are_inclusive_and_converted_to_whole_seconds()
    {
        var min = new BatchDelayService(FixedRandom.AlwaysMin());
        var max = new BatchDelayService(FixedRandom.AlwaysMax());

        Assert.Equal(5, min.GenerateRandomBatchSize(Defaults));
        Assert.Equal(9, max.GenerateRandomBatchSize(Defaults));
        Assert.Equal(TimeSpan.FromSeconds(10), min.GenerateRandomPostDelay(Defaults));
        Assert.Equal(TimeSpan.FromSeconds(30), max.GenerateRandomPostDelay(Defaults));
        Assert.Equal(TimeSpan.FromMinutes(15), min.GenerateRandomBatchDelay(Defaults));
        Assert.Equal(TimeSpan.FromMinutes(30), max.GenerateRandomBatchDelay(Defaults));
        Assert.Equal(TimeSpan.FromHours(3), min.GenerateRandomExtendedDelay(Defaults));
        Assert.Equal(TimeSpan.FromHours(5), max.GenerateRandomExtendedDelay(Defaults));
    }

    [Fact]
    public void Ranges_requested_from_the_random_source_are_in_seconds()
    {
        var random = FixedRandom.AlwaysMin();
        var service = new BatchDelayService(random);

        service.GenerateRandomPostDelay(Defaults);
        service.GenerateRandomBatchDelay(Defaults);
        service.GenerateRandomExtendedDelay(Defaults);

        Assert.Equal([(10, 30), (900, 1800), (10800, 18000)], random.Requests);
    }

    [Fact]
    public void Real_random_values_always_stay_within_the_configured_range_and_hit_both_ends()
    {
        var service = new BatchDelayService(new SystemRandomSource());
        var config = new BatchPacingConfig
        {
            MinBatchSize = 5,
            MaxBatchSize = 9,
            PostDelay = new DelayRange(10, 12, DelayUnit.Seconds),
            BatchDelay = new DelayRange(1, 2, DelayUnit.Minutes),
            ExtendedDelay = new DelayRange(1, 2, DelayUnit.Hours)
        };

        var sizes = Enumerable.Range(0, 2000).Select(_ => service.GenerateRandomBatchSize(config)).ToList();
        var posts = Enumerable.Range(0, 2000).Select(_ => service.GenerateRandomPostDelay(config)).ToList();
        var batches = Enumerable.Range(0, 2000).Select(_ => service.GenerateRandomBatchDelay(config)).ToList();
        var extended = Enumerable.Range(0, 2000).Select(_ => service.GenerateRandomExtendedDelay(config)).ToList();

        Assert.All(sizes, s => Assert.InRange(s, 5, 9));
        Assert.Equal([5, 6, 7, 8, 9], sizes.Distinct().Order());
        Assert.All(posts, d => Assert.InRange(d, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12)));
        Assert.Equal(3, posts.Distinct().Count());
        Assert.All(batches, d => Assert.InRange(d, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)));
        Assert.All(extended, d => Assert.InRange(d, TimeSpan.FromHours(1), TimeSpan.FromHours(2)));
        Assert.All(posts.Concat(batches).Concat(extended), d => Assert.Equal(0, d.Ticks % TimeSpan.TicksPerSecond));
    }

    [Fact]
    public void Equal_minimum_and_maximum_gives_that_value()
    {
        var service = new BatchDelayService(new SystemRandomSource());
        var config = new BatchPacingConfig { MinBatchSize = 7, MaxBatchSize = 7, PostDelay = new DelayRange(20, 20, DelayUnit.Seconds) };

        Assert.Equal(7, service.GenerateRandomBatchSize(config));
        Assert.Equal(TimeSpan.FromSeconds(20), service.GenerateRandomPostDelay(config));
    }
}
