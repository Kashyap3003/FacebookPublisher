namespace FacebookPublisher.Web;

/// <summary>Display helpers. The app runs on the user's machine, so server local time is the user's local time.</summary>
public static class Fmt
{
    public static string Time(DateTime? utc) =>
        utc is null ? "—" : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime().ToString("dd MMM yyyy HH:mm:ss");

    public static string Clock(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss");

    public static string Relative(DateTime? utc)
    {
        if (utc is null)
        {
            return "never";
        }
        var span = DateTime.UtcNow - DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
        return span.TotalSeconds switch
        {
            < 60 => "just now",
            < 3600 => $"{(int)span.TotalMinutes} min ago",
            < 86400 => $"{(int)span.TotalHours} h ago",
            _ => $"{(int)span.TotalDays} d ago"
        };
    }

    public static string Duration(DateTime? startUtc, DateTime? endUtc)
    {
        if (startUtc is null)
        {
            return "—";
        }
        var span = (endUtc ?? DateTime.UtcNow) - startUtc.Value;
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{span.Minutes}m {span.Seconds}s";
    }
}
