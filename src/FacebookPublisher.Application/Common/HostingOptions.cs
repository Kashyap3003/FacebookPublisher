namespace FacebookPublisher.Application.Common;

/// <summary>Deployment switches. Defaults suit running on your own PC.</summary>
public sealed class HostingOptions
{
    public const string SectionName = "Hosting";

    /// <summary>When false, no browser is launched and campaigns stop with an explanatory message (online demo).</summary>
    public bool AutomationEnabled { get; set; } = true;
}
