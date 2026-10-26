using System.ComponentModel;
using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.Windows;

public enum AtkBatchOutcome { NotRun, Reading, Success, NoInterface, Offline, TransientFailure,
    AmbiguousInterfaces, DifferentModel, InvalidReply, TimedOut, Cancelled, QueryFailed, CleanupFailed }
public sealed record AtkBatchResult(AtkBatchOutcome Outcome, int? Percent = null,
    long CapturedTimestamp = 0, DateTimeOffset? ObservedAtUtc = null);
public sealed record AtkBatchState(bool Busy, bool Blocked, bool Invalidated, bool QueryMayHaveStarted,
    AtkBatchOutcome Outcome, int Batches, int BatchesTerminal, int RequestsStarted, int RequestsTerminal,
    int Timeouts, int CleanupErrors, TimeSpan Remaining);

/// <summary>Production telemetry batch. Owns native occupancy through completion and cleanup.</summary>
public sealed class AtkBatterySession(IAtkHidTransport transport, TimeProvider? time = null)
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private readonly object _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private CancellationTokenSource? _cancellation;
    private Task<AtkBatchResult>? _active;
    private Task<AtkBatchResult> _completion = Task.FromResult(new AtkBatchResult(AtkBatchOutcome.NotRun));
    private Task<ShutdownResult>? _stop;
    private bool _closed, _blocked, _invalidated, _mayHaveSent;
    private long _started;
    private AtkBatchOutcome _outcome;
    private int _batches, _terminals, _requests, _requestTerminals, _timeouts, _cleanup;

    public Task<AtkBatchResult> ActiveTask { get { lock (_gate) return _completion; } }
    public AtkBatchState State
    {
        get
        {
            lock (_gate) return new(_active is not null, _blocked, _invalidated, _mayHaveSent, _outcome,
                _batches, _terminals, _requests, _requestTerminals, _timeouts, _cleanup,
                _active is null ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Max(0, (Budget - _time.GetElapsedTime(_started)).Ticks)));
        }
    }
    public bool TryStart()
    {
        CancellationTokenSource cancel; TaskCompletionSource<AtkBatchResult> done;
        lock (_gate)
        {
            if (_closed || _blocked || _active is not null) return false;
            _started = _time.GetTimestamp(); _mayHaveSent = _invalidated = false;
            _outcome = AtkBatchOutcome.Reading; _batches++;
            cancel = _cancellation = new();
            done = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = _completion = done.Task;
        }
        _ = RunAsync(cancel, done);
        return true;
    }
    private void Check(CancellationToken token)
    {
        lock (_gate)
        {
            if (!_invalidated && _time.GetElapsedTime(_started) >= Budget) Invalidate(AtkBatchOutcome.TimedOut);
            if (_closed || _invalidated) throw new OperationCanceledException(token);
        }
        token.ThrowIfCancellationRequested();
    }
    private void Invalidate(AtkBatchOutcome reason)
    {
        if (_invalidated) return;
        _invalidated = true; _outcome = reason; _blocked |= _mayHaveSent;
        if (reason == AtkBatchOutcome.TimedOut) _timeouts++;
    }
    public void Tick()
    {
        CancellationTokenSource? cancel = null;
        lock (_gate)
            if (_active is not null && !_invalidated && _time.GetElapsedTime(_started) >= Budget)
            { Invalidate(AtkBatchOutcome.TimedOut); cancel = _cancellation; }
        RequestCancel(cancel);
    }
    public void Cancel()
    {
        CancellationTokenSource? cancel;
        lock (_gate) { if (_active is null) return; Invalidate(AtkBatchOutcome.Cancelled); cancel = _cancellation; }
        RequestCancel(cancel);
    }
    private void RequestCancel(CancellationTokenSource? cancel)
    {
        try { cancel?.Cancel(); }
        catch (ObjectDisposedException) { }
        catch { lock (_gate) { _cleanup++; _blocked = true; } }
    }
    private async Task<AtkReplyObservation> QueryAsync(IAtkHidConnection connection, AtkReadCommand command, CancellationToken token)
    {
        // Cancellation and the possible-send marker share a gate: cancellation cannot slip between them.
        lock (_gate) { Check(token); _mayHaveSent = true; _requests++; }
        AtkReplyObservation reply;
        try { reply = await connection.QueryAsync(command, token).ConfigureAwait(false); }
        finally { lock (_gate) _requestTerminals++; }
        Check(token);
        int minimum = command == AtkReadCommand.WirelessOnline ? 1 : 2;
        if (reply.Command != command || reply.RawStatus != 0 || !reply.MatchesRequestChecksumConvention
            || reply.DeclaredDataLength < minimum || reply.DeclaredDataLength > 10)
            throw new AtkInvalidReplyException(AtkReplyError.DataLength);
        return reply;
    }
    private async Task RunAsync(CancellationTokenSource cancel, TaskCompletionSource<AtkBatchResult> done)
    {
        IAtkHidConnection? connection = null;
        AtkBatchResult result = new(AtkBatchOutcome.TransientFailure);
        CancellationToken token = cancel.Token;
        try
        {
            Check(token);
            var scan = await transport.ScanAsync(token).ConfigureAwait(false); Check(token);
            if (scan.InspectionErrors != 0) { result = new(AtkBatchOutcome.TransientFailure); return; }
            var targets = scan.Endpoints.Where(e => AtkHidPolicy.Allows(e.Descriptor) && e.Descriptor.FeatureBytes == 0).ToArray();
            if (targets.Length != 1)
            { result = new(targets.Length == 0 ? AtkBatchOutcome.NoInterface : AtkBatchOutcome.AmbiguousInterfaces); return; }
            connection = await transport.OpenAsync(targets[0], token).ConfigureAwait(false); Check(token);
            var online = await QueryAsync(connection, AtkReadCommand.WirelessOnline, token).ConfigureAwait(false);
            if (online.OnlineCandidate is null) { result = new(AtkBatchOutcome.InvalidReply); return; }
            if (online.OnlineCandidate == false) { result = new(AtkBatchOutcome.Offline); return; }
            var model = await QueryAsync(connection, AtkReadCommand.MouseIdentity, token).ConfigureAwait(false);
            if (model.DuckbillUltraCandidate != true) { result = new(AtkBatchOutcome.DifferentModel); return; }
            var battery = await QueryAsync(connection, AtkReadCommand.Battery, token).ConfigureAwait(false);
            result = battery.PercentCandidate is >= 0 and <= 100
                ? new(AtkBatchOutcome.Success, battery.PercentCandidate, _time.GetTimestamp(), _time.GetUtcNow())
                : new(AtkBatchOutcome.InvalidReply);
        }
        catch (OperationCanceledException)
        { lock (_gate) { if (!_invalidated) Invalidate(AtkBatchOutcome.Cancelled); result = new(_outcome); } }
        catch (AtkInvalidReplyException) { result = new(AtkBatchOutcome.InvalidReply); }
        catch (Exception error) when (error is UnauthorizedAccessException or Win32Exception or IOException or InvalidOperationException)
        { lock (_gate) result = new(_mayHaveSent ? AtkBatchOutcome.QueryFailed : AtkBatchOutcome.TransientFailure); }
        catch (Exception) { lock (_gate) result = new(_mayHaveSent ? AtkBatchOutcome.QueryFailed : AtkBatchOutcome.TransientFailure); }
        finally
        {
            if (connection is not null)
            {
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                catch { lock (_gate) { _cleanup++; _blocked = true; result = new(AtkBatchOutcome.CleanupFailed); } }
            }
            lock (_gate)
            {
                if (!_invalidated && _time.GetElapsedTime(_started) >= Budget) Invalidate(AtkBatchOutcome.TimedOut);
                if (_invalidated) result = new(_outcome);
                _blocked |= result.Outcome is AtkBatchOutcome.AmbiguousInterfaces or AtkBatchOutcome.DifferentModel
                    or AtkBatchOutcome.InvalidReply or AtkBatchOutcome.QueryFailed or AtkBatchOutcome.CleanupFailed;
                _outcome = result.Outcome; _terminals++;
                _active = null; _cancellation = null; cancel.Dispose(); done.TrySetResult(result);
            }
        }
    }
    public Task<ShutdownResult> StopAsync()
    {
        TaskCompletionSource<ShutdownResult> done; Task? active;
        lock (_gate)
        {
            if (_stop is not null) return _stop;
            _closed = true; active = _active;
            done = new(TaskCreationOptions.RunContinuationsAsynchronously); _stop = done.Task;
        }
        Cancel(); _ = FinishStopAsync(active, done); return done.Task;
    }
    private async Task FinishStopAsync(Task? active, TaskCompletionSource<ShutdownResult> done)
    {
        if (active is not null) try { await active.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
        lock (_gate) done.TrySetResult(new(_active is null && _cleanup == 0, _active is null ? 0 : 1, _cleanup));
    }
}
