using System.Collections.Concurrent;
using System.Threading.Channels;
using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Automation;

namespace FacebookPublisher.Infrastructure.Campaigns;

/// <summary>Uniform inclusive random integers from the shared thread-safe generator.</summary>
public sealed class SystemRandomSource : IRandomSource
{
    public int NextInclusive(int minInclusive, int maxInclusive)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minInclusive, maxInclusive);
        return maxInclusive == int.MaxValue
            ? (int)Random.Shared.NextInt64(minInclusive, (long)maxInclusive + 1)
            : Random.Shared.Next(minInclusive, maxInclusive + 1);
    }
}

/// <summary>Real waiting: Task.Delay honours the CancellationToken, so Stop interrupts even multi-hour delays at once.</summary>
public sealed class TaskAsyncDelay : IAsyncDelay
{
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        duration <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(duration, cancellationToken);
}

public sealed class CampaignQueue : ICampaignQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(Guid campaignId, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(campaignId, cancellationToken);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Running campaigns of this process. Different users' campaigns run at the same time; each user has at most one
/// running campaign because a browser profile can only be used by one browser process.
/// </summary>
public sealed class CampaignExecutionTracker : ICampaignExecutionTracker
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, (Guid OwnerId, CancellationTokenSource Cts)> _running = [];

    public Guid InstanceId { get; } = Guid.NewGuid();

    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    public IReadOnlyCollection<Guid> RunningCampaignIds
    {
        get
        {
            lock (_lock)
            {
                return _running.Keys.ToList();
            }
        }
    }

    public CancellationToken Begin(Guid campaignId, Guid ownerId, CancellationToken hostStoppingToken)
    {
        lock (_lock)
        {
            if (_running.ContainsKey(campaignId))
            {
                throw new InvalidOperationException($"Campaign {campaignId} is already running.");
            }
            if (_running.Values.Any(r => r.OwnerId == ownerId))
            {
                throw new InvalidOperationException($"User {ownerId} already has a running campaign.");
            }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(hostStoppingToken);
            _running[campaignId] = (ownerId, cts);
            return cts.Token;
        }
    }

    public bool RequestCancel(Guid campaignId)
    {
        lock (_lock)
        {
            if (!_running.TryGetValue(campaignId, out var run))
            {
                return false;
            }
            run.Cts.Cancel();
            return true;
        }
    }

    public int RequestCancelForOwner(Guid ownerId)
    {
        lock (_lock)
        {
            var runs = _running.Values.Where(r => r.OwnerId == ownerId).ToList();
            foreach (var run in runs)
            {
                run.Cts.Cancel();
            }
            return runs.Count;
        }
    }

    public void End(Guid campaignId)
    {
        lock (_lock)
        {
            if (_running.Remove(campaignId, out var run))
            {
                run.Cts.Dispose();
            }
        }
    }

    public bool IsRunning(Guid campaignId)
    {
        lock (_lock)
        {
            return _running.ContainsKey(campaignId);
        }
    }

    public bool IsOwnerBusy(Guid ownerId)
    {
        lock (_lock)
        {
            return _running.Values.Any(r => r.OwnerId == ownerId);
        }
    }
}

public sealed class CampaignApprovalGate : ICampaignApprovalGate
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _pending = new();

    public async Task<ApprovalDecision> WaitAsync(Guid targetId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[targetId] = tcs;
        try
        {
            var approved = await tcs.Task.WaitAsync(timeout, cancellationToken);
            return approved ? ApprovalDecision.Approved : ApprovalDecision.Rejected;
        }
        catch (TimeoutException)
        {
            return ApprovalDecision.TimedOut;
        }
        finally
        {
            _pending.TryRemove(targetId, out _);
        }
    }

    public bool Resolve(Guid targetId, bool approve) =>
        _pending.TryGetValue(targetId, out var tcs) && tcs.TrySetResult(approve);

    public bool IsAwaiting(Guid targetId) => _pending.ContainsKey(targetId);
}
