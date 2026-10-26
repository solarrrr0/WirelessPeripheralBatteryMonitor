using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

namespace PeripheralBatteryTray.App;

internal sealed record ManualGattValue(ReadOutcome Outcome, int? Percent, DateTimeOffset ObservedAtUtc);
internal sealed record ManualGattState(int Attempts, bool Busy, bool TimedOut, double RetryAfterSeconds, ReadOutcome? Outcome,
    int CleanupErrors, IReadOnlyList<ManualGattValue> Values, TelemetrySnapshot Telemetry);

/// <summary>One explicitly requested read at a time. Never attached to periodic scheduling.</summary>
internal sealed class ManualGattSession
{
    private readonly object _gate = new();
    private readonly BindingRegistry _bindings;
    private readonly IBatteryProvider _provider;
    private readonly Func<bool> _discoveryReady;
    private readonly TimeProvider _time;
    private readonly DiagnosticTelemetry _telemetry;
    private readonly List<ManualGattValue> _values = [];
    private IBatteryReadOperation? _active;
    private long? _lastStarted;
    private int _attempts, _cleanupErrors;
    private bool _timedOut, _closing;
    private ReadOutcome? _outcome;
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    internal ManualGattSession(BindingRegistry bindings, IBatteryProvider provider, Func<bool> discoveryReady,
        DiagnosticTelemetry telemetry, TimeProvider? time = null)
        => (_bindings, _provider, _discoveryReady, _telemetry, _time) = (bindings, provider, discoveryReady, telemetry, time ?? TimeProvider.System);

    internal ManualGattState State
    {
        get { lock (_gate) return new(_attempts, _active is not null, _timedOut,
            _lastStarted is long last ? Math.Max(0, 10 - _time.GetElapsedTime(last, _time.GetTimestamp()).TotalSeconds) : 0, _outcome,
            _cleanupErrors, _values.ToArray(), _telemetry.Capture()); }
    }

    internal bool TryRead(string bindingKey)
    {
        IBatteryReadOperation operation;
        lock (_gate)
        {
            if (_closing || _active is not null || !_discoveryReady()
                || (_lastStarted is long last && _time.GetElapsedTime(last, _time.GetTimestamp()) < TimeSpan.FromSeconds(10))) return false;
            var binding = _bindings.Find(bindingKey);
            if (binding is null || !binding.GattCandidate || binding.Device.Binding.SleepInvalidated
                || binding.Device.Binding.Ble?.Connection != ConnectionState.Connected
                || binding.Endpoints.Count(e => e.Transport == TransportKind.Ble && e.Paired && e.Present
                    && e.Connection == ConnectionState.Connected) != 1) return false;
            long started = _time.GetTimestamp();
            var request = new QueryRequest(binding.Device.Binding.Stamp(ReadingSource.GattUncached, _attempts + 1),
                bindingKey, RefreshReason.Manual, started, Budget, ["$directory"]);
            operation = _provider.CreateRead(request, CancellationToken.None);
            _active = operation; _lastStarted = started; _attempts++; _values.Clear(); _outcome = null; _timedOut = false;
            _telemetry.Record(ReadingSource.GattUncached, DiagnosticStep.QueryStarted);
        }
        try { operation.Start(update => Accept(operation, update)); }
        catch
        {
            lock (_gate) { _outcome = ReadOutcome.Error; _cleanupErrors++; }
            Cancel(operation); _ = Observe(operation); return false;
        }
        _ = Observe(operation);
        return true;
    }

    private void Accept(IBatteryReadOperation operation, ProviderUpdate update)
    {
        lock (_gate)
        {
            if (_closing || _timedOut || _active != operation
                || _time.GetElapsedTime(_lastStarted!.Value, _time.GetTimestamp()) >= Budget) return;
            if (update is ValueUpdate value && _values.Count < 64)
            {
                _values.Add(new(value.Outcome, value.Value?.Percent, value.ObservedAtUtc));
                if (value.Outcome != ReadOutcome.Success) _outcome = value.Outcome;
                if (value.Outcome == ReadOutcome.Success && value.Value?.Percent is not null)
                    _telemetry.Record(ReadingSource.GattUncached, DiagnosticStep.ValueAccepted);
            }
            else if (update is ServiceFailureUpdate failure) _outcome = failure.Outcome;
            else if (update is DirectoryChangedUpdate) _values.Clear();
        }
    }

    private async Task Observe(IBatteryReadOperation operation)
    {
        ReadCompletion result;
        try { result = await operation.Completion.ConfigureAwait(false); }
        catch { result = new(ReadOutcome.Error, 1); }
        Complete(operation, result);
    }
    private void Complete(IBatteryReadOperation operation, ReadCompletion result)
    {
        lock (_gate)
        {
            if (_active != operation) return;
            _active = null; _cleanupErrors += result.CleanupErrors;
            _outcome = _timedOut ? ReadOutcome.TimedOut : _outcome ?? result.Outcome;
            _telemetry.Record(ReadingSource.GattUncached, DiagnosticStep.OperationTerminal, result.Outcome, result.CleanupErrors);
        }
    }

    internal void Tick()
    {
        IBatteryReadOperation? cancel = null;
        lock (_gate)
        {
            if (_active is not null && !_timedOut && _lastStarted is long started
                && _time.GetElapsedTime(started, _time.GetTimestamp()) >= Budget)
            { _timedOut = true; _outcome = ReadOutcome.TimedOut; cancel = _active;
                _telemetry.Record(ReadingSource.GattUncached, DiagnosticStep.LogicalTimeout); }
        }
        if (cancel is not null) Cancel(cancel);
    }
    private void Cancel(IBatteryReadOperation operation)
    {
        try { operation.RequestCancel(); }
        catch { lock (_gate) _cleanupErrors++; }
        _telemetry.Record(ReadingSource.GattUncached, DiagnosticStep.CancelRequested);
    }
    internal async Task<ShutdownResult> StopAsync()
    {
        IBatteryReadOperation? active;
        lock (_gate) { _closing = true; active = _active; }
        if (active is not null) Cancel(active);
        Task disposal;
        try { disposal = _provider.DisposeAsync().AsTask(); }
        catch { lock (_gate) _cleanupErrors++; return new(false, active is null ? 0 : 1, _cleanupErrors); }
        try { await disposal.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
        catch { lock (_gate) _cleanupErrors++; }
        if (active?.Completion.IsCompleted == true)
        {
            try { Complete(active, await active.Completion); }
            catch { Complete(active, new(ReadOutcome.Error, 1)); }
        }
        // Keep observing late native cleanup without disposing in-flight objects early.
        if (!disposal.IsCompleted) _ = ObserveLate(disposal);
        lock (_gate) return new(disposal.IsCompletedSuccessfully && (_active is null || _active.Completion.IsCompleted)
            && _cleanupErrors == 0, _active?.Completion.IsCompleted == false ? 1 : 0, _cleanupErrors);
    }
    private async Task ObserveLate(Task disposal) { try { await disposal.ConfigureAwait(false); } catch { lock (_gate) _cleanupErrors++; } }
}
