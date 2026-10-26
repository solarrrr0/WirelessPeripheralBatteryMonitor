using System.Diagnostics;
using System.Text.Json;
using PeripheralBatteryTray.App;
using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

internal static class AtkAutoCases
{
    internal static void Run(Action<string, string, Action> run, Action<bool> check)
    {
        run("AA01", "ATK configuration migrates with permission off and applies atomically in isolated modes", () =>
        {
            string path = Path.GetFullPath("atk-test-config.json"); var files = new Files();
            var store = new PollingSettingsStore(path, files);
            check(!store.Current.AtkAutoEnabled && store.Current.AtkMinutes == 5);
            files.Data[path] = """{"schemaVersion":1,"pollIntervalMinutes":7,"future":42,"atk":{"autoRefreshEnabled":true}}""";
            store = new(path, files); check(!store.Current.AtkAutoEnabled && store.Current.Minutes == 7 && files.Writes == 0);
            check(store.TrySave(8, true, 10));
            var loaded = new PollingSettingsStore(path, files);
            check(loaded.Current is { Minutes: 8, AtkAutoEnabled: true, AtkMinutes: 10 }
                && JsonDocument.Parse(files.Data[path]).RootElement.GetProperty("future").GetInt32() == 42);
            files.Fail = true; string before = files.Data[path];
            check(!loaded.TrySave(2, false, 30) && loaded.Current.AtkAutoEnabled && files.Data[path] == before);
            files.Fail = false;
            foreach (string text in new[] {
                """{"schemaVersion":5,"pollIntervalMinutes":1}""",
                """{"schemaVersion":2,"pollIntervalMinutes":5,"atk":{"autoRefreshEnabled":true,"autoRefreshEnabled":false,"pollIntervalMinutes":5}}""",
                """{"schemaVersion":2,"pollIntervalMinutes":5,"atk":{"autoRefreshEnabled":true,"pollIntervalMinutes":4}}""",
                """{"schemaVersion":2,"pollIntervalMinutes":5,"atk":{"autoRefreshEnabled":"true","pollIntervalMinutes":5}}""",
                """{"schemaVersion":2,"pollIntervalMinutes":5,"atk":{}}""", "broken" })
            { files.Data[path] = text; var invalid = new PollingSettingsStore(path, files); check(!invalid.Current.CanSave && !invalid.Current.AtkAutoEnabled && !invalid.TrySave(5) && files.Data[path] == text); }
            files.Data[path] = """{"schemaVersion":2,"pollIntervalMinutes":5,"atk":{"autoRefreshEnabled":false,"pollIntervalMinutes":5,"future":9}}""";
            store = new(path, files); check(store.TrySave(5, true, 6)
                && JsonDocument.Parse(files.Data[path]).RootElement.GetProperty("atk").GetProperty("future").GetInt32() == 9);
            foreach (var args in new string[][] { ["--mock"], ["--self-test"], ["--verify-windows-host"], ["--verify-gatt-host"], ["--verify-atk-host"], ["--measurement-report", "x"] })
                check(!StartupOptions.Parse(args)!.AllowsAtkTray);
            check(StartupOptions.Parse([])!.AllowsAtkTray);
            var failedStore = new PollingSettingsStore(Path.GetFullPath("failed-settings.json"), new Files { Fail = true });
            int applied = 0;
            using (var form = new SettingsForm(failedStore, BatteryMode.Real, _ => applied++, true))
            {
                form.Show(); form.AtkEnabled.Checked = true; form.ApplyButton.PerformClick();
                check(applied == 0 && !failedStore.Current.AtkAutoEnabled); form.Close();
            }
            var fake = new Transport(); var time = new Clock(); var session = new AtkTraySession(fake, time);
            session.AttachDispatcher(a => a()); time.Advance(3600); check(fake.Scans == 0);
            session.StopAsync().GetAwaiter().GetResult();
        });
        run("AA02", "Single timer coalesces triggers and preserves interval across rescheduling and stale callbacks", () =>
        {
            var clock = new Clock(); var queue = new Queue<Action>(); var fake = new Transport();
            var session = new AtkTraySession(fake, clock); session.AttachDispatcher(queue.Enqueue);
            session.Configure(true, 5); clock.Advance(9); Drain(queue); check(fake.Scans == 0);
            clock.Advance(1); Drain(queue); check(fake.Scans == 1 && session.Reading is { Percent: 95, Automatic: true });
            check(!session.TryRead()); clock.Advance(10); check(session.TryRead() && fake.Scans == 2);
            clock.Advance(299); Drain(queue); check(fake.Scans == 2);
            clock.Advance(1); // Due callback is queued; a settings change invalidates it.
            session.Configure(false, 5); Drain(queue); check(fake.Scans == 2 && !session.AutoEnabled);
            session.Configure(true, 5); clock.Advance(10); Drain(queue); check(fake.Scans == 3);
            session.Configure(true, 10); clock.Advance(300); Drain(queue);
            check(fake.Scans == 3 && session.Reading is { Percent: null, LastPercent: 95 });
            clock.Advance(300); Drain(queue); check(fake.Scans == 4);
            session.Configure(false, 10); int timers = clock.Created; clock.Advance(3600); Drain(queue);
            check(fake.Scans == 4 && clock.Created <= timers + 1);
            session.StopAsync().GetAwaiter().GetResult();
        });
        run("AA03", "Production batch rejects before later commands and shares deadline through scan and cleanup", () =>
        {
            foreach (var bad in new[] { AtkReadCommand.WirelessOnline, AtkReadCommand.MouseIdentity, AtkReadCommand.Battery })
            foreach (bool checksumOnly in new[] { false, true })
            {
                var fake = new Transport { InvalidCommand = bad, BadChecksumOnly = checksumOnly }; var batch = new AtkBatterySession(fake);
                check(batch.TryStart()); var result = batch.ActiveTask.GetAwaiter().GetResult();
                check(result.Outcome == AtkBatchOutcome.InvalidReply && batch.State.Blocked
                    && fake.Commands.Count == (bad == AtkReadCommand.WirelessOnline ? 1 : bad == AtkReadCommand.MouseIdentity ? 2 : 3));
                check(!batch.TryStart()); batch.StopAsync().GetAwaiter().GetResult();
            }
            foreach (var change in new Action<Transport>[] { f => f.Feature = 8, f => f.Targets = 2, f => f.Model = false })
            {
                var fake = new Transport(); change(fake); var batch = new AtkBatterySession(fake); check(batch.TryStart());
                check(batch.ActiveTask.GetAwaiter().GetResult().Percent is null && fake.Commands.Count <= 2);
                batch.StopAsync().GetAwaiter().GetResult();
            }
            var zero = new Transport { Percent = 0 }; var valid = new AtkBatterySession(zero); check(valid.TryStart());
            check(valid.ActiveTask.GetAwaiter().GetResult().Percent == 0 && zero.Commands.Count == 3); valid.StopAsync().GetAwaiter().GetResult();
            var clock = new Clock(); var slow = new Transport { OnScan = () => clock.Advance(6), OnQuery = _ => clock.Advance(4) };
            var expired = new AtkBatterySession(slow, clock); check(expired.TryStart());
            check(expired.ActiveTask.GetAwaiter().GetResult().Outcome == AtkBatchOutcome.TimedOut && slow.Commands.Count == 1 && expired.State.Blocked);
            expired.StopAsync().GetAwaiter().GetResult();
            clock = new(); slow = new() { OnDispose = () => clock.Advance(10) }; expired = new(slow, clock); check(expired.TryStart());
            check(expired.ActiveTask.GetAwaiter().GetResult().Percent is null && expired.State.Blocked); expired.StopAsync().GetAwaiter().GetResult();
        });
        run("AA04", "Recoverable backoff saturates, while possibly sent timeout and cleanup failures cannot reopen", () =>
        {
            var clock = new Clock(); var queue = new Queue<Action>(); var fake = new Transport { Online = false };
            var session = new AtkTraySession(fake, clock); session.AttachDispatcher(queue.Enqueue); session.Configure(true, 5);
            clock.Advance(10); Drain(queue);
            int count = 1;
            foreach (int minutes in new[] { 5, 10, 20, 30, 30 })
            {
                clock.Advance(minutes * 60 - 1); Drain(queue); check(fake.Scans == count);
                clock.Advance(1); Drain(queue); check(fake.Scans == ++count && session.State == AtkRefreshState.Backoff);
            }
            fake.Online = true; clock.Advance(10); check(session.TryRead() && session.Reading.Percent == 95);
            clock.Advance(300); Drain(queue); check(fake.Scans == ++count + 1 && session.State == AtkRefreshState.Scheduled);
            session.StopAsync().GetAwaiter().GetResult();
            clock = new(); queue = new(); fake = new() { DenyOpen = true }; session = new(fake, clock); session.AttachDispatcher(queue.Enqueue); session.Configure(true, 5);
            clock.Advance(10); Drain(queue); check(session.State == AtkRefreshState.Backoff && fake.Commands.Count == 0);
            fake.DenyOpen = false; clock.Advance(300); Drain(queue); check(session.Reading.Percent == 95); session.StopAsync().GetAwaiter().GetResult();
            clock = new(); queue = new(); var pending = new TaskCompletionSource<AtkReplyObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
            fake = new() { PendingQuery = pending }; session = new(fake, clock); session.AttachDispatcher(queue.Enqueue); session.Configure(true, 5);
            clock.Advance(10); Drain(queue); check(fake.Commands.Count == 1 && !session.TryRead());
            clock.Advance(10); Drain(queue); check(session.State == AtkRefreshState.Draining && session.Reading.Percent is null);
            session.Configure(false, 5); session.Configure(true, 5); clock.Advance(3600); Drain(queue); check(fake.Scans == 1);
            pending.SetResult(Reply(AtkReadCommand.WirelessOnline)); Pump(() => session.ActiveTask.IsCompleted);
            check(session.State == AtkRefreshState.Blocked && !session.TryRead() && fake.Commands.Count == 1);
            check(session.StopAsync().GetAwaiter().GetResult().FullyStopped);
            fake = new() { FailDispose = true }; session = new(fake); check(session.TryRead() && session.State == AtkRefreshState.Blocked);
            check(!session.StopAsync().GetAwaiter().GetResult().FullyStopped);
        });
        run("AA05", "Saved settings drive actual tray controls, history and minimum without ordinary-refresh HID", () =>
        {
            string prefix = @"Local\PeripheralBatteryTray.Tests." + Guid.NewGuid().ToString("N") + ".";
            using var instance = new SingleInstance(AppMode.Real, prefix);
            var clock = new Clock(); var fake = new Transport(); var atk = new AtkTraySession(fake, clock); var store = new PollingSettingsStore();
            var coordinator = new BatteryCoordinator(BatteryMode.Real, []);
            using var context = new TrayApplicationContext(instance, coordinator, settingsStore: store, atk: atk);
            using var form = new SettingsForm(store, BatteryMode.Real, context.ApplySettings, true);
            form.Show(); form.AtkEnabled.Checked = true; form.AtkInterval.Value = 10;
            check(!atk.AutoEnabled && fake.Scans == 0); form.ApplyButton.PerformClick();
            check(atk.AutoEnabled && atk.IntervalMinutes == 10 && store.Current.AtkAutoEnabled && fake.Scans == 0);
            form.Close(); context.ShowPopup(); Application.DoEvents(); context.RequestRefresh(); Application.DoEvents(); check(fake.Scans == 0);
            clock.Advance(10); Application.DoEvents(); check(atk.Reading.Percent == 95 && context.DisplayedMinimumPercent == 95);
            check(context.AtkAutomaticMenu!.Text?.Contains("开启") == true && context.Popup.DeviceRowCount == 1);
            check(context.CaptureDiagnostics() is { SchemaVersion: 3, Atk.Batches: 1, Atk.RequestsTerminal: 3 });
            context.Popup.Hide(); int renders = context.ListRenderCount;
            clock.Advance(300); Application.DoEvents(); check(context.DisplayedMinimumPercent is null && atk.Reading.LastPercent == 95 && context.ListRenderCount == renders);
            context.ShowPopup(); Application.DoEvents();
            check(Labels(context.Popup).Any(t => t.Contains("95%（上次）")) && Labels(context.Popup).Any(t => t.Contains("自动读取")));
            fake.Online = false; context.RequestAtkRead(); Application.DoEvents();
            check(atk.Reading is { Percent: null, LastPercent: 95, Automatic: true }); // A failed manual attempt cannot relabel an automatic historical reading.
            context.RequestExit(); Pump(() => context.LastExit.IsCompleted); check(context.Shutdown!.FullyStopped);
        });
        run("AA06", "Power, queued callbacks, cancellation and exit preserve occupancy and bounded diagnostic evidence", () =>
        {
            var clock = new Clock(); var queue = new Queue<Action>(); var fake = new Transport();
            var session = new AtkTraySession(fake, clock); session.AttachDispatcher(queue.Enqueue); session.Configure(true, 5);
            clock.Advance(10); session.Suspend(); Drain(queue); check(fake.Scans == 0 && session.State == AtkRefreshState.Suspended);
            session.Resume(); clock.Advance(9); Drain(queue); check(fake.Scans == 0);
            clock.Advance(1); Drain(queue); check(session.Reading.Percent == 95);
            session.Configure(false, 5); check(session.Reading.Percent == 95); session.Suspend(); session.Resume();
            clock.Advance(600); Drain(queue); check(fake.Scans == 1 && session.Reading is { Percent: null, LastPercent: 95 });
            var d = session.CaptureDiagnostics(); check(d.Batches == 1 && d.BatchesTerminal == 1 && d.RequestsStarted == 3 && d.RequestsTerminal == 3);
            var report = DiagnosticExporter.Create(new BatteryCoordinator(BatteryMode.Real, []).Snapshot, clock, TimeSpan.FromMinutes(5), new DiagnosticTelemetry().Capture(), "test", "test", 0, null)
                with { SchemaVersion = 2, Atk = d };
            var bytes = DiagnosticExporter.Serialize(report); string json = System.Text.Encoding.UTF8.GetString(bytes);
            check(bytes.Length <= DiagnosticExporter.MaxBytes && !json.Contains("private-") && JsonDocument.Parse(bytes).RootElement.GetProperty("Atk").GetProperty("Batches").GetInt32() == 1);
            session.Configure(true, 5); clock.Advance(10); session.StopAsync().GetAwaiter().GetResult(); Drain(queue); check(fake.Scans == 1);
            clock = new(); queue = new(); var pending = new TaskCompletionSource<AtkReplyObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
            fake = new() { PendingQuery = pending }; session = new(fake, clock); session.AttachDispatcher(queue.Enqueue); session.Configure(true, 5);
            clock.Advance(10); Drain(queue); session.Configure(false, 5); check(session.State == AtkRefreshState.Draining);
            var watch = Stopwatch.StartNew(); var stopping = session.StopAsync(); Pump(() => stopping.IsCompleted, 3500);
            check(watch.Elapsed < TimeSpan.FromSeconds(3) && stopping.Result is { FullyStopped: false, UnresolvedOperations: 1 });
            pending.SetResult(Reply(AtkReadCommand.WirelessOnline)); Pump(() => session.ActiveTask.IsCompleted);
            check(session.Reading.Percent is null && !session.TryRead() && fake.Commands.Count == 1);
        });
    }
    private static IEnumerable<string> Labels(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x.Text }.Concat(Labels(x)));
    private static void Drain(Queue<Action> actions) { int limit = 100; while (actions.TryDequeue(out var action)) { if (--limit == 0) throw new InvalidOperationException("Timer loop"); action(); } }
    private static void Pump(Func<bool> done, int milliseconds = 2000)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Yield(); }
        if (!done()) throw new TimeoutException("Async test did not finish.");
    }
    private static AtkReplyObservation Reply(AtkReadCommand c, int percent = 95) => new(c, 0, c == AtkReadCommand.WirelessOnline ? (byte)1 : (byte)2,
        true, c == AtkReadCommand.WirelessOnline ? true : null, c == AtkReadCommand.MouseIdentity ? true : null, c == AtkReadCommand.Battery ? percent : null);
    private sealed class Transport : IAtkHidTransport
    {
        internal int Scans, Targets = 1, Percent = 95; internal ushort Feature;
        internal bool Online = true, Model = true, DenyOpen, FailDispose, BadChecksumOnly;
        internal AtkReadCommand? InvalidCommand;
        internal Action? OnScan, OnDispose; internal Action<AtkReadCommand>? OnQuery;
        internal TaskCompletionSource<AtkReplyObservation>? PendingQuery;
        internal List<AtkReadCommand> Commands = [];
        public Task<AtkHidScan> ScanAsync(CancellationToken token)
        { Scans++; OnScan?.Invoke(); return Task.FromResult(new AtkHidScan(Enumerable.Range(0, Targets).Select(_ => new AtkHidEndpoint("private-test-path", new(0x373B, 0x11FD, 1, 0xFF02, 2, 17, 17, Feature, true, true))).ToArray(), 0)); }
        public Task<IAtkHidConnection> OpenAsync(AtkHidEndpoint endpoint, CancellationToken token)
        { if (DenyOpen) throw new UnauthorizedAccessException(); return Task.FromResult<IAtkHidConnection>(new Connection(this)); }
        private sealed class Connection(Transport owner) : IAtkHidConnection
        {
            public Task<AtkReplyObservation> QueryAsync(AtkReadCommand c, CancellationToken token)
            {
                owner.Commands.Add(c); owner.OnQuery?.Invoke(c);
                if (owner.PendingQuery is not null) return owner.PendingQuery.Task;
                var r = Reply(c, owner.Percent) with { OnlineCandidate = c == AtkReadCommand.WirelessOnline ? owner.Online : null,
                    DuckbillUltraCandidate = c == AtkReadCommand.MouseIdentity ? owner.Model : null };
                if (owner.InvalidCommand == c) r = r with { RawStatus = owner.BadChecksumOnly ? (byte)0 : (byte)7,
                    MatchesRequestChecksumConvention = !owner.BadChecksumOnly };
                return Task.FromResult(r);
            }
            public ValueTask DisposeAsync()
            { owner.OnDispose?.Invoke(); if (owner.FailDispose) throw new IOException(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class Files : ISettingsFiles
    {
        internal Dictionary<string, string> Data = []; internal bool Fail; internal int Writes;
        public bool Exists(string p) => Data.ContainsKey(p);
        public string Read(string p) => Data[p];
        public void CreateDirectory(string p) { }
        public void WriteNew(string p, string text, Action created) { Writes++; Data.Add(p, text); created(); }
        public void Replace(string a, string b) { if (Fail) throw new IOException(); Data[b] = Data[a]; Data.Remove(a); }
        public void MoveNew(string a, string b) { if (Fail) throw new IOException(); Data.Add(b, Data[a]); Data.Remove(a); }
        public void DeleteOwn(string p) => Data.Remove(p);
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks; private readonly List<Timer> _timers = []; internal int Created;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(_ticks);
        public override ITimer CreateTimer(TimerCallback cb, object? state, TimeSpan due, TimeSpan period)
        { if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("Periodic timer"); Created++; var t = new Timer(this, cb, state); _timers.Add(t); t.Change(due, period); return t; }
        internal void Advance(int seconds)
        { _ticks += seconds * 1000L; foreach (var timer in _timers.ToArray()) if (timer.Due is long due && due <= _ticks) { timer.Due = null; timer.Fire(); } }
        private sealed class Timer(Clock clock, TimerCallback cb, object? state) : ITimer
        {
            internal long? Due;
            public bool Change(TimeSpan due, TimeSpan period) { Due = due == Timeout.InfiniteTimeSpan ? null : clock._ticks + (long)due.TotalMilliseconds; return true; }
            internal void Fire() => cb(state);
            public void Dispose() { Due = null; clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
