using System.ComponentModel;
using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.Windows;

public enum AtkManualStatus { NotRun, Inspecting, InterfacesChecked, NoInterface, AmbiguousInterfaces,
    Reading, OfflineObservation, DifferentModel, ObservationOnly, NeedsProtocolReview,
    TimedOut, Cancelled, AccessDenied, InvalidReply, Failed }
public sealed record AtkManualState(AtkManualStatus Status, bool Busy, bool Quarantined, double RetryAfterSeconds,
    int CandidateCount, int InspectionErrors, int ReadBatches, int RequestsStarted, int RequestsTerminal,
    int CleanupErrors, AtkReplyError? RejectedReply, IReadOnlyList<AtkReplyObservation> Observations)
{
    // A protocol observation must not enter current battery aggregation before real response validation.
    public int? AcceptedPercent => null;
}

/// <summary>Explicit diagnostics only: inspect, then at most one manually requested telemetry batch.</summary>
public sealed class AtkManualSession(IAtkHidTransport transport, TimeProvider? time = null)
{
    private readonly object _gate = new();
    private readonly IAtkHidTransport _transport = transport;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private AtkHidEndpoint[] _endpoints = [];
    private readonly List<AtkReplyObservation> _observations = [];
    private Task? _active;
    private Task<ShutdownResult>? _stop;
    private CancellationTokenSource? _cancellation;
    private AtkManualStatus _status;
    private long _started;
    private long? _lastRead;
    private bool _reading, _closed, _quarantined, _timedOut;
    private int _inspectionErrors, _batches, _requests, _terminals, _cleanupErrors;
    private AtkReplyError? _rejectedReply;
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    public AtkManualState State
    {
        get
        {
            lock (_gate) return new(_status, _active is not null, _quarantined,
                _lastRead is long last ? Math.Max(0, 10 - _time.GetElapsedTime(last, _time.GetTimestamp()).TotalSeconds) : 0,
                _endpoints.Length, _inspectionErrors, _batches, _requests, _terminals, _cleanupErrors, _rejectedReply, _observations.ToArray());
        }
    }
    public bool TryInspect() => Start(false);
    public bool TryRead() => Start(true);
    private bool Start(bool read)
    {
        TaskCompletionSource done; CancellationTokenSource cancellation; AtkHidEndpoint? endpoint = null;
        lock (_gate)
        {
            if (_closed || _active is not null || _quarantined) return false;
            if (read)
            {
                if (_endpoints.Length != 1 || _inspectionErrors != 0 || !AtkHidPolicy.Allows(_endpoints[0].Descriptor)
                    || (_lastRead is long last && _time.GetElapsedTime(last, _time.GetTimestamp()) < TimeSpan.FromSeconds(10))) return false;
                endpoint = _endpoints[0]; _lastRead = _time.GetTimestamp(); _batches++;
            }
            else { _endpoints = []; _inspectionErrors = 0; }
            _started = _time.GetTimestamp(); _reading = read; _timedOut = false; _rejectedReply = null; _observations.Clear();
            _status = read ? AtkManualStatus.Reading : AtkManualStatus.Inspecting;
            cancellation = _cancellation = new();
            done = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = done.Task;
        }
        _ = Run(endpoint, cancellation, done);
        return true;
    }

