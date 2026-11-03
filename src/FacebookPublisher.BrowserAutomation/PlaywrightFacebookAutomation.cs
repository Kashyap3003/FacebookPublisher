using FacebookPublisher.Application.Automation;
using FacebookPublisher.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace FacebookPublisher.BrowserAutomation;

/// <summary>
/// Drives Facebook's normal web UI in the user's own logged-in browser profile. It performs the same steps a person
/// would (open group, open composer, type, attach, click Post) and stops at the first sign that Facebook wants
/// a human: login wall, checkpoint, or restriction notice. It never joins groups, solves challenges, or masks automation.
/// </summary>
public sealed class PlaywrightFacebookAutomation : IFacebookAutomation
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly BrowserSessionManager _session;
    private readonly FacebookAutomationOptions _options;
    private readonly FacebookPageInspector _inspector;
    private readonly ILogger<PlaywrightFacebookAutomation> _logger;
    private readonly SemaphoreSlim _workLock = new(1, 1);
    private IPage? _workPage;

    public PlaywrightFacebookAutomation(
        BrowserSessionManager session,
        IOptions<FacebookAutomationOptions> options,
        ILogger<PlaywrightFacebookAutomation> logger)
    {
        _session = session;
        _options = options.Value;
        _inspector = new FacebookPageInspector(_options);
        _logger = logger;
    }

    public bool IsBrowserOpen => _session.IsOpen;

    public async Task<SessionStatus> CheckSessionAsync(CancellationToken cancellationToken)
    {
        var context = await _session.GetContextAsync(requireVisible: false, cancellationToken);
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync(_options.BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await WaitQuietlyAsync(page, cancellationToken);

            var url = page.Url;
            if (_options.Phrases.CheckpointUrlFragments.Any(f => url.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                return new SessionStatus(false, "Facebook is asking for a security check. Open the login window and complete it.", DateTime.UtcNow);
            }

            var cookies = await context.CookiesAsync([_options.BaseUrl]);
            var loggedIn = cookies.Any(c => c.Name == _options.SessionCookieName && !string.IsNullOrEmpty(c.Value))
                           && !_options.Phrases.LoginUrlFragments.Any(f => url.Contains(f, StringComparison.OrdinalIgnoreCase));

            return loggedIn
                ? new SessionStatus(true, "Logged in to Facebook.", DateTime.UtcNow)
                : new SessionStatus(false, "Not logged in. Use 'Open login window' and sign in manually.", DateTime.UtcNow);
        }
        catch (PlaywrightException ex)
        {
            throw Translate(ex);
        }
        catch (TimeoutException)
        {
            return new SessionStatus(false, "Facebook did not respond in time. Check your internet connection.", DateTime.UtcNow);
        }
        finally
        {
            await ClosePageQuietlyAsync(page);
        }
    }

    public async Task OpenLoginWindowAsync(CancellationToken cancellationToken)
    {
        var context = await _session.GetContextAsync(requireVisible: true, cancellationToken);
        try
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(_options.LoginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.BringToFrontAsync();
            _logger.LogInformation("Login window opened; waiting for the user to sign in manually");
        }
        catch (PlaywrightException ex)
        {
            throw Translate(ex);
        }
    }

    public async Task<ResolvedLink?> ResolveLinkAsync(string url, CancellationToken cancellationToken)
    {
        await _workLock.WaitAsync(cancellationToken);
        try
        {
            var page = await GetWorkPageAsync(cancellationToken);
            try
            {
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Opening {Url} timed out; inspecting the partially loaded page", url);
            }

            // Facebook redirects share links client-side; wait for a group address or a login/checkpoint wall.
            var phrases = _options.Phrases;
            await WaitUntilAsync(() => Task.FromResult(
                    page.Url.Contains("/groups/", StringComparison.OrdinalIgnoreCase) ||
                    phrases.LoginUrlFragments.Any(f => page.Url.Contains(f, StringComparison.OrdinalIgnoreCase)) ||
                    phrases.CheckpointUrlFragments.Any(f => page.Url.Contains(f, StringComparison.OrdinalIgnoreCase))),
                TimeSpan.FromSeconds(_options.PageProbeSeconds), cancellationToken);
            await _inspector.ThrowIfHaltRequiredAsync(page);

            if (!page.Url.Contains("/groups/", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Link {Url} led to {Destination}, which is not a group", url, page.Url);
                return null;
            }

            await WaitQuietlyAsync(page, cancellationToken);
            var name = await FacebookPageInspector.ReadGroupNameAsync(page);
            _logger.LogInformation("Resolved {Url} to {Destination} ({Name})", url, page.Url, name);
            return new ResolvedLink(page.Url, name);
        }
        catch (PlaywrightException ex)
        {
            throw Translate(ex);
        }
        finally
        {
            _workLock.Release();
        }
    }

    public async Task<MembershipCheckResult> CheckMembershipAsync(string groupUrl, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await _workLock.WaitAsync(cancellationToken);
        try
        {
            var page = await GetWorkPageAsync(cancellationToken);
            progress?.Report("Opening the group page…");
            await NavigateAsync(page, groupUrl, cancellationToken);
            progress?.Report("Checking membership (looking for the Join button or the post composer)…");
            return await ProbeMembershipAsync(page, cancellationToken);
        }
        catch (PlaywrightException ex)
        {
            throw Translate(ex);
        }
        finally
        {
            _workLock.Release();
        }
    }

    public async Task<PublishResult> PublishAsync(
        string groupUrl,
        PublishRequest request,
        Func<CancellationToken, Task<ApprovalDecision>>? approval,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        await _workLock.WaitAsync(cancellationToken);
        try
        {
            var page = await GetWorkPageAsync(cancellationToken);
            if (!page.Url.StartsWith(groupUrl, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("Opening the group page…");
                await NavigateAsync(page, groupUrl, cancellationToken);
            }
            return await PublishOnPageAsync(page, request, approval, progress, cancellationToken);
        }
        catch (PlaywrightException ex)
        {
            throw Translate(ex);
        }
        finally
        {
            _workLock.Release();
        }
    }

    public async Task CloseBrowserAsync()
    {
        await _workLock.WaitAsync();
        try
        {
            _workPage = null;
            await _session.CloseAsync();
        }
        finally
        {
            _workLock.Release();
        }
    }

    private async Task<MembershipCheckResult> ProbeMembershipAsync(IPage page, CancellationToken ct)
    {
        var selectors = _options.Selectors;
        var deadline = DateTime.UtcNow.AddSeconds(_options.PageProbeSeconds);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await _inspector.ThrowIfHaltRequiredAsync(page);

            if (await FacebookPageInspector.AnyVisibleAsync(page, selectors.PendingRequestButton))
            {
                return new MembershipCheckResult(MembershipStatus.NotMember, await FacebookPageInspector.ReadGroupNameAsync(page),
                    "A membership request is pending; the account is not a member yet.");
            }

            if (await FacebookPageInspector.AnyVisibleAsync(page, selectors.JoinButton))
            {
                return new MembershipCheckResult(MembershipStatus.NotMember, await FacebookPageInspector.ReadGroupNameAsync(page),
                    "The account is not a member of this group. Groups are never joined automatically.");
            }

            if (await FacebookPageInspector.AnyVisibleAsync(page, selectors.ComposerTrigger) ||
                await FacebookPageInspector.AnyVisibleAsync(page, selectors.MemberIndicator))
            {
                return new MembershipCheckResult(MembershipStatus.Member, await FacebookPageInspector.ReadGroupNameAsync(page), null);
            }

            // Checked last: Join/composer controls prove the group exists, whatever else the page says.
            var inaccessible = await _inspector.FirstVisibleInaccessiblePhraseAsync(page);
            if (inaccessible is not null)
            {
                return new MembershipCheckResult(MembershipStatus.Inaccessible, null, $"Facebook says: \"{inaccessible}\". The group may be deleted, private, or the link is wrong.");
            }

            await Task.Delay(PollInterval, ct);
        }

        return new MembershipCheckResult(MembershipStatus.Unknown, await FacebookPageInspector.ReadGroupNameAsync(page),
            $"Could not find a Join button or post composer within {_options.PageProbeSeconds}s. If the group looks normal in the browser, update the selectors in appsettings.json.");
    }

    private async Task<PublishResult> PublishOnPageAsync(
        IPage page,
        PublishRequest request,
        Func<CancellationToken, Task<ApprovalDecision>>? approval,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var selectors = _options.Selectors;
        await _inspector.ThrowIfHaltRequiredAsync(page);

        // 1. Open the composer.
        progress?.Report("Opening the post composer…");
        var trigger = await WaitForFirstVisibleAsync(page.Locator("html"), selectors.ComposerTrigger, TimeSpan.FromSeconds(_options.ComposerOpenSeconds), ct);
        if (trigger is null)
        {
            return PublishResult.Failure("The post composer is not available in this group. Posting may be limited to admins or disabled.");
        }
        await trigger.ClickAsync();

        var dialog = page.Locator(selectors.ComposerDialog).Last;
        if (!await WaitUntilAsync(() => dialog.IsVisibleAsync(), TimeSpan.FromSeconds(_options.ComposerOpenSeconds), ct))
        {
            await _inspector.ThrowIfHaltRequiredAsync(page);
            return PublishResult.Failure("The post composer did not open.");
        }

        try
        {
            // 2. Text.
            if (!string.IsNullOrWhiteSpace(request.Text))
            {
                progress?.Report($"Typing the message ({request.Text.Length:N0} characters)…");
                var textbox = dialog.Locator(selectors.ComposerTextbox).First;
                await textbox.ClickAsync();
                var lines = request.Text.Replace("\r\n", "\n").Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Length > 0)
                    {
                        await page.Keyboard.InsertTextAsync(lines[i]);
                    }
                    if (i < lines.Length - 1)
                    {
                        await page.Keyboard.PressAsync("Shift+Enter");
                    }
                }
            }

            // 3. Attachments.
            var media = request.Files.Where(f => f.Type is AttachmentType.Image or AttachmentType.Video).Select(f => f.FullPath).ToArray();
            var documents = request.Files.Where(f => f.Type == AttachmentType.Pdf).Select(f => f.FullPath).ToArray();

            if (media.Length > 0)
            {
                progress?.Report($"Attaching {media.Length} photo/video file(s)…");
                var input = await FindFileInputAsync(dialog, selectors.MediaFileInput, [selectors.PhotoVideoButton], ct);
                if (input is null)
                {
                    await DiscardComposerAsync(page);
                    return PublishResult.Failure("Could not find the photo/video upload control in the composer.");
                }
                await input.SetInputFilesAsync(media);
                _logger.LogInformation("Attached {Count} photo/video file(s)", media.Length);
            }

            if (documents.Length > 0)
            {
                progress?.Report($"Attaching {documents.Length} PDF document(s)…");
                var input = await FindFileInputAsync(dialog, selectors.DocumentFileInput, [selectors.FileButton, selectors.MoreOptionsButton, selectors.FileButton], ct);
                if (input is null)
                {
                    await DiscardComposerAsync(page);
                    return PublishResult.Failure("This group's composer does not offer file (PDF) attachments.");
                }
                await input.SetInputFilesAsync(documents);
                _logger.LogInformation("Attached {Count} document(s)", documents.Length);
            }

            await _inspector.ThrowIfHaltRequiredAsync(page);

            // 4. Wait until Facebook has processed the uploads and enabled the Post button.
            progress?.Report(request.Files.Count > 0
                ? "Waiting for Facebook to finish uploading and enable the Post button…"
                : "Waiting for the Post button…");
            var postButton = await WaitForEnabledPostButtonAsync(dialog, request.UploadTimeout, ct);
            if (postButton is null)
            {
                await _inspector.ThrowIfHaltRequiredAsync(page);
                await DiscardComposerAsync(page);
                return PublishResult.Failure($"The Post button did not become available within {request.UploadTimeout.TotalSeconds:0}s (upload still processing or the content was refused).");
            }

            // 5. Optional human review.
            if (approval is not null)
            {
                await page.BringToFrontAsync();
                var decision = await approval(ct);
                if (decision != ApprovalDecision.Approved)
                {
                    await DiscardComposerAsync(page);
                    return decision == ApprovalDecision.Rejected
                        ? PublishResult.RejectedByUser()
                        : PublishResult.Failure("Approval timed out; the prepared post was discarded and nothing was published.");
                }

                if (!await dialog.IsVisibleAsync())
                {
                    return PublishResult.Failure("The composer was closed in the browser during review, so the application did not post. Check the group to see whether it was posted manually.");
                }
                postButton = await WaitForEnabledPostButtonAsync(dialog, TimeSpan.FromSeconds(30), ct);
                if (postButton is null)
                {
                    await DiscardComposerAsync(page);
                    return PublishResult.Failure("The Post button was no longer available after review.");
                }
            }

            // 6. Submit. From here on the outcome is reported, never retried.
            ct.ThrowIfCancellationRequested();
            progress?.Report("Clicking Post…");
            await postButton.ClickAsync();
            _logger.LogInformation("Post submitted");

            progress?.Report("Waiting for Facebook to confirm the post…");
            var closed = await WaitUntilAsync(async () => !await dialog.IsVisibleAsync(), TimeSpan.FromSeconds(Math.Max(_options.PostConfirmSeconds, 10)), CancellationToken.None);
            await _inspector.ThrowIfHaltRequiredAsync(page);

            if (!closed)
            {
                var detail = await ReadDialogTextAsync(dialog);
                return PublishResult.Failure($"Facebook did not confirm the post (composer still open). {detail}".Trim());
            }

            return await _inspector.AnyPhraseVisibleAsync(page, _options.Phrases.PendingAdminApproval)
                ? PublishResult.Success("Submitted; the group requires admin approval before the post becomes visible.")
                : PublishResult.Success();
        }
        catch (OperationCanceledException)
        {
            await DiscardComposerAsync(page);
            throw;
        }
    }

    private async Task<ILocator?> FindFileInputAsync(ILocator dialog, string inputSelector, IEnumerable<string[]> buttonsToTry, CancellationToken ct)
    {
        var input = dialog.Locator(inputSelector).First;
        if (await input.CountAsync() > 0)
        {
            return input;
        }

        foreach (var buttonSelectors in buttonsToTry)
        {
            var button = await FacebookPageInspector.FirstVisibleAsync(dialog, buttonSelectors);
            if (button is null)
            {
                continue;
            }
            await button.ClickAsync();
            if (await WaitUntilAsync(async () => await input.CountAsync() > 0, TimeSpan.FromSeconds(5), ct))
            {
                return input;
            }
        }
        return null;
    }

    private async Task<ILocator?> WaitForEnabledPostButtonAsync(ILocator dialog, TimeSpan timeout, CancellationToken ct)
    {
        ILocator? found = null;
        await WaitUntilAsync(async () =>
        {
            var button = await FacebookPageInspector.FirstVisibleAsync(dialog, _options.Selectors.PostButton);
            if (button is null || await button.GetAttributeAsync("aria-disabled") == "true")
            {
                return false;
            }
            found = button;
            return true;
        }, timeout, ct);
        return found;
    }

    private async Task DiscardComposerAsync(IPage page)
    {
        try
        {
            if (!await page.Locator(_options.Selectors.ComposerDialog).Last.IsVisibleAsync())
            {
                return;
            }
            await page.Keyboard.PressAsync("Escape");
            await Task.Delay(800);
            var discard = await FacebookPageInspector.FirstVisibleAsync(page.Locator("html"), _options.Selectors.DiscardButton);
            if (discard is not null)
            {
                await discard.ClickAsync();
            }
            _logger.LogInformation("Composer discarded without posting");
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "Could not discard composer");
        }
    }

    private async Task NavigateAsync(IPage page, string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Navigation to {Url} timed out; inspecting the partially loaded page", url);
        }
        await WaitQuietlyAsync(page, ct);
        await _inspector.ThrowIfHaltRequiredAsync(page);
    }

    private async Task<IPage> GetWorkPageAsync(CancellationToken ct)
    {
        var context = await _session.GetContextAsync(requireVisible: false, ct);
        if (_workPage is { IsClosed: false } page && ReferenceEquals(page.Context, context))
        {
            return page;
        }
        _workPage = context.Pages.FirstOrDefault(p => !p.IsClosed && p.Url == "about:blank") ?? await context.NewPageAsync();
        return _workPage;
    }

    private static async Task<ILocator?> WaitForFirstVisibleAsync(ILocator scope, string[] selectors, TimeSpan timeout, CancellationToken ct)
    {
        ILocator? found = null;
        await WaitUntilAsync(async () => (found = await FacebookPageInspector.FirstVisibleAsync(scope, selectors)) is not null, timeout, ct);
        return found;
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await condition())
            {
                return true;
            }
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            await Task.Delay(PollInterval, ct);
        }
    }

    private static async Task WaitQuietlyAsync(IPage page, CancellationToken ct)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions { Timeout = 15_000 });
        }
        catch (TimeoutException)
        {
            // Facebook keeps long-polling connections open; a missing load event is normal.
        }
        await Task.Delay(1000, ct);
    }

    private static async Task<string> ReadDialogTextAsync(ILocator dialog)
    {
        try
        {
            var alert = dialog.Locator("[role='alert']").First;
            if (await alert.IsVisibleAsync())
            {
                return "Facebook said: " + (await alert.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 2000 })).Trim();
            }
        }
        catch (PlaywrightException)
        {
        }
        catch (TimeoutException)
        {
        }
        return string.Empty;
    }

    private static async Task ClosePageQuietlyAsync(IPage page)
    {
        try
        {
            await page.CloseAsync();
        }
        catch (PlaywrightException)
        {
        }
    }

    private Exception Translate(PlaywrightException ex)
    {
        if (ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase))
        {
            _workPage = null;
            return new AutomationHaltedException(HaltReason.BrowserUnavailable, "The browser window was closed.", ex);
        }
        return ex;
    }
}
