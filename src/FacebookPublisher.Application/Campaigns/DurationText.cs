using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;

namespace FacebookPublisher.Application.Campaigns;

/// <summary>Human-readable durations and pacing summaries, shared by messages, the UI and reports.</summary>
public static class DurationText
{
    public static string Format(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }
        if (duration.TotalHours >= 1)
        {
            var hours = (int)duration.TotalHours;
            return duration.Minutes == 0 ? $"{hours} h" : $"{hours} h {duration.Minutes} min";
        }
        if (duration.TotalMinutes >= 1)
        {
            var minutes = (int)duration.TotalMinutes;
            return duration.Seconds == 0 ? $"{minutes} min" : $"{minutes} min {duration.Seconds} s";
        }
        return $"{(int)duration.TotalSeconds} s";
    }

    public static string Format(DelayRange range) =>
        $"{range.Min}–{range.Max} {UnitText(range.Unit)}";

    public static string UnitText(DelayUnit unit) => unit switch
    {
        DelayUnit.Seconds => "seconds",
        DelayUnit.Minutes => "minutes",
        DelayUnit.Hours => "hours",
        _ => unit.ToString()
    };

    public static string Describe(DelayKind kind) => kind switch
    {
        DelayKind.BetweenPosts => "Between posts",
        DelayKind.BetweenBatches => "Between batches",
        DelayKind.Extended => "Extended break",
        _ => "None"
    };

    public static string Summary(BatchPacingConfig p) =>
        $"Batches of {p.MinBatchSize}–{p.MaxBatchSize} groups · {Format(p.PostDelay)} between posts · " +
        $"{Format(p.BatchDelay)} between batches · {Format(p.ExtendedDelay)} after every {p.ExtendedDelayAfterBatches} batches";
}
