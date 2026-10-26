namespace PeripheralBatteryTray.Core;

/// <summary>At most one queued UI callback; highest snapshot version wins.</summary>
public sealed class SnapshotMailbox(Action<Action> post, Action<BatterySnapshot> display) : IDisposable
{
    private readonly object _gate = new();
    private BatterySnapshot? _latest;
    private bool _queued, _disposed;
    private long _acceptedVersion = -1, _displayedVersion = -1;
    public long DisplayedVersion { get { lock (_gate) return _displayedVersion; } }
    public void Publish(BatterySnapshot snapshot)
    {
        bool enqueue;
        lock (_gate)
        {
            if (_disposed || snapshot.Version <= _acceptedVersion) return;
            _acceptedVersion = snapshot.Version; _latest = snapshot; enqueue = !_queued; _queued = true;
        }
        if (enqueue)
        {
            try { post(Drain); }
            catch (InvalidOperationException) { Dispose(); }
        }
    }
    private void Drain()
    {
        BatterySnapshot? snapshot;
        lock (_gate)
        {
            _queued = false;
            if (_disposed) return;
            snapshot = _latest; _latest = null;
            if (snapshot is null || snapshot.Version <= _displayedVersion) return;
            _displayedVersion = snapshot.Version;
        }
        display(snapshot);
    }
    public void Dispose() { lock (_gate) { _disposed = true; _latest = null; } }
}
