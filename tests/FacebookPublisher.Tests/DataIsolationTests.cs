using FacebookPublisher.Application.Campaigns;
using FacebookPublisher.Application.Common;
using FacebookPublisher.Application.Content;
using FacebookPublisher.Application.Groups;
using FacebookPublisher.Application.Reports;
using FacebookPublisher.Application.Settings;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;
using FacebookPublisher.Infrastructure.Campaigns;
using FacebookPublisher.Infrastructure.Persistence;
using FacebookPublisher.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FacebookPublisher.Tests;

/// <summary>
/// User A and user B share one database (SQL Server LocalDB, real migrations). Every service is called exactly as the
/// web host calls it for the signed-in user; B must never see, change or execute anything of A's.
/// </summary>
public class DataIsolationTests : IAsyncLifetime
{
    private SqlTestDatabase _db = null!;
    private string _uploads = null!;
    private Guid _a;
    private Guid _b;

    public async Task InitializeAsync()
    {
        _db = await SqlTestDatabase.CreateAsync();
        _a = await _db.CreateUserAsync("user-a@example.test");
        _b = await _db.CreateUserAsync("user-b@example.test");
        _uploads = Path.Combine(Path.GetTempPath(), "gp-isolation-" + Guid.NewGuid().ToString("N"));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (Directory.Exists(_uploads))
        {
            Directory.Delete(_uploads, recursive: true);
        }
    }

