using System.Collections.Concurrent;
using System.Threading.Channels;
using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Automation;

namespace FacebookPublisher.Infrastructure.Campaigns;

public sealed class CampaignQueue : ICampaignQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(Guid campaignId, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(campaignId, cancellationToken);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class CampaignExecutionTracker : ICampaignExecutionTracker
{
    private readonly Lock _lock = new();
    private Guid? _activeId;
    private CancellationTokenSource? _cts;

    public Guid? ActiveCampaignId
    {
        get
        {
            lock (_lock)
            {
                return _activeId;
            }
        }
    }

    public CancellationToken Begin(Guid campaignId, CancellationToken hostStoppingToken)
    {
        lock (_lock)
        {
            if (_activeId is not null)
            {
                throw new InvalidOperationException($"Campaign {_activeId} is already running.");
            }
            _activeId = campaignId;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(hostStoppingToken);
            return _cts.Token;
        }
    }

    public bool RequestCancel(Guid campaignId)
    {
        lock (_lock)
        {
            if (_activeId != campaignId || _cts is null)
            {
                return false;
            }
            _cts.Cancel();
            return true;
        }
    }

    public void End(Guid campaignId)
    {
        lock (_lock)
        {
            if (_activeId != campaignId)
            {
                return;
            }
            _cts?.Dispose();
            _cts = null;
            _activeId = null;
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
