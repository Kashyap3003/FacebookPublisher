namespace FacebookPublisher.Application.Common;

/// <summary>Deployment switches. Defaults suit running on your own PC; the cloud demo turns automation off and login on.</summary>
public sealed class HostingOptions
{
    public const string SectionName = "Hosting";

    /// <summary>When false, no browser is launched and campaigns stop with an explanatory message (cloud demo).</summary>
    public bool AutomationEnabled { get; set; } = true;

    /// <summary>When true, every page requires the username/password below.</summary>
    public bool RequireLogin { get; set; }

    public string LoginUsername { get; set; } = string.Empty;

    public string LoginPassword { get; set; } = string.Empty;
}