    [Fact]
    public async Task Groups_are_private_and_the_same_group_can_be_saved_by_each_user()
    {
        var a1 = await Groups(_a).AddAsync(new SaveGroupRequest("A one", "https://www.facebook.com/groups/shared", null));
        await Groups(_a).AddAsync(new SaveGroupRequest("A two", "https://www.facebook.com/groups/a-only", null));
        await Groups(_b).AddAsync(new SaveGroupRequest("B copy", "https://www.facebook.com/groups/shared", null));

        Assert.Equal(["A one", "A two"], (await Groups(_a).ListAsync()).Select(g => g.Name).Order());
        Assert.Equal(["B copy"], (await Groups(_b).ListAsync()).Select(g => g.Name));

        await Assert.ThrowsAsync<NotFoundException>(() => Groups(_b).UpdateAsync(a1.Id, new SaveGroupRequest("hijack", a1.Url, null)));
        await Assert.ThrowsAsync<NotFoundException>(() => Groups(_b).DeleteAsync(a1.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Groups(_b).SetActiveAsync(a1.Id, false));
        await Assert.ThrowsAsync<NotFoundException>(() => Groups(_b).VerifyAccessAsync(a1.Id));
        Assert.Equal("A one", (await Groups(_a).ListAsync()).Single(g => g.Id == a1.Id).Name);
    }

    [Fact]
    public async Task Content_and_attachments_are_private_and_stored_in_the_owners_folder()
    {
        var contentId = await Content(_a).CreateAsync(new CreateContentRequest("A post", "hello",
            [new UploadFile("photo.png", 4, () => new MemoryStream([1, 2, 3, 4]))]));
        var attachmentId = (await Content(_a).GetAsync(contentId)).Attachments.Single().Id;

        Assert.Empty(await Content(_b).ListAsync());
        await Assert.ThrowsAsync<NotFoundException>(() => Content(_b).GetAsync(contentId));
        await Assert.ThrowsAsync<NotFoundException>(() => Content(_b).DeleteAsync(contentId));
        Assert.Null(await Content(_b).GetAttachmentFileAsync(attachmentId));

        var file = await Content(_a).GetAttachmentFileAsync(attachmentId);
        Assert.NotNull(file);
        Assert.StartsWith(Path.Combine(_uploads, _a.ToString("N")), file.FullPath);
        Assert.True(File.Exists(file.FullPath));
    }

    [Fact]
    public async Task Campaigns_reports_and_dashboard_are_private()
    {
        var (campaignId, targetId) = await CreateCampaignForAsync(_a, start: false);

        Assert.Null(await Campaigns(_b).GetProgressAsync(campaignId));
        Assert.Empty(await Campaigns(_b).ListAsync());
        await Assert.ThrowsAsync<NotFoundException>(() => Campaigns(_b).StartAsync(campaignId));
        await Assert.ThrowsAsync<NotFoundException>(() => Campaigns(_b).ResumeAsync(campaignId));
        await Assert.ThrowsAsync<NotFoundException>(() => Campaigns(_b).StopAsync(campaignId));
        await Assert.ThrowsAsync<NotFoundException>(() => Campaigns(_b).ResolveUnknownOutcomeAsync(targetId, true));
        await Assert.ThrowsAsync<NotFoundException>(() => Campaigns(_b).SubmitApprovalAsync(targetId, true));
        await Assert.ThrowsAsync<NotFoundException>(() => Reports(_b).GetCampaignReportAsync(campaignId));

        var bDashboard = await Reports(_b).GetDashboardAsync();
        Assert.Equal((0, 0, 0), (bDashboard.TotalGroups, bDashboard.TotalContents, bDashboard.TotalCampaigns));
        var aDashboard = await Reports(_a).GetDashboardAsync();
        Assert.Equal((2, 1, 1), (aDashboard.TotalGroups, aDashboard.TotalContents, aDashboard.TotalCampaigns));
        Assert.Equal(campaignId, (await Reports(_a).GetCampaignReportAsync(campaignId)).CampaignId);
    }

    [Fact]
    public async Task A_campaign_cannot_be_built_from_another_users_content_or_groups()
    {
        var groupOfA = await Groups(_a).AddAsync(new SaveGroupRequest(null, "https://www.facebook.com/groups/of-a", null));
        var contentOfA = await Content(_a).CreateAsync(new CreateContentRequest("A post", "hello", []));
        var groupOfB = await Groups(_b).AddAsync(new SaveGroupRequest(null, "https://www.facebook.com/groups/of-b", null));
        var contentOfB = await Content(_b).CreateAsync(new CreateContentRequest("B post", "hello", []));

        var withForeignContent = await Assert.ThrowsAsync<AppValidationException>(() => Campaigns(_b).CreateAsync(
            new CreateCampaignRequest("x", contentOfA, [groupOfB.Id], new BatchPacingConfig(), false), start: false));
        Assert.Contains(withForeignContent.Errors, e => e.Contains("Select the content"));

        var withForeignGroup = await Assert.ThrowsAsync<AppValidationException>(() => Campaigns(_b).CreateAsync(
            new CreateCampaignRequest("x", contentOfB, [groupOfA.Id], new BatchPacingConfig(), false), start: false));
        Assert.Contains(withForeignGroup.Errors, e => e.Contains("no longer exist"));
    }

    [Fact]
    public async Task Settings_are_per_user()
    {
        var aModel = await Settings(_a).GetAsync();
        aModel.Pacing.MinBatchSize = 7;
        aModel.MaxGroupsPerCampaign = 120;
        await Settings(_a).SaveAsync(aModel);

        var bModel = await Settings(_b).GetAsync();
        Assert.Equal((5, 50), (bModel.Pacing.MinBatchSize, bModel.MaxGroupsPerCampaign));
        bModel.Headless = true;
        await Settings(_b).SaveAsync(bModel);

        var reloadedA = await Settings(_a).GetAsync();
        Assert.Equal((7, 120, false), (reloadedA.Pacing.MinBatchSize, reloadedA.MaxGroupsPerCampaign, reloadedA.Headless));
        await using var db = await _db.CreateContextAsync();
        Assert.Equal(2, await db.Settings.CountAsync());
    }

    [Fact]
    public async Task The_data_layer_rejects_writes_to_another_users_records_even_if_a_service_had_a_bug()
    {
        var groupOfA = await Groups(_a).AddAsync(new SaveGroupRequest("A", "https://www.facebook.com/groups/a-guarded", null));
        FacebookGroup entityOfA;
        await using (var unscoped = await _db.CreateContextAsync())
        {
            entityOfA = await unscoped.Groups.AsNoTracking().SingleAsync(g => g.Id == groupOfA.Id);
        }

        await using (var asB = (AppDbContext)await _db.CreateForOwnerAsync(_b))
        {
            // B's context cannot even see the record...
            Assert.False(await asB.Groups.AnyAsync(g => g.Id == groupOfA.Id));
            // ...and forcing an update of it is refused before anything reaches the database.
            entityOfA.Name = "changed by B";
            asB.Groups.Update(entityOfA);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => asB.SaveChangesAsync());
        }

        await using (var asB = (AppDbContext)await _db.CreateForOwnerAsync(_b))
        {
            asB.Groups.Add(new FacebookGroup { OwnerId = _a, Name = "planted", Url = "https://www.facebook.com/groups/planted" });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => asB.SaveChangesAsync());
        }

