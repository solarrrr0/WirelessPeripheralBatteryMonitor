using System.Collections.ObjectModel;
using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.Windows;

public enum EndpointEventKind { Added, Updated, Removed, EnumerationCompleted, Stopped, Aborted }
public sealed record EndpointEvent(EndpointEventKind Kind, AepId Id, IReadOnlyDictionary<string, object?> Properties);
public interface IEndpointWatcher
{
    event Action<EndpointEvent>? Changed;
    void Start();
    void Stop();
}
public interface IEndpointWatcherFactory { IEndpointWatcher Create(TransportKind transport, bool pairedOnly); }
public enum DiscoveryStatus { Enumerating, Completed, Incomplete, Stopping, Stopped, Failed }
public sealed record DiscoverySnapshot(long Version, IReadOnlyList<Endpoint> Endpoints,
    DiscoveryStatus ClassicStatus, DiscoveryStatus BleStatus, int ResyncRequests);

public sealed class BluetoothDiscovery(IEndpointWatcherFactory factory, TimeProvider? time = null) : IAsyncDisposable
{
    public const string Name = "System.ItemNameDisplay", Connected = "System.Devices.Aep.IsConnected",
        Paired = "System.Devices.Aep.IsPaired", Container = "System.Devices.Aep.ContainerId", Battery = "System.Devices.BatteryLife";
    private readonly object _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<TransportKind, Slot> _slots = [];
    private long _version;
    private bool _stopping;
    private bool _suspended;
    private int _resync;
    private Task? _stopTask;
    public event Action<DiscoverySnapshot>? Changed;
    private sealed class Slot(TransportKind kind)
    {
        public TransportKind Kind = kind;
        public long Epoch;
        public IEndpointWatcher? Watcher;
        public Action<EndpointEvent>? Handler;
        public DiscoveryStatus Status;
        public bool Terminal;
        public bool RestartRequested;
        public int Retries;
        public readonly Dictionary<AepId, Dictionary<string, object?>> Properties = [];
        public readonly Dictionary<AepId, Endpoint> Endpoints = [];
        public TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ITimer? Deadline, Retry;
    }
    public DiscoverySnapshot Snapshot { get { lock (_gate) return CreateSnapshot(); } }
    private DiscoverySnapshot CreateSnapshot() => new(_version, Array.AsReadOnly(_slots.Values.SelectMany(s => s.Endpoints.Values).ToArray()),
        _slots.GetValueOrDefault(TransportKind.Classic)?.Status ?? DiscoveryStatus.Stopped,
        _slots.GetValueOrDefault(TransportKind.Ble)?.Status ?? DiscoveryStatus.Stopped, _resync);
    public void Start()
    {
        foreach (TransportKind kind in new[] { TransportKind.Classic, TransportKind.Ble })
        {
            Slot slot;
            lock (_gate)
            {
                if (_stopping || _suspended) return;
                if (_slots.TryGetValue(kind, out var existing) && !existing.Terminal) continue;
                if (existing is null) _slots[kind] = slot = new(kind); else { slot = existing; slot.Retries = 0; }
            }
            StartSlot(slot);
        }
    }
    private void StartSlot(Slot slot)
    {
        long epoch;
        lock (_gate)
        {
            if (_stopping || _suspended || (slot.Watcher is not null && !slot.Terminal)) return;
            epoch = ++slot.Epoch; slot.Terminal = false; slot.Status = DiscoveryStatus.Enumerating;
            slot.RestartRequested = false;
            slot.Retry?.Dispose(); slot.Retry = null;
            slot.Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            slot.Properties.Clear(); slot.Endpoints.Clear();
        }
        try
        {
            var watcher = factory.Create(slot.Kind, true);
            Action<EndpointEvent> handler = evt => Receive(slot, epoch, evt);
            bool stop;
            lock (_gate) { slot.Watcher = watcher; slot.Handler = handler; stop = _stopping; }
            watcher.Changed += handler;
            if (!stop) watcher.Start(); else watcher.Stop();
            lock (_gate)
            {
                if (!_stopping && slot.Status == DiscoveryStatus.Enumerating)
                    slot.Deadline = _time.CreateTimer(_ => EnumerationTimeout(slot, epoch), null,
                        TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception) { Receive(slot, epoch, new(EndpointEventKind.Aborted, default, new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>()))); }
    }
    private void EnumerationTimeout(Slot slot, long epoch)
    {
        DiscoverySnapshot snapshot;
        lock (_gate) { if (_stopping || slot.Epoch != epoch || slot.Status != DiscoveryStatus.Enumerating) return;
            slot.Status = DiscoveryStatus.Incomplete; ++_version; snapshot = CreateSnapshot(); }
        Notify(snapshot);
    }
    private void Receive(Slot slot, long epoch, EndpointEvent evt)
    {
        DiscoverySnapshot snapshot; IEndpointWatcher? detach = null; Action<EndpointEvent>? handler = null;
        lock (_gate)
        {
            if (slot.Epoch != epoch || slot.Terminal) return;
            bool terminal = evt.Kind is EndpointEventKind.Stopped or EndpointEventKind.Aborted;
            if ((_stopping || _suspended || slot.RestartRequested) && !terminal) return;
            if (terminal)
            {
                slot.Terminal = true; slot.Deadline?.Dispose(); slot.Deadline = null;
                slot.Status = _stopping || _suspended || slot.RestartRequested ? DiscoveryStatus.Stopped : DiscoveryStatus.Failed;
                foreach (AepId id in slot.Endpoints.Keys.ToArray()) slot.Endpoints[id] = slot.Endpoints[id] with { Connection = ConnectionState.Unknown, Present = false };
                slot.Stopped.TrySetResult(); detach = slot.Watcher; handler = slot.Handler;
                if (!_stopping && !_suspended && !slot.RestartRequested && slot.Retries < 3)
                {
                    int delayMinutes = 1 << slot.Retries++;
                    slot.Retry = _time.CreateTimer(_ => StartSlot(slot), null, TimeSpan.FromMinutes(delayMinutes), Timeout.InfiniteTimeSpan);
                }
            }
            else if (evt.Kind == EndpointEventKind.EnumerationCompleted)
            { slot.Status = DiscoveryStatus.Completed; slot.Deadline?.Dispose(); slot.Deadline = null; }
            else if (evt.Kind == EndpointEventKind.Removed)
            { if (slot.Endpoints.TryGetValue(evt.Id, out var old)) slot.Endpoints[evt.Id] = old with { Connection = ConnectionState.Unknown, Present = false }; }
            else
            {
                if (!slot.Properties.TryGetValue(evt.Id, out var properties))
                {
                    if (evt.Kind == EndpointEventKind.Updated) { _resync++; return; }
                    slot.Properties[evt.Id] = properties = new(StringComparer.Ordinal);
                }
                foreach (var pair in evt.Properties.Where(p => p.Key is Name or Connected or Paired or Container or Battery)) properties[pair.Key] = pair.Value;
                object? Get(string key) => properties.GetValueOrDefault(key);
                slot.Endpoints[evt.Id] = new(evt.Id, slot.Kind, Get(Name) as string ?? "蓝牙设备",
                    Get(Paired) is true, Get(Connected) is bool connected ? connected ? ConnectionState.Connected : ConnectionState.Disconnected : ConnectionState.Unknown,
                    Get(Container) is Guid guid && guid != Guid.Empty ? new(guid) : null,
                    Get(Battery) is byte b && b <= 100 ? b : null);
            }
            ++_version; snapshot = CreateSnapshot();
        }
        if (detach is not null && handler is not null) detach.Changed -= handler;
        Notify(snapshot);
        if (slot.RestartRequested) StartSlot(slot);
    }
    private void Notify(DiscoverySnapshot snapshot)
    {
        lock (_gate) { if (_stopping) return; }
        try { Changed?.Invoke(snapshot); } catch (Exception) { }
    }
    public void Suspend()
    {
        Slot[] slots;
        lock (_gate)
        {
            if (_stopping || _suspended) return;
            _suspended = true; slots = _slots.Values.ToArray();
            foreach (var slot in slots)
            {
                slot.RestartRequested = false;
                slot.Deadline?.Dispose(); slot.Retry?.Dispose();
                slot.Properties.Clear(); slot.Endpoints.Clear();
                slot.Status = slot.Terminal ? DiscoveryStatus.Stopped : DiscoveryStatus.Stopping;
            }
            ++_version;
        }
        foreach (var slot in slots.Where(s => !s.Terminal))
            try { slot.Watcher?.Stop(); } catch (Exception) { /* Wait for original terminal; never overlap watchers. */ }
    }
    public void Resume()
    {
        Slot[] slots;
        lock (_gate)
        {
            if (_stopping || !_suspended) return;
            _suspended = false; slots = _slots.Values.ToArray();
            foreach (var slot in slots) { slot.RestartRequested = true; slot.Retries = 0; }
        }
        foreach (var slot in slots.Where(s => s.Terminal)) StartSlot(slot);
    }
    public Task StopAsync()
    {
        Slot[] slots;
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask;
            _stopping = true; Changed = null; slots = _slots.Values.ToArray();
            foreach (var slot in slots) { slot.Deadline?.Dispose(); slot.Retry?.Dispose(); if (!slot.Terminal) slot.Status = DiscoveryStatus.Stopping; }
            _stopTask = Task.WhenAll(slots.Select(s => s.Stopped.Task));
        }
        foreach (var slot in slots.Where(s => !s.Terminal))
        { try { slot.Watcher?.Stop(); } catch (Exception) { /* Unresolved terminal remains explicit. */ } }
        return _stopTask;
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
