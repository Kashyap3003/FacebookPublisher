using FacebookPublisher.Application.Campaigns;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;

namespace FacebookPublisher.Tests;

public class BatchPacingValidatorTests
{
    [Fact]
    public void Default_configuration_is_valid()
    {
        Assert.Empty(BatchPacingValidator.Validate(new BatchPacingConfig()));
    }

    public static TheoryData<string, Action<BatchPacingConfig>, string> InvalidConfigurations => new()
    {
        { "zero batch size", c => c.MinBatchSize = 0, BatchPacingValidator.BatchSizeField },
        { "max batch below min", c => { c.MinBatchSize = 8; c.MaxBatchSize = 6; }, BatchPacingValidator.BatchSizeField },
        { "huge batch", c => c.MaxBatchSize = 1000, BatchPacingValidator.BatchSizeField },
        { "negative post delay", c => c.PostDelay.Min = -1, BatchPacingValidator.PostDelayField },
        { "post delay max below min", c => { c.PostDelay.Min = 30; c.PostDelay.Max = 10; }, BatchPacingValidator.PostDelayField },
        { "post delay under 5 s", c => { c.PostDelay.Min = 2; }, BatchPacingValidator.PostDelayField },
        { "post delay over 1 h", c => { c.PostDelay.Unit = DelayUnit.Minutes; c.PostDelay.Max = 90; }, BatchPacingValidator.PostDelayField },
        { "post delay in hours", c => c.PostDelay.Unit = DelayUnit.Hours, BatchPacingValidator.PostDelayField },
        { "batch delay in seconds", c => c.BatchDelay.Unit = DelayUnit.Seconds, BatchPacingValidator.BatchDelayField },
        { "batch delay max below min", c => { c.BatchDelay.Min = 30; c.BatchDelay.Max = 15; }, BatchPacingValidator.BatchDelayField },
        { "batch delay over 24 h", c => { c.BatchDelay.Unit = DelayUnit.Hours; c.BatchDelay.Max = 25; }, BatchPacingValidator.BatchDelayField },
        { "extended over 72 h", c => c.ExtendedDelay.Max = 100, BatchPacingValidator.ExtendedDelayField },
        { "overflowing value", c => c.ExtendedDelay.Max = int.MaxValue, BatchPacingValidator.ExtendedDelayField },
        { "extended after zero", c => c.ExtendedDelayAfterBatches = 0, BatchPacingValidator.ExtendedAfterField },
        { "extended after too many", c => c.ExtendedDelayAfterBatches = 5000, BatchPacingValidator.ExtendedAfterField }
    };

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void Invalid_configurations_are_rejected_with_a_message_for_the_right_field(string _, Action<BatchPacingConfig> mutate, string field)
    {
        var config = new BatchPacingConfig();
        mutate(config);

        var errors = BatchPacingValidator.Validate(config);

        Assert.Contains(errors, e => e.Field == field && !string.IsNullOrWhiteSpace(e.Message));
    }

    [Fact]
    public void Zero_batch_and_extended_delays_are_allowed()
    {
        var config = new BatchPacingConfig
        {
            BatchDelay = new DelayRange(0, 0, DelayUnit.Minutes),
            ExtendedDelay = new DelayRange(0, 0, DelayUnit.Hours)
        };

        Assert.Empty(BatchPacingValidator.Validate(config));
    }
}
