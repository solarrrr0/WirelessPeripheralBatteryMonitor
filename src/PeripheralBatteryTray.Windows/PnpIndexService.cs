namespace PeripheralBatteryTray.Windows;

public sealed record IndexSnapshot(long Version, IReadOnlyList<PnpNode> Nodes, bool Complete, bool Draining, bool Failed);

/// <summary>Rebuild intent coalesces; one shared metadata lane and at least 30s between original starts.</summary>
public sealed class PnpIndexService(PnpPropertyReader reader, MetadataLane lane, TimeProvider? time = null) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private CancellationTokenSource? _cancel;
    private ITimer? _deadline, _retry;
    private bool _pending, _running, _stopped;
    private long? _started;
    private long _version;
    private List<PnpNode> _partial = [];
    private IndexSnapshot _snapshot = new(0, [], false, false, false);
    public event Action<IndexSnapshot>? Changed;
    public IndexSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public void RequestRebuild()
    {
        lock (_gate) { if (_stopped) return; _pending = true; }
        TryStart();
    }
    private void TryStart()
    {
        CancellationTokenSource cancel;
        lock (_gate)
        {
            if (_stopped || _running || !_pending) return;
            TimeSpan elapsed = _started.HasValue ? _time.GetElapsedTime(_started.Value, _time.GetTimestamp()) : TimeSpan.FromDays(1);
            if (elapsed < TimeSpan.FromSeconds(30))
            { _retry?.Dispose(); _retry = _time.CreateTimer(_ => TryStart(), null, TimeSpan.FromSeconds(30) - elapsed, Timeout.InfiniteTimeSpan); return; }
            // A battery-property worker may already own the lane. Await its original terminal, once.
            if (lane.IsBusy)
            { _running = true; _ = WaitLane(); return; }
            _running = true; _pending = false; _started = _time.GetTimestamp(); _partial = [];
            _cancel = cancel = new();
            _deadline = _time.CreateTimer(_ => DeadlineReached(cancel), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
        if (!lane.TryStart(() => reader.BuildIndex(cancel.Token, node =>
        {
            lock (_gate) if (!_stopped && !cancel.IsCancellationRequested && _started.HasValue
                && _time.GetElapsedTime(_started.Value, _time.GetTimestamp()) < TimeSpan.FromSeconds(2)) _partial.Add(node);
        }), out Task terminal))
        {
            lock (_gate) { _pending = true; _deadline?.Dispose(); _cancel = null; }
            cancel.Dispose(); _ = WaitLane(); return;
        }
        _ = Observe(terminal, cancel);
    }
    private async Task WaitLane()
    {
        try { await lane.Completion.ConfigureAwait(false); } catch (Exception) { }
        lock (_gate) _running = false;
        TryStart();
    }
    private void DeadlineReached(CancellationTokenSource cancel)
    {
        IndexSnapshot snapshot;
        lock (_gate)
        {
            if (_stopped || _cancel != cancel || !_running) return;
            cancel.Cancel(); _snapshot = snapshot = new(++_version, Array.AsReadOnly(_partial.ToArray()), false, true, false);
        }
        Notify(snapshot);
    }
    private async Task Observe(Task terminal, CancellationTokenSource cancel)
    {
        bool failed = false; try { await terminal.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception) { failed = true; }
        IndexSnapshot? snapshot = null;
        lock (_gate)
        {
            if (_cancel == cancel)
            {
                _deadline?.Dispose(); _deadline = null; _running = false;
                if (!_stopped) _snapshot = snapshot = new(++_version, Array.AsReadOnly(_partial.ToArray()),
                    !cancel.IsCancellationRequested && !failed && _time.GetElapsedTime(_started!.Value, _time.GetTimestamp()) < TimeSpan.FromSeconds(2), false, failed);
            }
        }
        cancel.Dispose(); if (snapshot is not null) Notify(snapshot); TryStart();
    }
    private void Notify(IndexSnapshot snapshot) { try { Changed?.Invoke(snapshot); } catch (Exception) { } }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) { _stopped = true; Changed = null; _pending = false; _deadline?.Dispose(); _retry?.Dispose();
            try { _cancel?.Cancel(); } catch (ObjectDisposedException) { } }
        try { await lane.Completion.ConfigureAwait(false); } catch (Exception) { }
    }
}
