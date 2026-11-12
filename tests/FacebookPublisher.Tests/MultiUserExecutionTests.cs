using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Automation;
using FacebookPublisher.Application.Campaigns;
using FacebookPublisher.Application.Groups;
using FacebookPublisher.Application.Content;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;
using FacebookPublisher.Infrastructure.Campaigns;
using FacebookPublisher.Web.BackgroundServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FacebookPublisher.Tests;

/// <summary>
/// Several users' campaigns in one running application (one shared tracker, queue and browser factory), on SQL Server.
/// Every campaign must use its owner's own browser session and data, users run at the same time, and each user runs
/// at most one campaign at a time.
/// </summary>
public class MultiUserExecutionTests : IAsyncLifetime
{
    private SqlTestDatabase _db = null!;
    private Guid _a;
    private Guid _b;

    public async Task InitializeAsync()
    {
        _db = await SqlTestDatabase.CreateAsync();
        _a = await _db.CreateUserAsync("user-a@example.test");
        _b = await _db.CreateUserAsync("user-b@example.test");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Each_campaign_runs_with_its_owners_browser_session_and_users_run_at_the_same_time()
    {
        var host = new SharedHost(_db);
        var campaignA = await host.CreateCampaignAsync(_a, groups: 4);
        var campaignB = await host.CreateCampaignAsync(_b, groups: 3);

        // A's first post waits until B has posted: this only completes if both campaigns execute concurrently.
        var bPosted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Automations.For(_b).AfterPublish = _ => { bPosted.TrySetResult(); return Task.CompletedTask; };
        host.Automations.For(_a).AfterPublish = async _ => await bPosted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await Task.WhenAll(host.RunAsync(campaignA), host.RunAsync(campaignB));

        Assert.Equal(await GroupUrlsAsync(campaignA), host.Automations.For(_a).PublishedTo.Order());
        Assert.Equal(await GroupUrlsAsync(campaignB), host.Automations.For(_b).PublishedTo.Order());
        Assert.Empty(host.Automations.For(_a).PublishedTo.Intersect(host.Automations.For(_b).PublishedTo));
        Assert.Equal(new[] { _a, _b }.Order(), host.Automations.Requests.Distinct().Order());
        Assert.Equal(CampaignStatus.Completed, await StatusAsync(campaignA));
        Assert.Equal(CampaignStatus.Completed, await StatusAsync(campaignB));
    }

    [Fact]
    public async Task Campaign_of_a_disabled_account_is_not_executed()
    {
        var host = new SharedHost(_db);
        var campaign = await host.CreateCampaignAsync(_a, groups: 2);
        host.Accounts.Disabled[_a] = true;

        await host.RunAsync(campaign);

        Assert.Equal(CampaignStatus.Halted, await StatusAsync(campaign));
        Assert.Empty(host.Automations.For(_a).PublishedTo);
        Assert.Empty(host.Automations.For(_a).MembershipChecks);
    }

    [Fact]
    public async Task Stop_of_one_users_campaign_does_not_affect_another_users_running_campaign()
    {
        var host = new SharedHost(_db);
        var campaignA = await host.CreateCampaignAsync(_a, groups: 5);
        var campaignB = await host.CreateCampaignAsync(_b, groups: 5);
        host.Automations.For(_a).AfterPublish = async _ => await host.Service(_a).StopAsync(campaignA);

        await Task.WhenAll(host.RunAsync(campaignA), host.RunAsync(campaignB));

        Assert.Equal(CampaignStatus.Stopped, await StatusAsync(campaignA));
        Assert.Equal(CampaignStatus.Completed, await StatusAsync(campaignB));
        Assert.Single(host.Automations.For(_a).PublishedTo);
        Assert.Equal(5, host.Automations.For(_b).PublishedTo.Count);
    }

    [Fact]
    public async Task The_worker_runs_different_users_concurrently_and_each_users_campaigns_one_after_another()
    {
        var host = new SharedHost(_db);
        var a1 = await host.CreateCampaignAsync(_a, groups: 2, start: false);
        var a2 = await host.CreateCampaignAsync(_a, groups: 2, start: false);
        var b1 = await host.CreateCampaignAsync(_b, groups: 2, start: false);
        var a1Urls = await GroupUrlsAsync(a1);
        var a2Urls = await GroupUrlsAsync(a2);

        var bPosted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var maxRunningForA = 0;
        host.Automations.For(_b).AfterPublish = _ => { bPosted.TrySetResult(); return Task.CompletedTask; };
        host.Automations.For(_a).AfterPublish = async url =>
        {
            maxRunningForA = Math.Max(maxRunningForA, host.Tracker.RunningCampaignIds.Count(id => id == a1 || id == a2));
            if (a1Urls.Contains(url))
            {
                await bPosted.Task.WaitAsync(TimeSpan.FromSeconds(30)); // A1 and B1 overlap
            }
        };

        using var services = host.BuildWorkerServices();
        var worker = services.GetRequiredService<CampaignWorker>();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Start a second campaign of A while its first is queued: it must wait for A1, not run beside it.
            await host.Service(_a, host.Queue).StartAsync(a1);
            await host.Service(_a, host.Queue).StartAsync(a2);
            await host.Service(_b, host.Queue).StartAsync(b1);
            await WaitUntilAsync(async () =>
                await StatusAsync(a1) == CampaignStatus.Completed && await StatusAsync(a2) == CampaignStatus.Completed
                && await StatusAsync(b1) == CampaignStatus.Completed, TimeSpan.FromSeconds(60));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        var publishedByA = host.Automations.For(_a).PublishedTo;
        Assert.Equal(a1Urls.Concat(a2Urls).Order(), publishedByA.Order());
        Assert.True(publishedByA.Take(2).All(a1Urls.Contains), "A's second campaign started before its first finished");
        Assert.Equal(1, maxRunningForA);
        Assert.Equal(2, host.Automations.For(_b).PublishedTo.Count);
    }

    // ---------- helpers ----------

    private async Task<CampaignStatus> StatusAsync(Guid campaignId)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.Campaigns.AsNoTracking().Where(c => c.Id == campaignId).Select(c => c.Status).SingleAsync();
    }

