using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Automation;
using FacebookPublisher.Application.Campaigns;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;
using FacebookPublisher.Infrastructure.Campaigns;
using FacebookPublisher.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FacebookPublisher.Tests;

public class CampaignRunnerTests
{
    private readonly TestDbFactory _db = new();
    private readonly FakeAutomation _automation = new();
    private readonly CampaignApprovalGate _gate = new();
    private readonly RecordingNotifier _notifier = new();

    private readonly CampaignExecutionTracker _tracker = new();

    private CampaignRunner CreateRunner() => new(
        _db, _automation, new FakeStorage(), _gate, _notifier,
        new BatchDelayService(new SystemRandomSource()), new RecordingDelay(), _tracker,
        NullLogger<CampaignRunner>.Instance);

    [Fact]
    public async Task Automatic_campaign_publishes_without_approval_and_reports_every_step_live()
    {
        var id = await SeedCampaignAsync(["auto-1", "auto-2"]);

        await CreateRunner().RunAsync(id, CancellationToken.None, CancellationToken.None);

        var campaign = await LoadAsync(id);
        Assert.False(campaign.RequireApproval);
        Assert.False(_automation.ApprovalRequested);
        Assert.Equal(CampaignStatus.Completed, campaign.Status);

        var texts = _notifier.Messages.Select(m => m.Text).ToList();
        Assert.Contains(texts, t => t.StartsWith("Campaign started") && t.Contains("fully automatic", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Facebook session is active.", texts);
        Assert.Contains(texts, t => t.StartsWith("Batch 1 started"));
        Assert.Contains(texts, t => t.StartsWith("Group 1 of 2 (batch 1"));
        Assert.Contains(texts, t => t.StartsWith("Between posts: waiting"));
        Assert.Contains(_notifier.Messages, m => m.Level == CampaignMessageLevel.Step && m.Text == "Clicking Post…" && m.GroupName == "Group auto-2");
        Assert.Equal(2, _notifier.Messages.Count(m => m.Level == CampaignMessageLevel.Success && m.Text.StartsWith("Published successfully")));
        Assert.StartsWith("Campaign finished after 1 batch(es): 2 published", texts[^1]);
    }

    [Fact]
    public async Task Each_group_gets_its_own_outcome_and_failures_do_not_stop_the_campaign()
    {
        var id = await SeedCampaignAsync(["member-ok", "not-member", "gone", "member-fails", "member-ok2"]);
        _automation.Membership["not-member"] = MembershipStatus.NotMember;
        _automation.Membership["gone"] = MembershipStatus.Inaccessible;
        _automation.PublishFailures.Add("member-fails");

        await CreateRunner().RunAsync(id, CancellationToken.None, CancellationToken.None);

        var campaign = await LoadAsync(id);
        Assert.Equal(CampaignStatus.CompletedWithErrors, campaign.Status);
        Assert.Equal(TargetStatus.Published, Status(campaign, "member-ok"));
        Assert.Equal(TargetStatus.SkippedNotMember, Status(campaign, "not-member"));
        Assert.Equal(TargetStatus.Inaccessible, Status(campaign, "gone"));
        Assert.Equal(TargetStatus.Failed, Status(campaign, "member-fails"));
        Assert.Equal(TargetStatus.Published, Status(campaign, "member-ok2"));
        Assert.Equal(["member-ok", "member-fails", "member-ok2"], _automation.PublishedTo.Select(Slug));
        Assert.All(campaign.Targets.Where(t => t.Status == TargetStatus.Published), t => Assert.NotNull(t.PublishedAtUtc));
    }

    [Fact]
    public async Task Inactive_session_halts_before_touching_any_group()
    {
        var id = await SeedCampaignAsync(["a", "b"]);
        _automation.LoggedIn = false;

        await CreateRunner().RunAsync(id, CancellationToken.None, CancellationToken.None);

        var campaign = await LoadAsync(id);
        Assert.Equal(CampaignStatus.Halted, campaign.Status);
        Assert.Contains("session", campaign.StatusReason, StringComparison.OrdinalIgnoreCase);
        Assert.All(campaign.Targets, t => Assert.Equal(TargetStatus.Pending, t.Status));
        Assert.True(campaign.CanBeResumed);
        Assert.Empty(_automation.MembershipChecks);
    }

    [Fact]
    public async Task Restriction_during_publish_halts_and_leaves_remaining_groups_untouched()
    {
        var id = await SeedCampaignAsync(["first", "blocked", "third"]);
        _automation.HaltOnPublish.Add("blocked");

        await CreateRunner().RunAsync(id, CancellationToken.None, CancellationToken.None);

        var campaign = await LoadAsync(id);
        Assert.Equal(CampaignStatus.Halted, campaign.Status);
        Assert.Equal(TargetStatus.Published, Status(campaign, "first"));
        Assert.Equal(TargetStatus.Failed, Status(campaign, "blocked"));
        Assert.Equal(TargetStatus.Pending, Status(campaign, "third"));
        Assert.DoesNotContain(_automation.MembershipChecks, u => u.EndsWith("third"));
    }

    [Fact]
    public async Task Resuming_a_halted_campaign_never_reposts_processed_groups()
    {
        var id = await SeedCampaignAsync(["first", "blocked", "third"]);
        _automation.HaltOnPublish.Add("blocked");
        await CreateRunner().RunAsync(id, CancellationToken.None, CancellationToken.None);

        // User fixes the problem and resumes.
        _automation.HaltOnPublish.Clear();
        _automation.PublishedTo.Clear();
        await using (var db = (AppDbContext)await _db.CreateAsync())
        {
            var c = await db.Campaigns.FirstAsync(x => x.Id == id);
            c.Status = CampaignStatus.Queued;
            await db.SaveChangesAsync();
        }
        await CreateRunner().RunAsync(id, CancellationToken.None, CancellationToken.None);

        var campaign = await LoadAsync(id);
        Assert.Equal(["third"], _automation.PublishedTo.Select(Slug));
        Assert.Equal(TargetStatus.Published, Status(campaign, "third"));
        Assert.Equal(TargetStatus.Failed, Status(campaign, "blocked"));
    }

    [Fact]
    public async Task Same_content_published_recently_to_the_same_group_is_skipped()
    {
        var first = await SeedCampaignAsync(["dup"]);
        await CreateRunner().RunAsync(first, CancellationToken.None, CancellationToken.None);
        _automation.PublishedTo.Clear();

        var second = await SeedCampaignAsync(["dup"], reuseContentFrom: first);
        await CreateRunner().RunAsync(second, CancellationToken.None, CancellationToken.None);

        var campaign = await LoadAsync(second);
        Assert.Equal(TargetStatus.SkippedDuplicate, Status(campaign, "dup"));
        Assert.Empty(_automation.PublishedTo);
    }

    [Fact]
    public async Task Rejected_review_skips_the_group_and_continues()
    {
        var id = await SeedCampaignAsync(["reject-me", "approve-me"], requireApproval: true);
        var decisions = new Queue<bool>([false, true]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reviewer = Task.Run(async () =>
        {
            while (decisions.Count > 0 && !cts.IsCancellationRequested)
            {
                await using var db = (AppDbContext)await _db.CreateAsync();
                var awaiting = await db.CampaignTargets.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.CampaignId == id && t.Status == TargetStatus.AwaitingApproval);
                if (awaiting is not null && _gate.IsAwaiting(awaiting.Id))
                {
                    _gate.Resolve(awaiting.Id, decisions.Dequeue());
                }
                await Task.Delay(20);
            }
        });

        await CreateRunner().RunAsync(id, cts.Token, CancellationToken.None);
        await reviewer;

        var campaign = await LoadAsync(id);
        Assert.Equal(TargetStatus.SkippedByUser, Status(campaign, "reject-me"));
        Assert.Equal(TargetStatus.Published, Status(campaign, "approve-me"));
        Assert.Equal(CampaignStatus.Completed, campaign.Status);
    }

    [Fact]
    public async Task User_stop_leaves_unprocessed_groups_pending_and_resumable()
    {
        var id = await SeedCampaignAsync(["a", "b"]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await CreateRunner().RunAsync(id, cts.Token, CancellationToken.None);

        var campaign = await LoadAsync(id);
        Assert.Equal(CampaignStatus.Stopped, campaign.Status);
        Assert.All(campaign.Targets, t => Assert.Equal(TargetStatus.Pending, t.Status));
        Assert.True(campaign.CanBeResumed);
        Assert.Null(campaign.ExecutionOwnerId);
        Assert.Empty(_automation.PublishedTo);
    }

    [Fact]
    public async Task Campaign_that_is_not_queued_is_ignored()
    {
        var id = await SeedCampaignAsync(["a"], status: CampaignStatus.Completed);

        await CreateRunner().RunAsync(id, CancellationToken.None, CancellationToken.None);

        Assert.Empty(_automation.MembershipChecks);
        Assert.Equal(CampaignStatus.Completed, (await LoadAsync(id)).Status);
    }

    // ---------- helpers ----------

    private async Task<Guid> SeedCampaignAsync(
        string[] slugs,
        bool requireApproval = false,
        CampaignStatus status = CampaignStatus.Queued,
        Guid? reuseContentFrom = null)
    {
        await using var db = (AppDbContext)await _db.CreateAsync();

        Guid contentId;
        if (reuseContentFrom is { } previous)
        {
            contentId = (await db.Campaigns.FirstAsync(c => c.Id == previous)).ContentPostId;
        }
        else
        {
            var content = new ContentPost { Title = "Post", Text = "Hello groups", ContentHash = Guid.NewGuid().ToString("N") };
            db.Contents.Add(content);
            contentId = content.Id;
        }

        var campaign = new Campaign
        {
            Name = "Test",
            ContentPostId = contentId,
            Status = status
        };
        if (requireApproval)
        {
            campaign.RequireApproval = true;
        }

        var order = 0;
        foreach (var slug in slugs)
        {
            var url = $"https://www.facebook.com/groups/{slug}";
            var group = await db.Groups.FirstOrDefaultAsync(g => g.Url == url);
            if (group is null)
            {
                group = new FacebookGroup { Name = slug, Url = url };
                db.Groups.Add(group);
            }
            campaign.Targets.Add(new CampaignTarget { FacebookGroupId = group.Id, GroupName = slug, GroupUrl = url, Order = order++ });
        }

        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync();
        return campaign.Id;
    }

    private async Task<Campaign> LoadAsync(Guid id)
    {
        await using var db = (AppDbContext)await _db.CreateAsync();
        return await db.Campaigns.AsNoTracking().Include(c => c.Targets).FirstAsync(c => c.Id == id);
    }

    private static TargetStatus Status(Campaign campaign, string slug) =>
        campaign.Targets.Single(t => t.GroupUrl.EndsWith("/" + slug)).Status;

    private static string Slug(string url) => url[(url.LastIndexOf('/') + 1)..];

    internal sealed class TestDbFactory : IApplicationDbContextFactory
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        public Task<IApplicationDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IApplicationDbContext>(new AppDbContext(_options));
    }

    internal sealed class FakeAutomation : IFacebookAutomation
    {
        public bool LoggedIn { get; set; } = true;
        public Dictionary<string, ResolvedLink?> ShareLinks { get; } = [];
        public List<string> ResolvedRequests { get; } = [];

        public Task<ResolvedLink?> ResolveLinkAsync(string url, CancellationToken cancellationToken)
        {
            ResolvedRequests.Add(url);
            if (!LoggedIn)
            {
                throw new AutomationHaltedException(HaltReason.SessionExpired, "Facebook redirected to the login page.");
            }
            return Task.FromResult(ShareLinks.GetValueOrDefault(url));
        }
        public Dictionary<string, MembershipStatus> Membership { get; } = [];
        public HashSet<string> PublishFailures { get; } = [];
        public HashSet<string> HaltOnPublish { get; } = [];
        public List<string> MembershipChecks { get; } = [];
        public List<string> PublishedTo { get; } = [];
        public bool ApprovalRequested { get; private set; }

        /// <summary>Runs after a (simulated) successful submission — e.g. to press Stop after the 60th post.</summary>
        public Func<string, Task>? AfterPublish { get; set; }

        public bool IsBrowserOpen => true;

        public Task<SessionStatus> CheckSessionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SessionStatus(LoggedIn, LoggedIn ? "ok" : "Not logged in.", DateTime.UtcNow));
        }

        public Task OpenLoginWindowAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<MembershipCheckResult> CheckMembershipAsync(string groupUrl, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Opening the group page…");
            MembershipChecks.Add(groupUrl);
            var status = Membership.GetValueOrDefault(Slug(groupUrl), MembershipStatus.Member);
            return Task.FromResult(new MembershipCheckResult(status, $"Group {Slug(groupUrl)}", null));
        }

        public async Task<PublishResult> PublishAsync(string groupUrl, PublishRequest request,
            Func<CancellationToken, Task<ApprovalDecision>>? approval, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            var slug = Slug(groupUrl);
            ApprovalRequested |= approval is not null;
            progress?.Report("Clicking Post…");
            if (approval is not null)
            {
                var decision = await approval(cancellationToken);
                if (decision != ApprovalDecision.Approved)
                {
                    return PublishResult.RejectedByUser();
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (PublishedTo)
            {
                PublishedTo.Add(groupUrl);
            }
            if (AfterPublish is not null)
            {
                await AfterPublish(groupUrl);
            }
            if (HaltOnPublish.Contains(slug))
            {
                throw new AutomationHaltedException(HaltReason.Restricted, "You can't post right now.");
            }
            return PublishFailures.Contains(slug) ? PublishResult.Failure("Composer not available.") : PublishResult.Success();
        }

        public Task CloseBrowserAsync() => Task.CompletedTask;
    }

    internal sealed class FakeStorage : IFileStorage
    {
        public Task<StoredFile> SaveAsync(Stream content, string originalFileName, long maxBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Stream OpenRead(string storedFileName) => Stream.Null;

        public string GetFullPath(string storedFileName) => Path.Combine(Path.GetTempPath(), storedFileName);

        public bool Exists(string storedFileName) => true;

        public void Delete(string storedFileName)
        {
        }
    }

    internal sealed class RecordingNotifier : ICampaignProgressNotifier
    {
        public List<CampaignMessage> Messages { get; } = [];

        public Task CampaignChangedAsync(CampaignProgressDto campaign, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TargetChangedAsync(Guid campaignId, TargetProgressDto target, CampaignCountsDto counts, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MessageAsync(Guid campaignId, CampaignMessage message, CancellationToken cancellationToken = default)
        {
            lock (Messages)
            {
                Messages.Add(message);
            }
            return Task.CompletedTask;
        }
    }
}
