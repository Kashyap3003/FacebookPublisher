using System.Collections.Concurrent;
using FacebookPublisher.Application.Abstractions;
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

/// <summary>
/// A throw-away SQL Server LocalDB database with the real EF Core migrations applied, so row-version concurrency,
/// enum/owned-column mapping and the migration itself are exercised exactly as in production.
/// </summary>
internal sealed class SqlTestDatabase : IApplicationDbContextFactory, IAsyncDisposable
{
    private readonly DbContextOptions<AppDbContext> _options;

    private SqlTestDatabase(string connectionString)
    {
        ConnectionString = connectionString;
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
    }

    public string ConnectionString { get; }

    public static async Task<SqlTestDatabase> CreateAsync()
    {
        var name = $"FacebookPublisherTests_{Guid.NewGuid():N}";
        var connectionString = $"Server=(localdb)\\MSSQLLocalDB;Database={name};Trusted_Connection=True;TrustServerCertificate=True";
        var database = new SqlTestDatabase(connectionString);
        await using var db = new AppDbContext(database._options);
        await db.Database.MigrateAsync();
        return database;
    }

    public Task<IApplicationDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IApplicationDbContext>(new AppDbContext(_options));

    public AppDbContext CreateContext() => new(_options);

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var db = new AppDbContext(_options);
        await db.Database.EnsureDeletedAsync();
    }
}
