using PeripheralBatteryTray.Core;

internal static class CoordinatorCases
{
    public static void Run(Action<string, string, Action> run, Action<bool> check)
    {
        void Wait(Func<bool> condition) => check(SpinWait.SpinUntil(condition, 2000));
        BatteryDeviceState Device(string id = "one", long epoch = 1) => new(new DeviceBinding {
            DeviceId = id, BindingKey = "opaque-" + id, BindingEpoch = 1,
            Ble = new(ConnectionState.Connected, epoch), GattDatabaseEpoch = 1 }, "Headset", "BLE");
        void Directory(FakeRead op, string key = "a", long sequence = 1) => op.Publish(new DirectoryUpdate(sequence,
            DirectoryScope.ServiceCharacteristics, "service-" + key, [new(key, "service-" + key, 0)], true));
        void Value(FakeRead op, SchedulerTime time, int percent = 73, string key = "a", long sequence = 2,
            ReadOutcome outcome = ReadOutcome.Success) => op.Publish(new ValueUpdate(sequence, key, "service-" + key,
                outcome, outcome == ReadOutcome.Success ? BatteryValue.FromPercent(percent) : null, time.GetUtcNow(), time.GetTimestamp()));
        run("C12", "100 manual refreshes coalesce without trailing read", () =>
        {
            var t = new SchedulerTime(); var p = new FakeProvider(ReadingSource.GattUncached);
            var c = new BatteryCoordinator(BatteryMode.Real, [p], t); c.UpsertDevice(Device(), true);
            for (int i = 0; i < 100; i++) c.Refresh();
            check(p.Reads.Count == 1 && c.ActiveReads.Count == 1);
            Directory(p.Reads[0]); Value(p.Reads[0], t); p.Reads[0].Finish();
            t.Advance(TimeSpan.FromSeconds(10)); check(p.Reads.Count == 1);
            check(c.Snapshot.TrayVisual.MinimumPercent == 73); c.StopAsync().GetAwaiter().GetResult();
        });
        run("C13", "Strict total deadline enforced before update commit", () =>
        {
            var t = new SchedulerTime(); var p = new FakeProvider(ReadingSource.GattUncached);
            var c = new BatteryCoordinator(BatteryMode.Real, [p], t); c.UpsertDevice(Device(), true);
            var op = p.Reads[0]; Directory(op); Directory(op, "b", 2);
            t.Advance(TimeSpan.FromMilliseconds(9999), fire: false); Value(op, t, sequence: 3);
            t.Advance(TimeSpan.FromMilliseconds(1), fire: false); Value(op, t, 10, "b", 4);
            check(c.ActiveReads.Single().Phase == QueryPhase.Draining && op.CancelCount == 1);
            check(c.Snapshot.TrayVisual.MinimumPercent == 73 && c.Snapshot.TrayVisual.HasUnknown);
            check(c.Snapshot.Devices[0].Components.Single(x => x.ComponentKey == "b").Source.LatestOutcome == ReadOutcome.TimedOut);
            op.Finish(); c.StopAsync().GetAwaiter().GetResult();
        });
        run("C14", "Native draining retains slot to original completion", () =>
        {
            var t = new SchedulerTime(); var p = new FakeProvider(ReadingSource.GattUncached);
            var c = new BatteryCoordinator(BatteryMode.Real, [p], t); c.UpsertDevice(Device(), true);
            Directory(p.Reads[0]); t.Advance(TimeSpan.FromSeconds(10)); c.Refresh();
            t.Advance(TimeSpan.FromSeconds(49)); Value(p.Reads[0], t, 1);
            check(p.Reads.Count == 1 && c.Snapshot.TrayVisual.MinimumPercent is null);
            t.Advance(TimeSpan.FromSeconds(1)); p.Reads[0].Finish();
            check(p.Reads.Count == 2 && c.ActiveReads.Count == 1); p.Reads[1].Finish(ReadOutcome.Cancelled);
            c.StopAsync().GetAwaiter().GetResult();
        });
        run("C15", "Never-terminal GATT does not busy-loop or block metadata lane", () =>
        {
            var t = new SchedulerTime(); var g = new FakeProvider(ReadingSource.GattUncached);
            var w = new FakeProvider(ReadingSource.WindowsProperty);
            var c = new BatteryCoordinator(BatteryMode.Real, [g, w], t); c.UpsertDevice(Device(), true);
            w.Reads[0].Finish(); t.Advance(TimeSpan.FromSeconds(10)); int timers = t.Callbacks;
            c.Refresh(); t.Advance(TimeSpan.FromHours(2));
            check(g.Reads.Count == 1 && c.ActiveReads.Count(x => x.Stamp.Source == ReadingSource.GattUncached) == 1);
            check(t.Callbacks - timers <= 2 && w.Reads.Count == 2);
            w.Reads[1].Finish(); var stop = c.StopAsync(); t.Advance(TimeSpan.FromSeconds(2));
            Wait(() => stop.IsCompleted); check(!stop.Result.FullyStopped); g.Reads[0].Finish();
        });
        run("C16", "Windows source requests cannot invalidate GATT", () =>
        {
            var t = new SchedulerTime(); var g = new FakeProvider(ReadingSource.GattUncached); var w = new FakeProvider(ReadingSource.WindowsProperty);
            var c = new BatteryCoordinator(BatteryMode.Real, [g, w], t); c.UpsertDevice(Device(), true);
            w.Reads[0].Publish(new ValueUpdate(1, "node", null, ReadOutcome.Success, BatteryValue.FromPercent(90), t.GetUtcNow(), t.GetTimestamp()));
            w.Reads[0].Finish(); c.Refresh(reason: RefreshReason.Property); t.Advance(TimeSpan.FromSeconds(1));
            Directory(g.Reads[0]); Value(g.Reads[0], t);
            check(c.Snapshot.TrayVisual.MinimumPercent == 73 && c.Snapshot.Devices[0].SystemCandidates[0].LastSuccess!.Value.Percent == 90);
            check(g.Reads.Count == 1 && w.Reads.Count == 2); g.Reads[0].Finish(); w.Reads[1].Finish(); c.StopAsync().GetAwaiter().GetResult();
        });
        run("C17", "New connection intent waits for old native operation", () =>
        {
            var t = new SchedulerTime(); var p = new FakeProvider(ReadingSource.GattUncached);
            var c = new BatteryCoordinator(BatteryMode.Real, [p], t); c.UpsertDevice(Device(), true);
            c.UpsertDevice(Device(epoch: 2), true); for (int i = 0; i < 100; i++) c.Refresh();
            t.Advance(TimeSpan.FromSeconds(20)); check(p.Reads.Count == 1);
            Directory(p.Reads[0]); Value(p.Reads[0], t, 1); p.Reads[0].Finish();
            check(p.Reads.Count == 2 && p.Reads[1].Request.Stamp.TransportEpoch == 2);
            Directory(p.Reads[1]); Value(p.Reads[1], t, 80); p.Reads[1].Finish();
            check(c.Snapshot.TrayVisual.MinimumPercent == 80); c.StopAsync().GetAwaiter().GetResult();
        });
        run("C18", "First-event debounce and missed periods coalesce", () =>
        {
            var t = new SchedulerTime(); var p = new FakeProvider(ReadingSource.WindowsProperty);
            var c = new BatteryCoordinator(BatteryMode.Real, [p], t); c.UpsertDevice(Device()); p.Reads[0].Finish();
            c.Refresh(reason: RefreshReason.Property);
            for (int i = 0; i < 9; i++) { t.Advance(TimeSpan.FromMilliseconds(100)); c.Refresh(reason: RefreshReason.Property); }
            check(p.Reads.Count == 1); t.Advance(TimeSpan.FromMilliseconds(100)); check(p.Reads.Count == 2);
            p.Reads[1].Publish(new ValueUpdate(1, "node", null, ReadOutcome.Success, BatteryValue.FromPercent(90), t.GetUtcNow(), t.GetTimestamp()));
            p.Reads[1].Finish(); t.Advance(TimeSpan.FromHours(1)); check(p.Reads.Count == 3);
            p.Reads[2].Finish(); c.StopAsync().GetAwaiter().GetResult();
        });
        run("C19", "Property notifications and provider observations do not trigger GATT", () =>
        {
            var t = new SchedulerTime(); var g = new FakeProvider(ReadingSource.GattUncached); var w = new FakeProvider(ReadingSource.WindowsProperty);
            var c = new BatteryCoordinator(BatteryMode.Real, [g, w], t); c.UpsertDevice(Device(), true);
            Directory(g.Reads[0]); Value(g.Reads[0], t); g.Reads[0].Finish(); w.Reads[0].Finish();
            for (int i = 0; i < 100; i++) c.Refresh(reason: RefreshReason.Property);
            t.Advance(TimeSpan.FromSeconds(1)); check(w.Reads.Count == 2 && g.Reads.Count == 1);
            w.Reads[1].Finish(); t.Advance(TimeSpan.FromSeconds(1)); check(w.Reads.Count == 2 && g.Reads.Count == 1);
            c.StopAsync().GetAwaiter().GetResult();
        });
        run("C20", "Per-service retry failures counted once and success resets own path", () =>
        {
            var t = new SchedulerTime(); var policy = new RefreshPolicy();
            int[] expected = [1, 2, 4, 8, 16, 30, 30];
            foreach (int minutes in expected) { policy.Complete(ReadOutcome.Error, t.GetTimestamp(), TimeSpan.FromMinutes(1), t);
                check(t.GetElapsedTime(t.GetTimestamp(), policy.NextTimestamp!.Value) == TimeSpan.FromMinutes(minutes)); }
            policy.Complete(ReadOutcome.Cancelled, t.GetTimestamp(), TimeSpan.FromMinutes(1), t); check(policy.ConsecutiveFailures == 7);
            policy.Complete(ReadOutcome.Success, t.GetTimestamp(), TimeSpan.FromMinutes(1), t); check(policy.ConsecutiveFailures == 0);
            var p = new FakeProvider(ReadingSource.GattUncached); var c = new BatteryCoordinator(BatteryMode.Real, [p], t, 1);
            c.UpsertDevice(Device(), true); var op = p.Reads[0]; Directory(op); Directory(op, "b", 2);
            Value(op, t, sequence: 3); Value(op, t, key: "b", sequence: 4, outcome: ReadOutcome.Error);
            Value(op, t, key: "b", sequence: 5, outcome: ReadOutcome.Error); op.Finish();
            t.Advance(TimeSpan.FromMinutes(1)); check(p.Reads.Count == 2 && p.Reads[1].Request.DueServiceKeys.Count == 2);
            op = p.Reads[1]; Directory(op); Directory(op, "b", 2); Value(op, t, sequence: 3);
            Value(op, t, key: "b", sequence: 4, outcome: ReadOutcome.Error); op.Finish();
            t.Advance(TimeSpan.FromMinutes(1)); check(p.Reads.Count == 3 && p.Reads[2].Request.DueServiceKeys.SequenceEqual(["service-a"]));
            Directory(p.Reads[2]); Value(p.Reads[2], t); p.Reads[2].Finish();
            check(c.Snapshot.TrayVisual.MinimumPercent == 73); c.StopAsync().GetAwaiter().GetResult();
        });
        run("C21", "Permission pause allows one throttled manual recheck", () =>
        {
            var t = new SchedulerTime(); var p = new FakeProvider(ReadingSource.GattUncached);
            var c = new BatteryCoordinator(BatteryMode.Real, [p], t); c.UpsertDevice(Device(), true);
            p.Reads[0].Finish(ReadOutcome.AccessDenied); c.Refresh(reason: RefreshReason.Periodic);
            t.Advance(TimeSpan.FromHours(1)); check(p.Reads.Count == 1 && t.Callbacks == 0);
            c.Refresh(); for (int i = 0; i < 100; i++) c.Refresh(); check(p.Reads.Count == 2);
            p.Reads[1].Finish(ReadOutcome.Unsupported); t.Advance(TimeSpan.FromHours(1)); check(p.Reads.Count == 2);
            c.StopAsync().GetAwaiter().GetResult();
        });
        run("C22", "UTC jumps do not affect monotonic deadline", () =>
        {
            var t = new SchedulerTime(); var p = new FakeProvider(ReadingSource.GattUncached);
            var c = new BatteryCoordinator(BatteryMode.Real, [p], t); c.UpsertDevice(Device(), true);
            t.JumpUtc(TimeSpan.FromHours(1)); Directory(p.Reads[0]); Value(p.Reads[0], t);
            t.JumpUtc(TimeSpan.FromHours(-2)); t.Advance(TimeSpan.FromSeconds(10));
            check(c.ActiveReads.Single().Phase == QueryPhase.Draining && c.Snapshot.TrayVisual.MinimumPercent == 73);
            p.Reads[0].Finish(); c.StopAsync().GetAwaiter().GetResult();
        });
        run("C23", "Period changes expire values and resume requires new evidence", () =>
        {
            var t = new SchedulerTime(); var p = new FakeProvider(ReadingSource.GattUncached);
            var c = new BatteryCoordinator(BatteryMode.Real, [p], t); c.UpsertDevice(Device(), true);
            Directory(p.Reads[0]); Value(p.Reads[0], t); p.Reads[0].Finish(); t.Advance(TimeSpan.FromMinutes(2));
            c.SetRefreshMinutes(1); check(c.Snapshot.TrayVisual.MinimumPercent is null);
            c.Suspend(); c.Resume(); c.Refresh(); t.Advance(TimeSpan.FromHours(1)); check(p.Reads.Count == 1);
            c.UpsertDevice(Device(epoch: 3), true); check(p.Reads.Count == 2 && c.Snapshot.TrayVisual.MinimumPercent is null);
            p.Reads[1].Finish(ReadOutcome.Cancelled); c.StopAsync().GetAwaiter().GetResult();
        });
        run("C24", "Old timers are inert and due devices receive fair access", () =>
        {
            var t = new SchedulerTime(); var p = new FakeProvider(ReadingSource.GattUncached);
            var c = new BatteryCoordinator(BatteryMode.Real, [p], t); c.UpsertDevice(Device("one"), true);
            Action old = t.CaptureLast(); c.UpsertDevice(Device("two"), true); c.UpsertDevice(Device("three"), true);
            Directory(p.Reads[0]); Value(p.Reads[0], t); p.Reads[0].Finish();
            old(); check(p.Reads.Count == 1); t.Advance(TimeSpan.FromSeconds(10)); check(p.Reads[1].Request.Stamp.DeviceId == "two");
            p.Reads[1].Finish(ReadOutcome.Cancelled); t.Advance(TimeSpan.FromSeconds(10)); check(p.Reads[2].Request.Stamp.DeviceId == "three");
            p.Reads[2].Finish(ReadOutcome.Cancelled); c.StopAsync().GetAwaiter().GetResult();
        });
        run("C26", "Create failure, synchronous completion and pre-start cancellation", () =>
        {
            var t = new SchedulerTime(); var fail = new FakeProvider(ReadingSource.GattUncached) { ThrowOnCreate = true };
            var c = new BatteryCoordinator(BatteryMode.Real, [fail], t); c.UpsertDevice(Device(), true);
            check(c.ActiveReads.Count == 0); c.StopAsync().GetAwaiter().GetResult();
            using var mock = new MockBatteryProvider(t); var m = new BatteryCoordinator(BatteryMode.Mock, [mock], t);
            int timerCount = t.CreatedTimers;
            foreach (var device in mock.ReadModelsAsync(default).Result) m.UpsertDevice(device);
            check(m.ActiveReads.Count == 0 && m.Snapshot.TrayVisual.MinimumPercent == 73);
            for (int i = 0; i < 100; i++) m.Refresh(); check(m.ActiveReads.Count == 0 && t.CreatedTimers == timerCount);
            m.StopAsync().GetAwaiter().GetResult();
            var p = new FakeProvider(ReadingSource.GattUncached); BatteryCoordinator? race = null;
            p.BeforeReturn = () => { _ = race!.StopAsync(); };
            race = new BatteryCoordinator(BatteryMode.Real, [p], t); race.UpsertDevice(Device(), true);
            check(p.Reads.Single().NativeStarts == 0 && p.Reads.Single().CancelCount == 1);
            check(race.StopAsync().GetAwaiter().GetResult().FullyStopped);
        });
        run("C27", "Idempotent shutdown has one shared two-second budget", () =>
        {
            var t = new SchedulerTime(); var p = new FakeProvider(ReadingSource.GattUncached) { HoldDisposal = true };
            var c = new BatteryCoordinator(BatteryMode.Real, [p], t); c.UpsertDevice(Device(), true);
            Task<ShutdownResult> stop = c.StopAsync(); check(ReferenceEquals(stop, c.StopAsync()));
            t.Advance(TimeSpan.FromMilliseconds(1999)); check(!stop.IsCompleted);
            t.Advance(TimeSpan.FromMilliseconds(1)); Wait(() => stop.IsCompleted);
            check(!stop.Result.FullyStopped && stop.Result.UnresolvedOperations >= 1);
            p.Reads[0].Finish(); p.DisposeCompletion.TrySetResult();
            var clean = new BatteryCoordinator(BatteryMode.Real, [], t);
            check(clean.StopAsync().GetAwaiter().GetResult().FullyStopped);
        });
        run("C28", "Shutdown rejects late state and observes faulted native terminal", () =>
        {
            var t = new SchedulerTime(); var p = new FakeProvider(ReadingSource.GattUncached);
            var c = new BatteryCoordinator(BatteryMode.Real, [p], t); int callbacks = 0;
            c.SnapshotChanged += _ => callbacks++; c.UpsertDevice(Device(), true); int before = callbacks;
            var stop = c.StopAsync(); c.Refresh(); c.UpsertDevice(Device("two"), true);
            Directory(p.Reads[0]); Value(p.Reads[0], t); p.Reads[0].Fault();
            Wait(() => stop.IsCompleted); check(callbacks == before && p.Reads.Count == 1 && c.ActiveReads.Count == 0);
            check(stop.Result.FullyStopped);
        });
        run("C29", "Snapshot mailbox coalesces latest version and rejects post-disposal work", () =>
        {
            var t = new SchedulerTime(); var queued = new Queue<Action>(); var displayed = new List<long>();
            using var mailbox = new SnapshotMailbox(action => queued.Enqueue(action), snapshot => displayed.Add(snapshot.Version));
            BatterySnapshot Snap(long version) => new(version, t.GetUtcNow(), BatteryMode.Real, [], t, TimeSpan.FromMinutes(5));
            mailbox.Publish(Snap(10)); mailbox.Publish(Snap(11)); mailbox.Publish(Snap(12));
            check(queued.Count == 1 && displayed.Count == 0); queued.Dequeue()();
            check(displayed.SequenceEqual([12L]) && mailbox.DisplayedVersion == 12);
            mailbox.Publish(Snap(11)); check(queued.Count == 0);
            mailbox.Publish(Snap(13)); mailbox.Dispose(); queued.Dequeue()(); mailbox.Publish(Snap(14));
            check(queued.Count == 0 && displayed.SequenceEqual([12L]));
        });
    }
}

