using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.Windows;

public static class ConnectionEvidence
{
    public static (ConnectionState State, bool NeedsResync) Combine(ConnectionState watcher, ConnectionState? direct)
        => direct is null || direct == watcher ? (watcher, false) : (ConnectionState.Unknown, true);
}

/// <summary>Owns discovery and metadata lifetime. Construction is inert; Start explicitly enables read-only discovery.</summary>
public sealed class WindowsBatteryRuntime
{
    private readonly object _applyGate = new();
    private readonly BluetoothDiscovery _discovery;
    private readonly PnpIndexService _index;
    private readonly DeviceIdentityResolver _resolver = new();
    private readonly BindingRegistry _registry;
    private readonly WindowsBatteryProvider _provider;
    private BatteryCoordinator? _coordinator;
    private HashSet<string> _ids = new(StringComparer.Ordinal);
    private Dictionary<string, byte?> _batteries = new(StringComparer.Ordinal);
    private Task? _stop;
    private bool _stopped;
    private bool _suspended;
    public IBatteryProvider Provider { get; }
    public BindingRegistry Bindings => _registry;
    public DiscoverySnapshot Discovery => _discovery.Snapshot;
    public IndexSnapshot Index => _index.Snapshot;
    public string? DiscoveryNotice => _discovery.Snapshot is var s && (s.ClassicStatus == DiscoveryStatus.Failed || s.BleStatus == DiscoveryStatus.Failed)
        ? "部分蓝牙发现失败，设备列表可能不完整；系统报告时效未知。"
        : s.ClassicStatus != DiscoveryStatus.Completed || s.BleStatus != DiscoveryStatus.Completed
            ? "蓝牙发现尚未完成，设备列表可能不完整。" : null;
    public WindowsBatteryRuntime(TimeProvider? time = null, IEndpointWatcherFactory? watcherFactory = null, IPnpNative? native = null,
        DiagnosticTelemetry? telemetry = null)
    {
        var lane = new MetadataLane();
        IPnpNative adapter = native ?? new PnpNative();
        if (telemetry is not null) adapter = new ObservedPnpNative(adapter, telemetry);
        var reader = new PnpPropertyReader(adapter);
        _registry = new(); _provider = new(_registry, reader, lane, time);
        _discovery = new(watcherFactory ?? new WinRtEndpointWatcherFactory(), time); _index = new(reader, lane, time);
        Provider = new OwnedProvider(this);
    }
    public void Start(BatteryCoordinator coordinator)
    {
        lock (_applyGate)
        {
            if (_coordinator is not null || _stopped) return;
            _coordinator = coordinator; _discovery.Changed += OnDiscovery; _index.Changed += OnIndex;
        }
        _discovery.Start(); _index.RequestRebuild();
    }
    public void Reidentify() { lock (_applyGate) if (_stopped) return; _index.RequestRebuild(); }
    public void Suspend()
    {
        lock (_applyGate)
        {
            if (_stopped || _suspended || _coordinator is null) return;
            _suspended = true;
            _coordinator.Suspend();
            _resolver.InvalidateConnections();
            _registry.Replace([]);
            _discovery.Suspend();
        }
    }
    public void Resume()
    {
        lock (_applyGate)
        {
            if (_stopped || !_suspended || _coordinator is null) return;
            _suspended = false;
            _coordinator.Resume();
            _discovery.Resume();
            _index.RequestRebuild();
        }
    }
    private void OnDiscovery(DiscoverySnapshot _) => Reconcile();
    private void OnIndex(IndexSnapshot _) => Reconcile();
    private void Reconcile()
    {
        lock (_applyGate)
        {
            if (_stopped || _suspended || _coordinator is null) return;
            DiscoverySnapshot discovery = _discovery.Snapshot;
            var bindings = _resolver.Resolve(discovery.Endpoints, _index.Snapshot.Nodes);
            _registry.Replace(bindings);
            var nextIds = bindings.Select(b => b.Device.Binding.DeviceId).ToHashSet(StringComparer.Ordinal);
            foreach (string removed in _ids.Except(nextIds)) _coordinator.RemoveDevice(removed);
            foreach (var binding in bindings)
            {
                bool ready = binding.Endpoints.Where(e => e.Transport == TransportKind.Ble).All(_ => discovery.BleStatus == DiscoveryStatus.Completed);
                _coordinator.UpsertDevice(binding.Device, ready && binding.GattCandidate);
                bool changedProperty = false;
                foreach (Endpoint endpoint in binding.Endpoints)
                {
                    string key = endpoint.Id.Value;
                    if (_batteries.TryGetValue(key, out byte? old) && old != endpoint.Battery) changedProperty = true;
                }
                if (changedProperty) _coordinator.Refresh(binding.Device.Binding.DeviceId, RefreshReason.Property);
            }
            _batteries = discovery.Endpoints.ToDictionary(e => e.Id.Value, e => e.Battery, StringComparer.Ordinal);
            _ids = nextIds;
            // Empty/failed discovery must also update the host's status notice.
            _coordinator.RepublishSnapshot();
        }
    }
    public Task StopAsync()
    {
        lock (_applyGate)
        {
            if (_stop is not null) return _stop;
            _stopped = true; _discovery.Changed -= OnDiscovery; _index.Changed -= OnIndex;
            _stop = Task.WhenAll(_discovery.StopAsync(), _index.DisposeAsync().AsTask(), _provider.DisposeAsync().AsTask());
            return _stop;
        }
    }
    private sealed class OwnedProvider(WindowsBatteryRuntime owner) : IBatteryProvider
    {
        public ReadingSource Source => ReadingSource.WindowsProperty;
        public IBatteryReadOperation CreateRead(QueryRequest request, CancellationToken lifetime) => owner._provider.CreateRead(request, lifetime);
        public async ValueTask DisposeAsync() => await owner.StopAsync().ConfigureAwait(false);
    }
}
