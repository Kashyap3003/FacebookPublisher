using System.Diagnostics;
using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Campaigns;
using FacebookPublisher.Application.Common;
using FacebookPublisher.Application.Settings;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;
using FacebookPublisher.Infrastructure.Campaigns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FacebookPublisher.Tests;

/// <summary>
/// Integration tests on a real SQL Server LocalDB database (real migrations, row versions and enum/owned-column
/// mapping). Each <see cref="AppProcess"/> simulates one run of the application, so "restart" means a new process with
/// a new instance id and fresh in-memory state — everything that survives must come from the database.
/// </summary>
public class BatchExecutionTests : IAsyncLifetime
{
    private SqlTestDatabase _db = null!;
    private Guid _user;

    public async Task InitializeAsync()
    {
        _db = await SqlTestDatabase.CreateAsync();
        _user = await _db.CreateUserAsync("user-a@example.test");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ---------- Batch algorithm ----------

    [Fact]
    public async Task Post_batch_and_extended_delays_are_applied_in_the_right_order_with_no_delay_after_the_last_group()
    {
        var groups = await SeedAsync(10);
        var app = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        var id = await app.CreateAsync(groups, Pacing(minBatch: 3, maxBatch: 5, extendedAfter: 2));

        await app.RunQueuedAsync();

        var campaign = await ReadAsync(id);
        Assert.Equal(CampaignStatus.Completed, campaign.Status);
        // Batches of 3 (random = minimum), final partial batch of 1.
        Assert.Equal([1, 1, 1, 2, 2, 2, 3, 3, 3, 4], campaign.Targets.OrderBy(t => t.Order).Select(t => t.BatchNumber ?? 0));
        Assert.Equal(4, campaign.CompletedBatches);
        // Extended break after batch 2 replaces the batch delay; nothing after the final group.
        Assert.Equal("P P B P P E P P B", WaitSequence(app));
        AssertWaited(app, TimeSpan.FromSeconds(6 * 10) + TimeSpan.FromMinutes(2 * 15) + TimeSpan.FromHours(3));
        Assert.Equal(DelayKind.None, campaign.CurrentDelayKind);
    }

    [Fact]
    public async Task Partial_final_batch_is_processed_and_counted_without_a_trailing_batch_delay()
    {
        var groups = await SeedAsync(7);
        var app = new AppProcess(_db, _user, FixedRandom.AlwaysMax());
        var id = await app.CreateAsync(groups, Pacing(minBatch: 3, maxBatch: 5));

        await app.RunQueuedAsync();

        var campaign = await ReadAsync(id);
        Assert.Equal(7, campaign.Targets.Count(t => t.Status == TargetStatus.Published));
        Assert.Equal([1, 1, 1, 1, 1, 2, 2], campaign.Targets.OrderBy(t => t.Order).Select(t => t.BatchNumber ?? 0));
        Assert.Equal(2, campaign.CompletedBatches);
        Assert.Equal("P P P P B P", WaitSequence(app));
        // Random = maximum: 30 s post delays and a 30 min batch delay.
        AssertWaited(app, TimeSpan.FromSeconds(5 * 30) + TimeSpan.FromMinutes(30));
    }

    // ---------- Stop during delays ----------

    [Fact]
    public async Task Stop_interrupts_a_long_delay_immediately_and_resume_waits_only_the_remaining_part()
    {
        var groups = await SeedAsync(3);
        var app = new AppProcess(_db, _user, FixedRandom.AlwaysMin(), new TaskAsyncDelay()); // real Task.Delay
        var id = await app.CreateAsync(groups, Pacing(minBatch: 1, maxBatch: 1));

        var stopper = Task.Run(async () =>
        {
            // Wait until the 15-minute break between batch 1 and 2 has started, then press Stop.
            while ((await ReadAsync(id)).CurrentDelayKind != DelayKind.BetweenBatches)
            {
                await Task.Delay(50);
            }
            await Task.Delay(200);
            await app.Service.StopAsync(id);
        });
        var watch = Stopwatch.StartNew();
        await app.RunQueuedAsync();
        await stopper;
        watch.Stop();

        var stopped = await ReadAsync(id);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Stop took {watch.Elapsed}");
        Assert.Equal(CampaignStatus.Stopped, stopped.Status);
        Assert.Equal(1, stopped.Targets.Count(t => t.Status == TargetStatus.Published));
        Assert.Equal(2, stopped.Targets.Count(t => t.Status == TargetStatus.Pending));
        Assert.Equal(1, stopped.CompletedBatches);
        Assert.Equal(2, stopped.CurrentBatchNumber);
        Assert.Equal(DelayKind.BetweenBatches, stopped.CurrentDelayKind);
        Assert.True(stopped.CurrentDelayEndsAtUtc > DateTime.UtcNow.AddMinutes(14));

        // Restart + manual Resume: the rest of the interrupted break is waited first, then batches 2 and 3.
        var restarted = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        await restarted.Service.ResumeAsync(id);
        await restarted.RunQueuedAsync();

        var done = await ReadAsync(id);
        Assert.Equal(CampaignStatus.Completed, done.Status);
        Assert.Equal(3, done.CompletedBatches);
        Assert.Equal("B B", WaitSequence(restarted));
        var resumedWait = restarted.Delay.Total - TimeSpan.FromMinutes(15); // second B is a full new 15 min break
        Assert.InRange(resumedWait, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        Assert.DoesNotContain(restarted.Automation.PublishedTo, url => app.Automation.PublishedTo.Contains(url));
    }

    [Fact]
    public async Task Stop_during_a_post_delay_preserves_progress_and_resume_finishes_the_same_batch_without_double_counting()
    {
        var groups = await SeedAsync(6);
        var app = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        var id = await app.CreateAsync(groups, Pacing(minBatch: 3, maxBatch: 3));
        app.Delay.OnDelay = async (_, _) => await app.Service.StopAsync(id); // Stop during the first wait (a post delay)

        await app.RunQueuedAsync();

        var stopped = await ReadAsync(id);
        Assert.Equal(CampaignStatus.Stopped, stopped.Status);
        Assert.Equal(1, stopped.Targets.Count(t => t.Status == TargetStatus.Published));
        Assert.Equal((1, 3, 1, 0), (stopped.CurrentBatchNumber, stopped.CurrentBatchSize, stopped.CurrentBatchProcessed, stopped.CompletedBatches));
        Assert.Equal(DelayKind.BetweenPosts, stopped.CurrentDelayKind);

        var restarted = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        await restarted.Service.ResumeAsync(id);
        await restarted.RunQueuedAsync();

        var done = await ReadAsync(id);
        Assert.Equal(CampaignStatus.Completed, done.Status);
        Assert.Equal([1, 1, 1, 2, 2, 2], done.Targets.OrderBy(t => t.Order).Select(t => t.BatchNumber ?? 0));
        Assert.Equal(2, done.CompletedBatches); // the interrupted batch is counted once, when it actually completed
        Assert.Equal(5, restarted.Automation.PublishedTo.Count);
    }

    [Fact]
    public async Task Stop_during_an_extended_break_keeps_the_extended_counter_consistent_after_resume()
    {
        var groups = await SeedAsync(8);
        var app = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        var id = await app.CreateAsync(groups, Pacing(minBatch: 2, maxBatch: 2, extendedAfter: 2));
        app.Delay.OnDelay = async (_, _) =>
        {
            if (LastWait(app) == "E")
            {
                await app.Service.StopAsync(id);
            }
        };

        await app.RunQueuedAsync();

        var stopped = await ReadAsync(id);
        Assert.Equal(CampaignStatus.Stopped, stopped.Status);
        Assert.Equal((2, 0, 3), (stopped.CompletedBatches, stopped.BatchesSinceExtendedDelay, stopped.CurrentBatchNumber));
        Assert.Equal(DelayKind.Extended, stopped.CurrentDelayKind);

        var restarted = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        await restarted.Service.ResumeAsync(id);
        await restarted.RunQueuedAsync();

        var done = await ReadAsync(id);
        Assert.Equal(4, done.CompletedBatches);
        // Remaining part of the extended break, then batch 3, a normal batch delay (counter 1 of 2), batch 4. No second extended break.
        Assert.Equal("E P B P", WaitSequence(restarted));
        Assert.Equal(8, done.Targets.Count(t => t.Status == TargetStatus.Published));
    }

    // ---------- The 100-group scenario ----------

    [Fact]
    public async Task Hundred_groups_stop_after_sixty_restart_and_resume_processes_only_the_remaining_forty()
    {
        var groups = await SeedAsync(100);
        var first = new AppProcess(_db, _user, new SystemRandomSource());
        var id = await first.CreateAsync(groups, new BatchPacingConfig()); // defaults: 5–9 groups per batch
        var published = 0;
        first.Automation.AfterPublish = async _ =>
        {
            if (Interlocked.Increment(ref published) == 60)
            {
                await first.Service.StopAsync(id); // the user presses Stop
            }
        };

        await first.RunQueuedAsync();

        // Everything below is read back from SQL Server.
        var stopped = await ReadAsync(id);
        Assert.Equal(CampaignStatus.Stopped, stopped.Status);
        Assert.Equal(60, stopped.Targets.Count(t => t.Status == TargetStatus.Published));
        Assert.Equal(40, stopped.Targets.Count(t => t.Status == TargetStatus.Pending));
        Assert.Equal(40, stopped.RemainingGroupCount);
        Assert.Equal(Enumerable.Range(0, 60), stopped.Targets.Where(t => t.Status == TargetStatus.Published).Select(t => t.Order).Order());
        var openBatch = stopped.CurrentBatchProcessed > 0 ? 1 : 0;
        Assert.Equal(stopped.Targets.Where(t => t.BatchNumber != null).Select(t => t.BatchNumber).Distinct().Count(),
            stopped.CompletedBatches + openBatch);
        var firstRunUrls = first.Automation.PublishedTo.ToHashSet();
        Assert.Equal(60, firstRunUrls.Count);

        // The application restarts (new process, nothing in memory) and the user manually clicks Resume.
        var second = new AppProcess(_db, _user, new SystemRandomSource());
        await second.Service.ResumeAsync(id);
        Assert.Single(second.Queue.Enqueued);
        await second.RunQueuedAsync();

        var done = await ReadAsync(id);
        Assert.Equal(CampaignStatus.Completed, done.Status);
        Assert.Equal(40, second.Automation.PublishedTo.Count);
        Assert.Empty(second.Automation.PublishedTo.Intersect(firstRunUrls));                 // no re-posting
        Assert.Equal(Enumerable.Range(60, 40),
            done.Targets.Where(t => second.Automation.PublishedTo.Contains(t.GroupUrl)).Select(t => t.Order).Order()); // groups 61–100
        Assert.All(done.Targets, t => Assert.Equal((TargetStatus.Published, 1), (t.Status, t.PublishAttempts)));
        Assert.DoesNotContain(second.Automation.MembershipChecks, url => firstRunUrls.Contains(url)); // never even opened again

        var batchSizes = done.Targets.GroupBy(t => t.BatchNumber!.Value).OrderBy(g => g.Key).Select(g => g.Count()).ToList();
        Assert.Equal(batchSizes.Count, done.CompletedBatches);
        Assert.All(batchSizes.SkipLast(1), size => Assert.InRange(size, 5, 9));
        Assert.InRange(batchSizes[^1], 1, 9);
    }

    // ---------- Crash recovery and manual verification ----------

    [Fact]
    public async Task Crash_mid_publish_marks_outcome_unknown_and_resume_never_retries_it_until_verified()
    {
        var groups = await SeedAsync(6);
        var crashed = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        var id = await crashed.CreateAsync(groups, Pacing(minBatch: 9, maxBatch: 9), start: false);

        // Simulate the database left behind by a process that died: 2 published, 2 mid-publish, 1 mid-check, 1 pending.
        await using (var db = await _db.CreateContextAsync())
        {
            var c = await db.Campaigns.Include(x => x.Targets).FirstAsync(x => x.Id == id);
            var t = c.Targets.OrderBy(x => x.Order).ToList();
            t[0].SetStatus(TargetStatus.Published);
            t[1].SetStatus(TargetStatus.Published);
            t[2].SetStatus(TargetStatus.Publishing);
            t[3].SetStatus(TargetStatus.Publishing);
            t[4].SetStatus(TargetStatus.CheckingMembership);
            foreach (var x in t.Take(5))
            {
                x.BatchNumber = 1;
                x.PublishAttempts = x.Status == TargetStatus.CheckingMembership ? 0 : 1;
            }
            c.Status = CampaignStatus.Running;
            c.StartBatch(1, 9);
            c.ExecutionOwnerId = Guid.NewGuid();
            c.LastExecutionAtUtc = DateTime.UtcNow.AddSeconds(-30);
            await db.SaveChangesAsync();
        }

        var app = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        Assert.Equal(1, await app.Service.ReconcileOrphanedCampaignsAsync());

        var recovered = await ReadAsync(id);
        var targets = recovered.Targets.OrderBy(x => x.Order).ToList();
        Assert.Equal(CampaignStatus.Interrupted, recovered.Status);
        Assert.Equal([TargetStatus.Published, TargetStatus.Published, TargetStatus.OutcomeUnknown, TargetStatus.OutcomeUnknown,
            TargetStatus.Pending, TargetStatus.Pending], targets.Select(x => x.Status));

        // Resume processes only the two Pending groups; unknown outcomes are not retried.
        await app.Service.ResumeAsync(id);
        await app.RunQueuedAsync();
        Assert.Equal([targets[4].GroupUrl, targets[5].GroupUrl], app.Automation.PublishedTo);
        Assert.Equal(CampaignStatus.CompletedWithErrors, (await ReadAsync(id)).Status);

        // Manual verification: group 3 was published, group 4 was not.
        await app.Service.ResolveUnknownOutcomeAsync(targets[2].Id, wasPublished: true);
        await app.Service.ResolveUnknownOutcomeAsync(targets[3].Id, wasPublished: false);
        var verified = await ReadAsync(id);
        Assert.Equal(TargetStatus.Published, verified.Targets.Single(x => x.Id == targets[2].Id).Status);
        Assert.Equal(TargetStatus.Pending, verified.Targets.Single(x => x.Id == targets[3].Id).Status);
        Assert.True(verified.CanBeResumed);

        var next = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        await next.Service.ResumeAsync(id);
        await next.RunQueuedAsync();
        Assert.Equal([targets[3].GroupUrl], next.Automation.PublishedTo); // only the group confirmed as not published
        var final = await ReadAsync(id);
        Assert.Equal(CampaignStatus.Completed, final.Status);
        Assert.All(final.Targets, x => Assert.Equal(TargetStatus.Published, x.Status));
    }

    [Fact]
    public async Task Startup_does_not_resume_anything_and_unknown_outcomes_need_a_stopped_campaign_to_verify()
    {
        var groups = await SeedAsync(2);
        var app = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        var id = await app.CreateAsync(groups, Pacing(minBatch: 2, maxBatch: 2)); // queued, never run

        // Restart: the new process neither runs nor re-queues it; opening the app marks it Interrupted.
        var restarted = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        Assert.Empty(restarted.Queue.Enqueued);
        await restarted.Service.ReconcileOrphanedCampaignsAsync();
        var campaign = await ReadAsync(id);
        Assert.Equal(CampaignStatus.Interrupted, campaign.Status);
        Assert.All(campaign.Targets, t => Assert.Equal(TargetStatus.Pending, t.Status));
        Assert.Empty(restarted.Automation.PublishedTo);
    }

    // ---------- Concurrency and idempotency ----------

    [Fact]
    public async Task Two_simultaneous_resume_requests_queue_the_campaign_only_once()
    {
        var groups = await SeedAsync(4);
        var app = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        var id = await app.CreateAsync(groups, Pacing(minBatch: 2, maxBatch: 2));
        await app.Service.StopAsync(id); // queued → stopped
        Assert.Equal(CampaignStatus.Stopped, (await ReadAsync(id)).Status);
        while (app.Queue.Enqueued.TryDequeue(out _))
        {
        }

        // Two requests (two tabs / double click), each with its own service and DbContext, at the same moment.
        var serviceA = app.NewService();
        var serviceB = app.NewService();
        using var gate = new SemaphoreSlim(0, 2);
        var results = await Task.WhenAll(
            Task.Run(async () => { await gate.WaitAsync(); return await TryAsync(() => serviceA.ResumeAsync(id)); }),
            Task.Run(async () => { await gate.WaitAsync(); return await TryAsync(() => serviceB.ResumeAsync(id)); }),
            Task.Run(() => { gate.Release(2); return true; }));

        Assert.Equal(1, results.Take(2).Count(ok => ok));
        Assert.Single(app.Queue.Enqueued);
        Assert.Equal(CampaignStatus.Queued, (await ReadAsync(id)).Status);
    }

    [Fact]
    public async Task Two_runners_racing_for_the_same_campaign_publish_each_group_exactly_once()
    {
        var groups = await SeedAsync(5);
        var app = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        var id = await app.CreateAsync(groups, Pacing(minBatch: 5, maxBatch: 5));
        var other = app.NewRunner(new CampaignRunnerTests.FakeAutomation());

        await Task.WhenAll(
            Task.Run(() => app.Runner.RunAsync(id, CancellationToken.None, CancellationToken.None)),
            Task.Run(() => other.Runner.RunAsync(id, CancellationToken.None, CancellationToken.None)));

        var all = app.Automation.PublishedTo.Concat(other.Automation.PublishedTo).ToList();
        Assert.Equal(5, all.Count);
        Assert.Equal(5, all.Distinct().Count());
        Assert.True(app.Automation.PublishedTo.Count == 0 || other.Automation.PublishedTo.Count == 0); // one claimed it, the other skipped
    }

    [Fact]
    public async Task Completed_campaign_cannot_be_started_or_resumed_again()
    {
        var groups = await SeedAsync(3);
        var app = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        var id = await app.CreateAsync(groups, Pacing(minBatch: 3, maxBatch: 3));
        await app.RunQueuedAsync();

        var reopened = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        await Assert.ThrowsAsync<AppValidationException>(() => reopened.Service.ResumeAsync(id));
        await Assert.ThrowsAsync<AppValidationException>(() => reopened.Service.StartAsync(id));
        Assert.Empty(reopened.Queue.Enqueued);
        Assert.Equal(CampaignStatus.Completed, (await ReadAsync(id)).Status);
    }

    [Fact]
    public async Task Saved_campaign_starts_only_when_the_user_clicks_start()
    {
        var groups = await SeedAsync(2);
        var app = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        var id = await app.CreateAsync(groups, Pacing(minBatch: 2, maxBatch: 2), start: false);

        Assert.Empty(app.Queue.Enqueued);
        Assert.Equal(CampaignStatus.NotStarted, (await ReadAsync(id)).Status);
        await Assert.ThrowsAsync<AppValidationException>(() => app.Service.ResumeAsync(id));

        await app.Service.StartAsync(id);
        await app.RunQueuedAsync();
        Assert.Equal(CampaignStatus.Completed, (await ReadAsync(id)).Status);
    }

    // ---------- Settings persistence and snapshots ----------

    [Fact]
    public async Task Settings_persist_reload_and_reject_invalid_values()
    {
        var settings = new SettingsService(_db.For(_user), NullLogger<SettingsService>.Instance);
        var model = await settings.GetAsync();
        model.Pacing = new BatchPacingConfig
        {
            MinBatchSize = 4,
            MaxBatchSize = 11,
            PostDelay = new DelayRange(1, 2, DelayUnit.Minutes),
            BatchDelay = new DelayRange(1, 2, DelayUnit.Hours),
            ExtendedDelayAfterBatches = 3,
            ExtendedDelay = new DelayRange(90, 150, DelayUnit.Minutes)
        };
        await settings.SaveAsync(model);

        var reloaded = (await new SettingsService(_db.For(_user), NullLogger<SettingsService>.Instance).GetAsync()).Pacing;
        Assert.Equal((4, 11, 3), (reloaded.MinBatchSize, reloaded.MaxBatchSize, reloaded.ExtendedDelayAfterBatches));
        Assert.Equal((1, 2, DelayUnit.Minutes), (reloaded.PostDelay.Min, reloaded.PostDelay.Max, reloaded.PostDelay.Unit));
        Assert.Equal((1, 2, DelayUnit.Hours), (reloaded.BatchDelay.Min, reloaded.BatchDelay.Max, reloaded.BatchDelay.Unit));
        Assert.Equal((90, 150, DelayUnit.Minutes), (reloaded.ExtendedDelay.Min, reloaded.ExtendedDelay.Max, reloaded.ExtendedDelay.Unit));

        model.Pacing.MaxBatchSize = 2; // below the minimum of 4
        await Assert.ThrowsAsync<AppValidationException>(() => settings.SaveAsync(model));
        Assert.Equal(11, (await settings.GetAsync()).Pacing.MaxBatchSize);
    }

    [Fact]
    public async Task Changing_global_settings_does_not_change_an_existing_campaign_snapshot()
    {
        var groups = await SeedAsync(2);
        var app = new AppProcess(_db, _user, FixedRandom.AlwaysMin());
        var id = await app.CreateAsync(groups, Pacing(minBatch: 2, maxBatch: 2), start: false);

        var settings = new SettingsService(_db.For(_user), NullLogger<SettingsService>.Instance);
        var model = await settings.GetAsync();
        model.Pacing.MinBatchSize = 7;
        model.Pacing.MaxBatchSize = 8;
        await settings.SaveAsync(model);

        var campaign = await ReadAsync(id);
        Assert.Equal((2, 2), (campaign.Pacing.MinBatchSize, campaign.Pacing.MaxBatchSize));
    }

    // ---------- helpers ----------

    private static BatchPacingConfig Pacing(int minBatch, int maxBatch, int extendedAfter = 5) => new()
    {
        MinBatchSize = minBatch,
        MaxBatchSize = maxBatch,
        ExtendedDelayAfterBatches = extendedAfter
    };

    /// <summary>
    /// The runner waits until the persisted end time, so the milliseconds spent saving and notifying after the delay
    /// was selected count towards it: allow just under a second of drift per wait.
    /// </summary>
    private static void AssertWaited(AppProcess app, TimeSpan expected)
    {
        var waits = WaitMessages(app).Count();
        Assert.InRange(app.Delay.Total, expected - TimeSpan.FromSeconds(waits), expected);
    }

    /// <summary>Waits as letters: P = between posts, B = between batches, E = extended break.</summary>
    private static string WaitSequence(AppProcess app) =>
        string.Join(' ', WaitMessages(app).Select(Letter));

    private static string? LastWait(AppProcess app) => WaitMessages(app).Select(Letter).LastOrDefault();

    private static IEnumerable<string> WaitMessages(AppProcess app)
    {
        lock (app.Notifier.Messages)
        {
            return app.Notifier.Messages
                .Where(m => m.Level == CampaignMessageLevel.Info && m.UntilUtc is not null && m.Text.Contains(": waiting"))
                .Select(m => m.Text)
                .ToList();
        }
    }

    private static string Letter(string text) =>
        text.StartsWith("Between posts") ? "P" : text.StartsWith("Between batches") ? "B" : text.StartsWith("Extended break") ? "E" : "?";

    private static async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (AppValidationException)
        {
            return false;
        }
    }

    private async Task<Campaign> ReadAsync(Guid id)
    {
        await using var db = await _db.CreateContextAsync();
        return await db.Campaigns.AsNoTracking().Include(c => c.Targets).FirstAsync(c => c.Id == id);
    }

    private async Task<(Guid ContentId, List<Guid> GroupIds)> SeedAsync(int groupCount)
    {
        await using var db = await _db.CreateContextAsync();
        if (!await db.Settings.AnyAsync(s => s.OwnerId == _user))
        {
            var settings = SettingsService.CreateDefaults();
            settings.OwnerId = _user;
            settings.MaxGroupsPerCampaign = 200;
            db.Settings.Add(settings);
        }
        var content = new ContentPost { OwnerId = _user, Title = $"Post {Guid.NewGuid():N}", Text = "Hello groups", ContentHash = Guid.NewGuid().ToString("N") };
        db.Contents.Add(content);
        var ids = new List<Guid>();
        for (var i = 1; i <= groupCount; i++)
        {
            var group = new FacebookGroup { OwnerId = _user, Name = $"Group {i:000}", Url = $"https://www.facebook.com/groups/test{i:000}-{content.Id:N}" };
            db.Groups.Add(group);
            ids.Add(group.Id);
        }
        await db.SaveChangesAsync();
        return (content.Id, ids);
    }

}
