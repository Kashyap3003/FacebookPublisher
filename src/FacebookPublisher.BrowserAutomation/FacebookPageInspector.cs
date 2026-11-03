using FacebookPublisher.Application.Automation;
using Microsoft.Playwright;

namespace FacebookPublisher.BrowserAutomation;

/// <summary>Read-only checks against the current page. Never clicks anything.</summary>
internal sealed class FacebookPageInspector(FacebookAutomationOptions options)
{
    private readonly FacebookPhrases _phrases = options.Phrases;

    /// <summary>Throws when the page shows a login wall, security checkpoint or restriction notice.</summary>
    public async Task ThrowIfHaltRequiredAsync(IPage page)
    {
        var url = page.Url;
        if (_phrases.CheckpointUrlFragments.Any(f => url.Contains(f, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AutomationHaltedException(HaltReason.Checkpoint, "Facebook redirected to a security checkpoint.");
        }

        if (_phrases.LoginUrlFragments.Any(f => url.Contains(f, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AutomationHaltedException(HaltReason.SessionExpired, "Facebook redirected to the login page.");
        }

        var restriction = await FirstNoticePhraseAsync(page, _phrases.Restriction);
        if (restriction is not null)
        {
            throw new AutomationHaltedException(HaltReason.Restricted, $"Facebook displayed: \"{restriction}\".");
        }
    }

    public Task<string?> FirstVisibleInaccessiblePhraseAsync(IPage page) => FirstNoticePhraseAsync(page, _phrases.Inaccessible);

    public async Task<bool> AnyPhraseVisibleAsync(IPage page, IEnumerable<string> phrases) =>
        await FirstNoticePhraseAsync(page, phrases) is not null;

    /// <summary>
    /// Looks for a phrase in dialogs/alerts, or anywhere on the page when no feed is shown (full-page notices).
    /// Group feeds are excluded because posts written by members can contain the same wording.
    /// </summary>
    private static async Task<string?> FirstNoticePhraseAsync(IPage page, IEnumerable<string> phrases)
    {
        var list = phrases as IReadOnlyCollection<string> ?? phrases.ToList();
        var notice = await FirstVisiblePhraseAsync(page.Locator("[role='dialog'], [role='alert']"), list);
        if (notice is not null)
        {
            return notice;
        }

        var feedVisible = await page.Locator("[role='feed']").First.IsVisibleAsync();
        return feedVisible ? null : await FirstVisiblePhraseAsync(page.Locator("body"), list);
    }

    public static async Task<bool> AnyVisibleAsync(IPage page, IEnumerable<string> selectors) =>
        await FirstVisibleAsync(page.Locator("html"), selectors) is not null;

    public static async Task<ILocator?> FirstVisibleAsync(ILocator scope, IEnumerable<string> selectors)
    {
        foreach (var selector in selectors)
        {
            var locator = scope.Locator(selector).First;
            try
            {
                if (await locator.IsVisibleAsync())
                {
                    return locator;
                }
            }
            catch (PlaywrightException)
            {
                // An invalid selector in configuration should not stop other selectors from being tried.
            }
        }
        return null;
    }

    public static async Task<string?> ReadGroupNameAsync(IPage page)
    {
        try
        {
            var heading = page.Locator("div[role='main'] h1").First;
            if (await heading.IsVisibleAsync())
            {
                var text = (await heading.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 2000 })).Trim();
                if (text.Length > 0)
                {
                    return text;
                }
            }
        }
        catch (PlaywrightException)
        {
        }
        catch (TimeoutException)
        {
        }

        var title = await page.TitleAsync();
        if (title.StartsWith('(') && title.IndexOf(')') is var close and > 0)
        {
            title = title[(close + 1)..];
        }
        title = title.Replace("| Facebook", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return title.Length == 0 || title.Equals("Facebook", StringComparison.OrdinalIgnoreCase) ? null : title;
    }

    private static async Task<string?> FirstVisiblePhraseAsync(ILocator scope, IEnumerable<string> phrases)
    {
        foreach (var phrase in phrases)
        {
            try
            {
                if (await scope.GetByText(phrase).First.IsVisibleAsync())
                {
                    return phrase;
                }
            }
            catch (PlaywrightException)
            {
            }
        }
        return null;
    }
}
