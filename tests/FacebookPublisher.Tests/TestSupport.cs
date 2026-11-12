using System.Collections.Concurrent;
using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Automation;
using FacebookPublisher.Infrastructure.Identity;
using FacebookPublisher.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FacebookPublisher.Tests;

/// <summary>Records every requested wait and returns immediately (honouring cancellation), so hours of pacing run instantly.</summary>
internal sealed class RecordingDelay : IAsyncDelay
{
    private readonly ConcurrentQueue<TimeSpan> _calls = new();

    public IReadOnlyCollection<TimeSpan> Calls => _calls;

    public TimeSpan Total => TimeSpan.FromTicks(_calls.Sum(c => c.Ticks));

    /// <summary>Optional hook run on each wait, e.g. to press Stop while the campaign is waiting.</summary>
    public Func<TimeSpan, CancellationToken, Task>? OnDelay { get; set; }

    public async Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Enqueue(duration);
        if (OnDelay is not null)
        {
            await OnDelay(duration, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>Deterministic randomness: always the minimum, always the maximum, or a custom picker.</summary>
internal sealed class FixedRandom(Func<int, int, int> pick) : IRandomSource
{
    public static FixedRandom AlwaysMin() => new((min, _) => min);

    public static FixedRandom AlwaysMax() => new((_, max) => max);

    public List<(int Min, int Max)> Requests { get; } = [];

    public int NextInclusive(int minInclusive, int maxInclusive)
    {
        Requests.Add((minInclusive, maxInclusive));
        return pick(minInclusive, maxInclusive);
    }
}

/// <summary>Captures what Start/Resume put on the campaign queue.</summary>
internal sealed class RecordingQueue : ICampaignQueue
{
    public ConcurrentQueue<Guid> Enqueued { get; } = new();

    public ValueTask EnqueueAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        Enqueued.Enqueue(campaignId);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
}


/// <summary>Creates plain AppDbContext instances from fixed options (stands in for EF's pooled factory).</summary>
public sealed class OptionsDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(options);
}

/// <summary>A fixed signed-in user, as the web host would resolve it from the authentication cookie.</summary>
internal sealed class FakeCurrentUser(Guid userId, bool isAdmin = false) : ICurrentUser
{
    public Task<Guid> GetUserIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(userId);

    public Task<bool> IsAdminAsync(CancellationToken cancellationToken = default) => Task.FromResult(isAdmin);
}

/// <summary>Every account is active unless listed in <see cref="Disabled"/>.</summary>
internal sealed class FakeUserAccounts : IUserAccounts
{
    public ConcurrentDictionary<Guid, bool> Disabled { get; } = new();

    public Task<bool> IsActiveAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(!Disabled.ContainsKey(userId));
}

/// <summary>One fake browser per application user, mirroring the real per-user factory.</summary>
internal sealed class FakeAutomationFactory : IFacebookAutomationFactory
{
    private readonly ConcurrentDictionary<Guid, CampaignRunnerTests.FakeAutomation> _byOwner = new();

    public ConcurrentQueue<Guid> Requests { get; } = new();

    public FakeAutomationFactory Use(Guid ownerId, CampaignRunnerTests.FakeAutomation automation)
    {
        automation.OwnerId = ownerId;
        _byOwner[ownerId] = automation;
        return this;
    }

    public CampaignRunnerTests.FakeAutomation For(Guid ownerId) =>
        _byOwner.GetOrAdd(ownerId, id => new CampaignRunnerTests.FakeAutomation { OwnerId = id });

    public IFacebookAutomation ForUser(Guid ownerId)
    {
        Requests.Enqueue(ownerId);
        return For(ownerId);
    }
}

/// <summary>Contexts scoped to one fixed user (what the web host's current-user factory produces).</summary>
public sealed class FixedUserDbContextFactory(ISystemDbContextFactory system, Guid userId) : IApplicationDbContextFactory
{
    public Task<IApplicationDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
        system.CreateForOwnerAsync(userId, cancellationToken);
}

/// <summary>
/// A throw-away SQL Server LocalDB database with the real EF Core migrations applied, so row-version concurrency,
/// enum/owned-column mapping, ownership foreign keys and the migrations themselves are exercised as in production.
/// </summary>
public sealed class SqlTestDatabase : ISystemDbContextFactory, IAsyncDisposable
{
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AppDbContextFactory _system;

    private SqlTestDatabase(string connectionString)
    {
        ConnectionString = connectionString;
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
        _system = new AppDbContextFactory(new OptionsDbContextFactory(_options));
    }

    public string ConnectionString { get; }

    public IDbContextFactory<AppDbContext> ContextFactory => new OptionsDbContextFactory(_options);

    public static async Task<SqlTestDatabase> CreateAsync()
    {
        var name = $"FacebookPublisherTests_{Guid.NewGuid():N}";
        var connectionString = $@"Server=(localdb)\MSSQLLocalDB;Database={name};Trusted_Connection=True;TrustServerCertificate=True";
        var database = new SqlTestDatabase(connectionString);
        await using var db = new AppDbContext(database._options);
        await db.Database.MigrateAsync();
        return database;
    }

    /// <summary>Creates an application user row (Identity table) so ownership foreign keys are satisfied.</summary>
    public async Task<Guid> CreateUserAsync(string email, bool disabled = false)
    {
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString(),
            IsDisabled = disabled
        };
        await using var db = new AppDbContext(_options);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Contexts scoped to <paramref name="userId"/>, as user-facing services receive them.</summary>
    public IApplicationDbContextFactory For(Guid userId) => new FixedUserDbContextFactory(_system, userId);

    public Task<IApplicationDbContext> CreateForOwnerAsync(Guid ownerId, CancellationToken cancellationToken = default) =>
        _system.CreateForOwnerAsync(ownerId, cancellationToken);

    public Task<IApplicationDbContext> CreateUnscopedAsync(CancellationToken cancellationToken = default) =>
        _system.CreateUnscopedAsync(cancellationToken);

    /// <summary>Unscoped context for test assertions and seeding.</summary>
    public async Task<AppDbContext> CreateContextAsync() => (AppDbContext)await _system.CreateUnscopedAsync();

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var db = new AppDbContext(_options);
        await db.Database.EnsureDeletedAsync();
    }
}