        // A context that was never scoped to a user (e.g. the one Identity uses) sees no application data at all.
        await using var unscopedByDefault = _db.ContextFactory.CreateDbContext();
        Assert.Equal(0, await unscopedByDefault.Groups.CountAsync());
        Assert.Equal("A", (await Groups(_a).ListAsync()).Single().Name);
    }

    [Fact]
    public async Task Crash_recovery_only_touches_the_signed_in_users_campaigns()
    {
        var (campaignOfA, _) = await CreateCampaignForAsync(_a, start: false);
        var (campaignOfB, _) = await CreateCampaignForAsync(_b, start: false);
        await using (var db = await _db.CreateContextAsync())
        {
            foreach (var c in await db.Campaigns.ToListAsync())
            {
                c.Status = CampaignStatus.Running;
                c.ExecutionOwnerId = Guid.NewGuid(); // a previous process that died
                c.LastExecutionAtUtc = DateTime.UtcNow.AddMinutes(-30);
            }
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await Campaigns(_b).ReconcileOrphanedCampaignsAsync());

        await using var check = await _db.CreateContextAsync();
        Assert.Equal(CampaignStatus.Running, (await check.Campaigns.SingleAsync(c => c.Id == campaignOfA)).Status);
        Assert.Equal(CampaignStatus.Interrupted, (await check.Campaigns.SingleAsync(c => c.Id == campaignOfB)).Status);
    }

    [Fact]
    public async Task Live_events_of_a_campaign_carry_only_its_owner()
    {
        var app = new AppProcess(_db, _a, FixedRandom.AlwaysMin());
        var seed = await SeedForAsync(_a, 3);
        await app.CreateAsync(seed, new BatchPacingConfig());

        await app.RunQueuedAsync();

        Assert.NotEmpty(app.Notifier.Events);
        Assert.All(app.Notifier.Events, e => Assert.Equal(_a, e.OwnerId));
    }

    // ---------- helpers ----------

    private GroupService Groups(Guid user) => new(_db.For(user), new FakeAutomationFactory(), new FakeCurrentUser(user),
        new CampaignExecutionTracker(), NullLogger<GroupService>.Instance);

    private ContentService Content(Guid user) => new(_db.For(user),
        new LocalFileStorage(Options.Create(new FileStorageOptions { RootPath = _uploads }), _uploads, NullLogger<LocalFileStorage>.Instance),
        Options.Create(new ContentUploadOptions()), NullLogger<ContentService>.Instance);

    private CampaignService Campaigns(Guid user) => new(_db.For(user), new RecordingQueue(), new CampaignExecutionTracker(),
        new CampaignApprovalGate(), new CampaignRunnerTests.RecordingNotifier(), NullLogger<CampaignService>.Instance);

    private ReportService Reports(Guid user) => new(_db.For(user));

    private SettingsService Settings(Guid user) => new(_db.For(user), NullLogger<SettingsService>.Instance);

    private async Task<(Guid CampaignId, Guid TargetId)> CreateCampaignForAsync(Guid user, bool start)
    {
        var seed = await SeedForAsync(user, 2);
        var id = await Campaigns(user).CreateAsync(
            new CreateCampaignRequest("Campaign", seed.ContentId, seed.GroupIds, new BatchPacingConfig(), false), start);
        var targetId = (await Campaigns(user).GetProgressAsync(id))!.Targets.First().Id;
        return (id, targetId);
    }

    private async Task<(Guid ContentId, List<Guid> GroupIds)> SeedForAsync(Guid user, int groups)
    {
        var groupIds = new List<Guid>();
        for (var i = 0; i < groups; i++)
        {
            groupIds.Add((await Groups(user).AddAsync(new SaveGroupRequest(null, $"https://www.facebook.com/groups/g{i}-{Guid.NewGuid():N}", null))).Id);
        }
        var contentId = await Content(user).CreateAsync(new CreateContentRequest($"Post {Guid.NewGuid():N}", "hello", []));
        return (contentId, groupIds);
    }
}