internal sealed class FakeProvider(ReadingSource source) : IBatteryProvider
{
    public ReadingSource Source => source;
    public List<FakeRead> Reads { get; } = [];
    public bool ThrowOnCreate, HoldDisposal;
    public Action? BeforeReturn;
    public TaskCompletionSource DisposeCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IBatteryReadOperation CreateRead(QueryRequest request, CancellationToken lifetime)
    {
        if (ThrowOnCreate) throw new InvalidOperationException("Test creation failure.");
        var read = new FakeRead(request); Reads.Add(read); BeforeReturn?.Invoke(); return read;
    }
    public ValueTask DisposeAsync() => HoldDisposal ? new(DisposeCompletion.Task) : ValueTask.CompletedTask;
}
internal sealed class FakeRead(QueryRequest request) : IBatteryReadOperation
{
    private Action<ProviderUpdate>? _publish;
    private readonly TaskCompletionSource<ReadCompletion> _completion = new();
    public QueryRequest Request => request;
    public Task<ReadCompletion> Completion => _completion.Task;
    public int CancelCount, NativeStarts;
    public void Start(Action<ProviderUpdate> publish)
    {
        _publish = publish;
        if (CancelCount > 0) Finish(ReadOutcome.Cancelled); else NativeStarts++;
    }
    public void Publish(ProviderUpdate update) => _publish?.Invoke(update);
    public void RequestCancel() => Interlocked.Increment(ref CancelCount);
    public void Finish(ReadOutcome outcome = ReadOutcome.Success) => _completion.TrySetResult(new(outcome));
    public void Fault() => _completion.TrySetException(new IOException("Fake native failure."));
}
internal sealed class SchedulerTime : TimeProvider
{
    private long _now = 100_000;
    private DateTimeOffset _utc = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
    private readonly List<ManualTimer> _timers = [];
    public int Callbacks, CreatedTimers;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => _now;
    public override DateTimeOffset GetUtcNow() => _utc;
    public void JumpUtc(TimeSpan delta) => _utc += delta;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state); _timers.Add(timer); CreatedTimers++;
        timer.Change(dueTime, period); return timer;
    }
    public Action CaptureLast() { var timer = _timers[^1]; return () => timer.Callback(timer.State); }
    public void Advance(TimeSpan duration, bool fire = true)
    {
        _now += (long)duration.TotalMilliseconds; _utc += duration;
        if (!fire) return;
        int guard = 0;
        while (_timers.FirstOrDefault(t => !t.Disposed && t.Due <= _now) is { } timer)
        {
            if (++guard > 100) throw new InvalidOperationException("Timer busy loop.");
            timer.Due = long.MaxValue; Callbacks++; timer.Callback(timer.State);
        }
    }
    private sealed class ManualTimer(SchedulerTime owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback = callback;
        public object? State = state;
        public long Due = long.MaxValue;
        public bool Disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (Disposed) return false;
            if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("Expected one-shot timer.");
            Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : RefreshPolicy.Add(owner._now, dueTime, owner); return true;
        }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