    private async Task<List<string>> GroupUrlsAsync(Guid campaignId)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.CampaignTargets.AsNoTracking().Where(t => t.CampaignId == campaignId).Select(t => t.GroupUrl).OrderBy(u => u).ToListAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not reached in time.");
            }
            await Task.Delay(100);
        }
    }

    /// <summary>One running application shared by several users: one tracker, queue, browser factory and notifier.</summary>
    private sealed class SharedHost(SqlTestDatabase db)
    {
        public CampaignExecutionTracker Tracker { get; } = new();

        public CampaignQueue Queue { get; } = new();

        public FakeAutomationFactory Automations { get; } = new();

        public FakeUserAccounts Accounts { get; } = new();

        public CampaignRunnerTests.RecordingNotifier Notifier { get; } = new();

        /// <summary>Campaign service for a signed-in user; only the worker test feeds the real queue.</summary>
        public CampaignService Service(Guid user, ICampaignQueue? queue = null) => new(db.For(user), queue ?? new RecordingQueue(), Tracker, new CampaignApprovalGate(), Notifier,
            NullLogger<CampaignService>.Instance);

        public CampaignRunner Runner() => new(db, Automations, Accounts, new CampaignRunnerTests.FakeStorage(), new CampaignApprovalGate(),
            Notifier, new BatchDelayService(FixedRandom.AlwaysMax()), new RecordingDelay(), Tracker, NullLogger<CampaignRunner>.Instance);

        public async Task<Guid> CreateCampaignAsync(Guid user, int groups, bool start = true)
        {
            var groupService = new GroupService(db.For(user), Automations, new FakeCurrentUser(user), Tracker, NullLogger<GroupService>.Instance);
            var groupIds = new List<Guid>();
            for (var i = 0; i < groups; i++)
            {
                groupIds.Add((await groupService.AddAsync(new SaveGroupRequest(null, $"https://www.facebook.com/groups/{user:N}-{Guid.NewGuid():N}", null))).Id);
            }
            var content = new ContentService(db.For(user), new CampaignRunnerTests.FakeStorage(),
                Options.Create(new ContentUploadOptions()), NullLogger<ContentService>.Instance);
            var contentId = await content.CreateAsync(new CreateContentRequest($"Post {Guid.NewGuid():N}", "hello", []));
            var pacing = new BatchPacingConfig { MinBatchSize = 9, MaxBatchSize = 9 };
            return await Service(user).CreateAsync(new CreateCampaignRequest("Campaign", contentId, groupIds, pacing, false), start);
        }

        /// <summary>Runs one queued campaign the way the worker does (owner from the campaign record).</summary>
        public async Task RunAsync(Guid campaignId)
        {
            var runner = Runner();
            var owner = await runner.GetOwnerIdAsync(campaignId) ?? throw new InvalidOperationException();
            var token = Tracker.Begin(campaignId, owner, CancellationToken.None);
            try
            {
                await runner.RunAsync(campaignId, token, CancellationToken.None);
            }
            finally
            {
                Tracker.End(campaignId);
            }
        }

        public ServiceProvider BuildWorkerServices()
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<ICampaignQueue>(Queue);
            services.AddSingleton<ICampaignExecutionTracker>(Tracker);
            services.AddSingleton<ISystemDbContextFactory>(db);
            services.AddSingleton<IFacebookAutomationFactory>(Automations);
            services.AddSingleton<IUserAccounts>(Accounts);
            services.AddSingleton<IFileStorage>(new CampaignRunnerTests.FakeStorage());
            services.AddSingleton<ICampaignApprovalGate, CampaignApprovalGate>();
            services.AddSingleton<ICampaignProgressNotifier>(Notifier);
            services.AddSingleton<IBatchDelayService>(new BatchDelayService(FixedRandom.AlwaysMax()));
            services.AddSingleton<IAsyncDelay>(new RecordingDelay());
            services.AddScoped<CampaignRunner>();
            services.AddSingleton(Options.Create(new CampaignWorkerOptions { MaxConcurrentCampaigns = 10 }));
            services.AddSingleton<CampaignWorker>();
            return services.BuildServiceProvider();
        }
    }
}
