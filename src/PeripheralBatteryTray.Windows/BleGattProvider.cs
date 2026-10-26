using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.Windows;

/// <summary>Inert until an eligible request is explicitly started. Does not own polling timers.</summary>
public sealed class BleGattProvider(BindingRegistry registry, IGattClient client,
    TimeProvider? time = null, Action<string, ConnectionState>? reportConnection = null) : IBatteryProvider
{
    private readonly BindingRegistry _registry = registry;
    private readonly IGattClient _client = client;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Action<string, ConnectionState>? _reportConnection = reportConnection;
    private readonly GattDirectoryCache _cache = new();
    private readonly object _gate = new();
    private readonly HashSet<Operation> _active = [];
    private bool _disposed;
    public ReadingSource Source => ReadingSource.GattUncached;
    public IBatteryReadOperation CreateRead(QueryRequest request, CancellationToken lifetime)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return new Operation(this, request, lifetime); }
    }
    public async ValueTask DisposeAsync()
    {
        Operation[] operations; lock (_gate) { _disposed = true; operations = _active.ToArray(); }
        foreach (var operation in operations) operation.RequestCancel();
        await Task.WhenAll(operations.Select(o => o.Completion)).ConfigureAwait(false);
    }
    private sealed class Operation(BleGattProvider owner, QueryRequest request, CancellationToken lifetime) : IBatteryReadOperation
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<ReadCompletion> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<IGattService> _owned = [];
        private readonly HashSet<object> _identities = new(ReferenceEqualityComparer.Instance);
        private IGattCall? _current;
        private IGattDevice? _device;
        private Action<ProviderUpdate>? _publish;
        private int _started, _cancelled, _cleanupErrors;
        private long _sequence;
        public Task<ReadCompletion> Completion => _completion.Task;
        public void RequestCancel()
        {
            if (Interlocked.Exchange(ref _cancelled, 1) != 0) return;
            CancelCurrent();
        }
        private void CancelCurrent()
        {
            IGattCall? call; lock (_gate) call = _current;
            try { call?.Cancel(); } catch (Exception) { Interlocked.Increment(ref _cleanupErrors); }
        }
        private bool CanContinue()
        {
            if (Volatile.Read(ref _cancelled) != 0 || lifetime.IsCancellationRequested
                || owner._time.GetElapsedTime(request.StartedTimestamp, owner._time.GetTimestamp()) >= request.Budget) return false;
            var binding = owner._registry.Find(request.BindingKey);
            return binding is not null && binding.GattCandidate && binding.Device.Binding.Ble?.Connection == ConnectionState.Connected
                && request.Stamp.Source == ReadingSource.GattUncached
                && BatteryRules.MatchesBinding(request.Stamp, binding.Device.Binding with { GattDatabaseEpoch = request.Stamp.DatabaseEpoch });
        }
        private void Check() { if (!CanContinue()) throw new OperationCanceledException(); }
        private async Task<T> Await<T>(Func<IGattCall<T>> start)
        {
            Check(); var call = start();
            lock (_gate) _current = call;
            if (Volatile.Read(ref _cancelled) != 0 || lifetime.IsCancellationRequested) CancelCurrent();
            try { return await call.Terminal.ConfigureAwait(false); }
            finally { lock (_gate) if (_current == call) _current = null; }
        }
        public void Start(Action<ProviderUpdate> publish)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Already started.");
            _publish = publish;
            lock (owner._gate)
            {
                if (owner._disposed) Interlocked.Exchange(ref _cancelled, 1);
                owner._active.Add(this);
            }
            _ = Run();
        }
        private void Emit(ProviderUpdate update) { Check(); _publish?.Invoke(update); }
        private void Fail(string path, ReadOutcome outcome) => Emit(new ServiceFailureUpdate(Interlocked.Increment(ref _sequence), path, outcome));
        private static ReadOutcome Outcome(GattStatus status) => status == GattStatus.AccessDenied ? ReadOutcome.AccessDenied : ReadOutcome.Error;
        private string ServiceKey(ushort handle) => $"{request.Stamp.BindingEpoch}:{request.Stamp.TransportEpoch}:{request.Stamp.DatabaseEpoch}/s:{handle}";
        private string ComponentKey(IGattService service, IGattCharacteristic characteristic) => ServiceKey(service.Handle) + "/c:" + characteristic.Handle;
        private void Register(IEnumerable<IGattService> services)
        { foreach (var service in services) if (_identities.Add(service.Identity)) _owned.Add(service); }
        private void ServicesChanged()
        {
            if (!CanContinue()) return;
            // Control event invalidates Core immediately before cancellation; do not recurse into discovery.
            try { _publish?.Invoke(new DirectoryChangedUpdate(Interlocked.Increment(ref _sequence))); } finally { RequestCancel(); }
        }
        private void ConnectionChanged()
        {
            if (_device is null || _device.Connection == ConnectionState.Connected) return;
            owner._reportConnection?.Invoke(request.Stamp.DeviceId, _device.Connection);
            // Core database invalidation immediately removes current eligibility even without a new watcher event.
            ServicesChanged(); RequestCancel();
        }
        private async Task Run()
        {
            ReadOutcome final = ReadOutcome.Success;
            using var registration = lifetime.Register(RequestCancel);
            try
            {
                Check(); var binding = owner._registry.Find(request.BindingKey)!;
                Endpoint? endpoint = binding.Endpoints.SingleOrDefault(e => e.Transport == TransportKind.Ble && e.Paired && e.Present);
                if (endpoint is null) { final = ReadOutcome.Offline; Fail("$directory", final); return; }
                _device = await Await(() => owner._client.Open(endpoint.Id, CanContinue)).ConfigureAwait(false);
                // Late-opened object is owned even when result acceptance has already expired.
                if (_device is null) { Check(); final = ReadOutcome.Unknown; return; }
                _device.ServicesChanged += ServicesChanged; _device.ConnectionChanged += ConnectionChanged;
                Check(); if (_device.Connection != ConnectionState.Connected)
                { owner._reportConnection?.Invoke(request.Stamp.DeviceId, _device.Connection); final = ReadOutcome.Offline; Fail("$directory", final); return; }
                bool rebuild = owner._cache.ForceUncached(request.Stamp);
                GattCacheMode mode = rebuild ? GattCacheMode.Uncached : GattCacheMode.Cached;
                var services = await Await(() => _device.Services(BatteryGattUuids.Service, mode)).ConfigureAwait(false);
                Register(services.Value); Check();
                if (services.Status == GattStatus.Success && services.Value.Count == 0 && mode == GattCacheMode.Cached)
                { services = await Await(() => _device.Services(BatteryGattUuids.Service, GattCacheMode.Uncached)).ConfigureAwait(false); Register(services.Value); Check(); }
                if (services.Status != GattStatus.Success) { final = Outcome(services.Status); Protocol(services.Status); Fail("$directory", final); return; }
                if (services.Value.Count > 8 || services.Value.Any(s => s.Handle == 0)
                    || services.Value.Select(s => s.Handle).Distinct().Count() != services.Value.Count)
                { Emit(new DirectoryUpdate(Interlocked.Increment(ref _sequence), DirectoryScope.GattServices, null, [], false)); final = ReadOutcome.Error; Fail("$directory", final); return; }
                string signature = string.Join(",", services.Value.Select(s => s.Handle).Order());
                if (owner._cache.ServicesChanged(request.Stamp, signature))
                { Emit(new DirectoryChangedUpdate(Interlocked.Increment(ref _sequence), services.Value.Count == 0)); RequestCancel(); final = ReadOutcome.Cancelled; return; }
                Emit(new DirectoryUpdate(Interlocked.Increment(ref _sequence), DirectoryScope.GattServices, null,
                    services.Value.OrderBy(s => s.Handle).Select((s, i) => new DirectoryMember(ServiceKey(s.Handle) + "/pending", ServiceKey(s.Handle), i)), true));
                if (services.Value.Count == 0) { final = ReadOutcome.Unsupported; Fail("$directory", final); return; }
                int order = 0;
                foreach (IGattService service in services.Value.OrderBy(s => s.Handle))
                {
                    string key = ServiceKey(service.Handle); int display = order++;
                    if (!request.DueServiceKeys.Contains("$directory") && !request.DueServiceKeys.Contains(key)) continue;
                    try
                    {
                    Check(); mode = rebuild ? GattCacheMode.Uncached : GattCacheMode.Cached;
                    var chars = await Await(() => service.Characteristics(BatteryGattUuids.Level, mode)).ConfigureAwait(false); Check();
                    if (chars.Status == GattStatus.Success && chars.Value.Count == 0 && mode == GattCacheMode.Cached)
                    { chars = await Await(() => service.Characteristics(BatteryGattUuids.Level, GattCacheMode.Uncached)).ConfigureAwait(false); Check(); }
                    if (chars.Status != GattStatus.Success) { Protocol(chars.Status); Fail(key, Outcome(chars.Status)); continue; }
                    if (chars.Value.Count > 1 || chars.Value.Any(c => c.Handle == 0)) { Fail(key, ReadOutcome.Error); continue; }
                    string charSignature = string.Join(",", chars.Value.Select(c => c.Handle + ":" + c.CanRead));
                    if (owner._cache.CharacteristicsChanged(request.Stamp, key, charSignature))
                    { Emit(new DirectoryChangedUpdate(Interlocked.Increment(ref _sequence), chars.Value.Count == 0)); RequestCancel(); final = ReadOutcome.Cancelled; return; }
                    if (chars.Value.Count == 0 || !chars.Value[0].CanRead) { Fail(key, ReadOutcome.Unsupported); continue; }
                    var characteristic = chars.Value[0]; string component = ComponentKey(service, characteristic);
                    Emit(new DirectoryUpdate(Interlocked.Increment(ref _sequence), DirectoryScope.ServiceCharacteristics, key,
                        [new(component, key, display)], true));
                    var value = await Await(() => characteristic.Read(GattCacheMode.Uncached)).ConfigureAwait(false); Check();
                    BatteryValue? parsed = value.Status == GattStatus.Success && value.Value is { Length: 1 } bytes && bytes[0] <= 100
                        ? BatteryValue.FromPercent(bytes[0]) : null;
                    Protocol(value.Status);
                    Emit(new ValueUpdate(Interlocked.Increment(ref _sequence), component, key, parsed is not null ? ReadOutcome.Success : Outcome(value.Status),
                        parsed, owner._time.GetUtcNow(), owner._time.GetTimestamp()));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { Fail(key, error.HResult == unchecked((int)0x80070005) ? ReadOutcome.AccessDenied : ReadOutcome.Error); }
                }
            }
            catch (OperationCanceledException) { final = owner._time.GetElapsedTime(request.StartedTimestamp, owner._time.GetTimestamp()) >= request.Budget
                    ? ReadOutcome.TimedOut : ReadOutcome.Cancelled; }
            catch (UnauthorizedAccessException) { final = ReadOutcome.AccessDenied; SafeFail(final); }
            catch (Exception) { final = ReadOutcome.Error; SafeFail(final); }
            finally
            {
                _publish = null;
                if (_device is not null)
                {
                    TryCleanup(() => _device.ServicesChanged -= ServicesChanged);
                    TryCleanup(() => _device.ConnectionChanged -= ConnectionChanged);
                }
                foreach (var service in _owned) TryCleanup(service.Dispose);
                if (_device is not null) TryCleanup(_device.Dispose);
                lock (owner._gate) owner._active.Remove(this);
                _completion.TrySetResult(new(final, _cleanupErrors));
            }
        }
        private void Protocol(GattStatus status) { if (status == GattStatus.ProtocolError) owner._cache.MarkProtocolFailure(request.Stamp); }
        private void SafeFail(ReadOutcome outcome) { try { if (CanContinue()) Fail("$directory", outcome); } catch (Exception) { } }
        private void TryCleanup(Action cleanup) { try { cleanup(); } catch (Exception) { _cleanupErrors++; } }
    }
}
