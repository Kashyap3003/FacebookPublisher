using System.Text.RegularExpressions;

namespace FacebookPublisher.Application.Groups;

/// <summary>Validates Facebook group links and converts them to one canonical form so duplicates can be detected.</summary>
public static partial class GroupUrlNormalizer
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "facebook.com", "www.facebook.com", "m.facebook.com", "web.facebook.com", "mbasic.facebook.com",
        "fb.com", "www.fb.com"
    };

    // Paths under /groups/ that are Facebook features, not individual groups.
    private static readonly HashSet<string> ReservedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "feed", "discover", "joins", "create", "notifications", "search", "your_groups"
    };

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{4,40}$")]
    private static partial Regex ShareCodeRegex();

    /// <summary>
    /// Recognises share links such as https://www.facebook.com/share/g/1FMskdPL2Z/. They carry no group address;
    /// Facebook only redirects them to the group for a logged-in browser, so they must be resolved there.
    /// The share code is case-sensitive and is preserved.
    /// </summary>
    public static bool TryParseShareLink(string? input, out string shareUrl)
    {
        shareUrl = string.Empty;
        var value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return false;
        }
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !AllowedHosts.Contains(uri.Host))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? code = segments switch
        {
            ["share", "g", var c, ..] => c,
            ["share", var c, ..] when !c.Equals("g", StringComparison.OrdinalIgnoreCase) => c,
            _ => null
        };
        if (code is null || !ShareCodeRegex().IsMatch(code))
        {
            return false;
        }

        shareUrl = segments[1].Equals("g", StringComparison.OrdinalIgnoreCase)
            ? $"https://www.facebook.com/share/g/{code}/"
            : $"https://www.facebook.com/share/{code}/";
        return true;
    }

    public static bool TryNormalize(string? input, out string normalizedUrl, out string? error)
    {
        normalizedUrl = string.Empty;
        var value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            error = "URL is empty.";
            return false;
        }

        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "Not a valid web address.";
            return false;
        }

        if (!AllowedHosts.Contains(uri.Host))
        {
            error = "Only facebook.com group links are supported.";
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && segments[0].Equals("share", StringComparison.OrdinalIgnoreCase))
        {
            error = "Share links (facebook.com/share/...) must be resolved in the logged-in browser first.";
            return false;
        }

        if (segments.Length < 2 || !segments[0].Equals("groups", StringComparison.OrdinalIgnoreCase))
        {
            error = "Link must point to a group, e.g. https://www.facebook.com/groups/123456789.";
            return false;
        }

        var identifier = Uri.UnescapeDataString(segments[1]);
        if (ReservedSegments.Contains(identifier) || !IdentifierRegex().IsMatch(identifier))
        {
            error = "The group identifier in the link is not valid.";
            return false;
        }

        normalizedUrl = $"https://www.facebook.com/groups/{identifier.ToLowerInvariant()}";
        error = null;
        return true;
    }

    public static string IdentifierOf(string normalizedUrl) =>
        normalizedUrl[(normalizedUrl.LastIndexOf('/') + 1)..];
}