    private void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_closed || _timedOut || _time.GetElapsedTime(_started, _time.GetTimestamp()) >= Budget)
                throw new OperationCanceledException(token);
        }
    }
    private async Task<AtkReplyObservation> Query(IAtkHidConnection connection, AtkReadCommand command, CancellationToken token)
    {
        Check(token);
        lock (_gate) _requests++;
        AtkReplyObservation observation;
        try { observation = await connection.QueryAsync(command, token).ConfigureAwait(false); }
        finally { lock (_gate) _terminals++; }
        if (observation.Command != command) throw new AtkInvalidReplyException(AtkReplyError.Command);
        lock (_gate) { Check(token); _observations.Add(observation); }
        return observation;
    }
    private async Task Run(AtkHidEndpoint? endpoint, CancellationTokenSource cancellation, TaskCompletionSource done)
    {
        IAtkHidConnection? connection = null;
        CancellationToken token = cancellation.Token;
        try
        {
            Check(token);
            if (endpoint is null)
            {
                var scan = await _transport.ScanAsync(token).ConfigureAwait(false); Check(token);
                var allowed = scan.Endpoints.Where(e => AtkHidPolicy.Allows(e.Descriptor)).ToArray();
                lock (_gate)
                {
                    Check(token);
                    _endpoints = allowed; _inspectionErrors = scan.InspectionErrors;
                    _status = scan.InspectionErrors != 0 ? AtkManualStatus.NeedsProtocolReview : allowed.Length switch
                    { 0 => AtkManualStatus.NoInterface, 1 => AtkManualStatus.InterfacesChecked, _ => AtkManualStatus.AmbiguousInterfaces };
                }
                return;
            }
            connection = await _transport.OpenAsync(endpoint, token).ConfigureAwait(false); Check(token);
            var online = await Query(connection, AtkReadCommand.WirelessOnline, token).ConfigureAwait(false);
            if (online.OnlineCandidate != true)
            { Set(online.OnlineCandidate == false ? AtkManualStatus.OfflineObservation : AtkManualStatus.NeedsProtocolReview); return; }
            var model = await Query(connection, AtkReadCommand.MouseIdentity, token).ConfigureAwait(false);
            if (model.DuckbillUltraCandidate != true) { Set(AtkManualStatus.DifferentModel); return; }
            var battery = await Query(connection, AtkReadCommand.Battery, token).ConfigureAwait(false);
            Set(battery.PercentCandidate is not null ? AtkManualStatus.ObservationOnly : AtkManualStatus.NeedsProtocolReview);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                bool timeout = _timedOut || (!_closed && _time.GetElapsedTime(_started, _time.GetTimestamp()) >= Budget);
                _status = timeout ? AtkManualStatus.TimedOut : AtkManualStatus.Cancelled;
                _quarantined |= _reading;
            }
        }
        catch (AtkInvalidReplyException error) { lock (_gate) _rejectedReply = error.Error; Fail(AtkManualStatus.InvalidReply); }
        catch (UnauthorizedAccessException) { Fail(AtkManualStatus.AccessDenied); }
        catch (Win32Exception error) { Fail(error.NativeErrorCode is 5 or 32 ? AtkManualStatus.AccessDenied : AtkManualStatus.Failed); }
        catch (Exception) { Fail(AtkManualStatus.Failed); }
        finally
        {
            if (connection is not null)
            {
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                catch { lock (_gate) { _cleanupErrors++; _quarantined = true; _status = AtkManualStatus.Failed; } }
            }
            lock (_gate) { _active = null; _cancellation = null; cancellation.Dispose(); done.TrySetResult(); }
        }
    }
    private void Set(AtkManualStatus status) { lock (_gate) if (!_timedOut && !_closed) _status = status; }
    private void Fail(AtkManualStatus status) { lock (_gate) { _status = _timedOut ? AtkManualStatus.TimedOut : status; _quarantined |= _reading; } }
    public void Tick()
    {
        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            if (_active is not null && !_timedOut && _time.GetElapsedTime(_started, _time.GetTimestamp()) >= Budget)
            { _timedOut = true; _quarantined |= _reading; _status = AtkManualStatus.TimedOut; cancel = _cancellation; }
        }
        Cancel(cancel);
    }
    private void Cancel(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        catch { lock (_gate) _cleanupErrors++; }
    }
    public Task<ShutdownResult> StopAsync()
    {
        TaskCompletionSource<ShutdownResult> done; Task? active; CancellationTokenSource? cancel;
        lock (_gate)
        {
            if (_stop is not null) return _stop;
            _closed = true; active = _active; cancel = _cancellation;
            done = new(TaskCreationOptions.RunContinuationsAsynchronously); _stop = done.Task;
        }
        Cancel(cancel);
        _ = StopCore(active, done);
        return done.Task;
    }
    private async Task StopCore(Task? active, TaskCompletionSource<ShutdownResult> done)
    {
        if (active is not null) try { await active.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
        lock (_gate) done.TrySetResult(new(_active is null && _cleanupErrors == 0, _active is null ? 0 : 1, _cleanupErrors));
    }
}
