using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Campaigns;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Infrastructure.Campaigns;
using Microsoft.Extensions.Logging.Abstractions;

namespace FacebookPublisher.Tests;

/// <summary>
/// One run of the application as seen by one signed-in user: its own instance id, queue, browser and in-memory
/// state. "Restart" means a new process — everything that survives must come from the database. Several users of the
/// same running application share one <see cref="CampaignExecutionTracker"/> (pass it in).
/// </summary>
internal sealed class AppProcess
{
    private readonly SqlTestDatabase _db;
    private readonly IRandomSource _random;
    private readonly IAsyncDelay? _realDelay;

    public AppProcess(SqlTestDatabase db, Guid userId, IRandomSource random, IAsyncDelay? realDelay = null, CampaignExecutionTracker? sharedTracker = null)
    {
        _db = db;
        UserId = userId;
        _random = random;
        _realDelay = realDelay;
        Tracker = sharedTracker ?? new CampaignExecutionTracker();
        Automations.Use(userId, Automation);
        Service = NewService();
        Runner = CreateRunner(Automations);
    }

    public Guid UserId { get; }

    public CampaignExecutionTracker Tracker { get; }

    public RecordingQueue Queue { get; } = new();

    public CampaignRunnerTests.FakeAutomation Automation { get; } = new();

    /// <summary>The per-user browser factory this process's runner resolves sessions from.</summary>
    public FakeAutomationFactory Automations { get; } = new();

    public CampaignRunnerTests.FakeStorage Storage { get; } = new();

    public CampaignRunnerTests.RecordingNotifier Notifier { get; } = new();

    public FakeUserAccounts Accounts { get; } = new();

    public RecordingDelay Delay { get; } = new();

    public CampaignService Service { get; }

    public CampaignRunner Runner { get; }

    public CampaignService NewService() =>
        new(_db.For(UserId), Queue, Tracker, new CampaignApprovalGate(), Notifier, NullLogger<CampaignService>.Instance);

    public (CampaignRunner Runner, CampaignRunnerTests.FakeAutomation Automation) NewRunner(CampaignRunnerTests.FakeAutomation automation) =>
        (CreateRunner(new FakeAutomationFactory().Use(UserId, automation)), automation);

    public Task<Guid> CreateAsync((Guid ContentId, List<Guid> GroupIds) seed, BatchPacingConfig pacing, bool start = true) =>
        Service.CreateAsync(new CreateCampaignRequest("Marketing Campaign 01", seed.ContentId, seed.GroupIds, pacing, false), start);

    /// <summary>Does what CampaignWorker does for each queued campaign (the owner comes from the campaign record).</summary>
    public async Task RunQueuedAsync()
    {
        while (Queue.Enqueued.TryDequeue(out var id))
        {
            var owner = await Runner.GetOwnerIdAsync(id) ?? throw new InvalidOperationException("Campaign not found.");
            var token = Tracker.Begin(id, owner, CancellationToken.None);
            try
            {
                await Runner.RunAsync(id, token, CancellationToken.None);
            }
            finally
            {
                Tracker.End(id);
            }
        }
    }

    private CampaignRunner CreateRunner(FakeAutomationFactory automations) => new(
        _db, automations, Accounts, Storage, new CampaignApprovalGate(), Notifier,
        new BatchDelayService(_random), _realDelay ?? Delay, Tracker, NullLogger<CampaignRunner>.Instance);
}
