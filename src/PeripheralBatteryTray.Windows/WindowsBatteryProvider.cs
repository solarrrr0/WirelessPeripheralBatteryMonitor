using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.Windows;

/// <summary>A single worker shared by indexing and battery properties. Busy work is never replaced on logical timeout.</summary>
public sealed class MetadataLane
{
    private readonly object _gate = new();
    private Task? _active;
    public bool IsBusy { get { lock (_gate) return _active is not null; } }
    public Task Completion { get { lock (_gate) return _active ?? Task.CompletedTask; } }
    public bool TryStart(Action action, out Task terminal)
    {
        lock (_gate)
        {
            if (_active is not null) { terminal = _active; return false; }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _active = terminal = completion.Task;
            _ = Task.Run(() =>
            {
                Exception? error = null; try { action(); } catch (Exception e) { error = e; }
                lock (_gate) _active = null;
                if (error is null) completion.TrySetResult(); else completion.TrySetException(error);
            });
            return true;
        }
    }
}
public sealed class BindingRegistry
{
    private readonly object _gate = new();
    private Dictionary<string, ResolvedBinding> _bindings = new(StringComparer.Ordinal);
    public void Replace(IEnumerable<ResolvedBinding> bindings)
    { lock (_gate) _bindings = bindings.ToDictionary(b => b.Device.Binding.BindingKey, StringComparer.Ordinal); }
    public ResolvedBinding? Find(string key) { lock (_gate) return _bindings.GetValueOrDefault(key); }
    public IReadOnlyList<ResolvedBinding> Snapshot() { lock (_gate) return _bindings.Values.ToArray(); }
}
public sealed class WindowsBatteryProvider(BindingRegistry registry, PnpPropertyReader reader,
    MetadataLane lane, TimeProvider? time = null) : IBatteryProvider
{
    private readonly BindingRegistry _registry = registry;
    private readonly PnpPropertyReader _reader = reader;
    private readonly MetadataLane _lane = lane;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private bool _disposed;
    public ReadingSource Source => ReadingSource.WindowsProperty;
    public IBatteryReadOperation CreateRead(QueryRequest request, CancellationToken lifetime)
    { ObjectDisposedException.ThrowIf(_disposed, this); return new Operation(this, request, lifetime); }
    public async ValueTask DisposeAsync() { _disposed = true; try { await _lane.Completion.ConfigureAwait(false); } catch (Exception) { } }
    private sealed class Operation(WindowsBatteryProvider owner, QueryRequest request, CancellationToken lifetime) : IBatteryReadOperation
    {
        private readonly TaskCompletionSource<ReadCompletion> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cancel = new();
        private int _started;
        public Task<ReadCompletion> Completion => _completion.Task;
        public void RequestCancel() { try { _cancel.Cancel(); } catch (ObjectDisposedException) { } }
        public void Start(Action<ProviderUpdate> publish)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Already started.");
            if (!owner._lane.TryStart(() => Work(publish), out Task terminal))
            { _completion.TrySetResult(new(ReadOutcome.Error)); _cancel.Dispose(); return; }
            _ = Observe(terminal);
        }
        private async Task Observe(Task terminal)
        {
            ReadOutcome outcome = ReadOutcome.Success;
            try { await terminal.ConfigureAwait(false); }
            catch (OperationCanceledException) { outcome = ReadOutcome.Cancelled; }
            catch (UnauthorizedAccessException) { outcome = ReadOutcome.AccessDenied; }
            catch (Exception) { outcome = ReadOutcome.Error; }
            _cancel.Dispose(); _completion.TrySetResult(new(outcome));
        }
        private void Work(Action<ProviderUpdate> publish)
        {
            long sequence = 0;
            void Check() { lifetime.ThrowIfCancellationRequested(); _cancel.Token.ThrowIfCancellationRequested(); }
            void Emit(string candidate, BatteryValue? value, ReadOutcome outcome)
            { Check(); publish(new ValueUpdate(++sequence, candidate, null, outcome, value, owner._time.GetUtcNow(), owner._time.GetTimestamp())); }
            Check(); ResolvedBinding? binding = owner._registry.Find(request.BindingKey);
            if (binding is null || binding.Device.Binding.BindingEpoch != request.Stamp.BindingEpoch) throw new OperationCanceledException();
            int alias = 0;
            foreach (Endpoint endpoint in binding.Endpoints)
            { Check(); Emit("aep:" + ++alias, endpoint.Battery is byte battery ? BatteryValue.FromPercent(battery) : null,
                endpoint.Battery.HasValue ? ReadOutcome.Success : ReadOutcome.Unknown); }
            alias = 0;
            foreach (PnpNode node in binding.Nodes)
                foreach ((PropertyKey key, bool compatibility) in new[] { (BatteryPropertyKeys.Standard, false), (BatteryPropertyKeys.Compatibility, true) })
                {
                    Check(); RawProperty raw = owner._reader.Read(node.Id, key); Check();
                    BatteryValue? value = PnpPropertyReader.DecodeBattery(raw, compatibility);
                    ReadOutcome result = value is not null ? ReadOutcome.Success : raw.Status == PropertyStatus.AccessDenied ? ReadOutcome.AccessDenied
                        : raw.Status is PropertyStatus.Success or PropertyStatus.Missing ? ReadOutcome.Unknown : ReadOutcome.Error;
                    Emit("pnp:" + alias + (compatibility ? ":compatibility" : ":standard"), value, result);
                    if (compatibility) alias++;
                }
        }
    }
}
