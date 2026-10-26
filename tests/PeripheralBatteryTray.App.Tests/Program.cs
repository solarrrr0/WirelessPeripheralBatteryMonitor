using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PeripheralBatteryTray.App;
using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

internal static class Program
{
    private static readonly List<object> Checks = [];
    private static int _assertions, _failures;
    private static void Check(bool condition) { _assertions++; if (!condition) throw new InvalidOperationException($"Assertion {_assertions} failed."); }
    private static void Run(string id, string name, Action action)
    {
        int before = _assertions;
        try { action(); Checks.Add(new { Id = id, Name = name, Passed = true, Assertions = _assertions - before }); Console.WriteLine($"PASS {id} {name}"); }
        catch (Exception e) { _failures++; Checks.Add(new { Id = id, Name = name, Passed = false, Error = e.ToString() }); Console.WriteLine($"FAIL {id} {e}"); }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--software-soak") return SoftwareSoak.Run(args);
        if (args.Length > 0 && args[0] == "--allocation-benchmark") return SoftwareSoak.Benchmark(args);
        Run("P01", "Actual startup parser rejects malformed inputs before composition", () =>
        {
            Check(SoftwareSoak.Run(["--software-soak"]) == 3 && SoftwareSoak.Benchmark(["--allocation-benchmark"]) == 3);
            foreach (string[] input in new string[][] { ["--report"], ["--nope"], ["--mock", "--mock"], ["--report", "x"],
                ["--exit", "--mock"], ["--exit", "--self-test"], ["--exit", "--expected-pid", "1"], ["--expected-pid", "1", "--expected-start-ticks", "2"],
                ["--exit", "--expected-pid", "-1", "--expected-start-ticks", "2"], ["--exit", "--expected-pid", "1", "--expected-start-ticks", "0"],
                ["--mock", "--verify-windows-host"], ["--self-test", "--verify-windows-host"], ["--self-test", "--report", "--mock"],
                ["--measurement-report"], ["--measurement-report", "x", "--self-test"], ["--measurement-report", "x", "--verify-windows-host"],
                ["--measurement-report", "x", "--exit"], ["--verify-gatt-host", "--mock"], ["--verify-gatt-host", "--self-test"],
                ["--verify-gatt-host", "--verify-windows-host"], ["--verify-gatt-host", "--measurement-report", "x"],
                ["--verify-gatt-host", "--exit"] })
                Check(StartupOptions.Parse(input) is null);
            Check(StartupOptions.Parse([]) is { Mock: false, Exit: false });
            Check(StartupOptions.Parse(["--self-test", "--mock", "--report", "test.json"]) is { Mock: true, SelfTest: true });
            Check(StartupOptions.Parse(["--exit", "--expected-pid", "1", "--expected-start-ticks", "2"]) is { ExpectedPid: 1, ExpectedStartTicks: 2 });
            Check(StartupOptions.Parse(["--mock", "--measurement-report", "x"]) is { Mock: true, MeasurementReport: "x" });
            Check(StartupOptions.Parse(["--verify-gatt-host", "--report", "gatt.local.json"]) is { GattCheck: true, Mock: false, HostCheck: false });
        });
        Run("P02", "Actual Mock composition never invokes Windows factory or persistent settings path", () =>
        {
            foreach (string[] input in new string[][] { ["--mock"], ["--self-test"] })
            {
                int factories = 0, stores = 0;
                var c = AppComposition.Create(StartupOptions.Parse(input)!, _ => { factories++; throw new InvalidOperationException("forbidden"); },
                    path => { stores++; Check(path is null); return new(); });
                Check(factories == 0 && stores == 1 && c.Runtime is null && c.Audit is null && c.Coordinator.Snapshot.Mode == BatteryMode.Mock);
                Check(c.Coordinator.Snapshot.Devices.Count == 3 && c.Coordinator.Snapshot.Devices.All(d => d.Binding.DeviceId.StartsWith("mock:")));
                Check(c.Settings.TrySave(30)); c.Coordinator.StopAsync().GetAwaiter().GetResult();
            }
        });
        Run("P03", "Mode-specific secondary opens existing instance without replacing marker or event", () =>
        {
            string prefix = @"Local\PeripheralBatteryTray.Tests." + Guid.NewGuid().ToString("N") + ".";
            using var primary = new SingleInstance(AppMode.Mock, prefix);
            using var same = new SingleInstance(AppMode.Mock, prefix);
            using var different = new SingleInstance(AppMode.Real, prefix);
            Check(primary.IsPrimary && !same.IsPrimary && !different.IsPrimary);
            Check(SingleInstance.Probe(prefix) == ExistingInstanceState.Mock && same.ShowEvent is null && different.ExitEvent is null);
            Check(SingleInstance.SignalShow(prefix) && primary.ShowEvent!.WaitOne(0));
            Check(!primary.ExitEvent!.WaitOne(0));
        });
        Run("P04", "Absent/partial/ambiguous/wrong-type markers stay distinct and failed construction rolls back", () =>
        {
            string prefix = @"Local\PeripheralBatteryTray.Tests." + Guid.NewGuid().ToString("N") + ".";
            Check(SingleInstance.Probe(prefix) == ExistingInstanceState.Missing);
            using (var mutex = new Mutex(false, prefix + "Mutex"))
            {
                Check(SingleInstance.Probe(prefix) == ExistingInstanceState.Unknown);
                using var a = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "Mode.Mock");
                using var b = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "Mode.Real");
                Check(SingleInstance.Probe(prefix) == ExistingInstanceState.Unknown);
            }
            using (var wrong = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "Mutex"))
                Check(SingleInstance.Probe(prefix) == ExistingInstanceState.Unknown);
            using (var stale = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "Show"))
            {
                bool rejected = false; try { using var instance = new SingleInstance(AppMode.Real, prefix); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && SingleInstance.Probe(prefix) == ExistingInstanceState.Missing);
            }
            using var recovered = new SingleInstance(AppMode.Real, prefix); Check(recovered.IsPrimary && SingleInstance.Probe(prefix) == ExistingInstanceState.Real);
        });
        Run("P05", "PID/start/path checked exit signals only exact target event, never global exit", () =>
        {
            string prefix = @"Local\PeripheralBatteryTray.Tests." + Guid.NewGuid().ToString("N") + ".";
            using var primary = new SingleInstance(AppMode.Mock, prefix);
            using var process = Process.GetCurrentProcess(); long ticks = process.StartTime.ToUniversalTime().Ticks;
            Check(!SingleInstance.SignalExpectedExit(process.Id, ticks + 1, prefix: prefix));
            Check(!SingleInstance.SignalExpectedExit(process.Id, ticks, Path.Combine(Path.GetTempPath(), "wrong.exe"), prefix));
            Check(!primary.ExactExitEvent!.WaitOne(0) && !primary.ExitEvent!.WaitOne(0));
            Check(SingleInstance.SignalExpectedExit(process.Id, ticks, prefix: prefix));
            Check(primary.ExactExitEvent.WaitOne(0) && !primary.ExitEvent!.WaitOne(0));
            Check(!SingleInstance.SignalExpectedExit(int.MaxValue, ticks, prefix: prefix));
        });
        Run("J01", "Actual hidden window coalesces synthetic power broadcasts", () =>
        {
            int suspends = 0, resumes = 0;
            using var window = new ShellEventWindow(() => { }, () => { }, () => suspends++, () => resumes++);
            window.SimulatePower(18); window.SimulatePower(10);
            Check(suspends == 0 && resumes == 0);
            window.SimulatePower(4); window.SimulatePower(4);
            Check(suspends == 1 && resumes == 0);
            window.SimulatePower(18); window.SimulatePower(7);
            Check(suspends == 1 && resumes == 1);
            window.SimulatePower(4); window.SimulatePower(7);
            Check(suspends == 2 && resumes == 2);
        });
        Run("M01", "Hidden real-mode list defers rendering and opening paints the latest immutable snapshot", () =>
        {
            string prefix = @"Local\PeripheralBatteryTray.Tests." + Guid.NewGuid().ToString("N") + ".";
            using var instance = new SingleInstance(AppMode.Real, prefix);
            var coordinator = new BatteryCoordinator(BatteryMode.Real, []);
            using var context = new TrayApplicationContext(instance, coordinator);
            var binding = new DeviceBinding { DeviceId = "memory-test", BindingKey = "memory-test", BindingEpoch = 1 };
            for (int i = 0; i < 100; i++) { coordinator.UpsertDevice(new(binding, "test-" + i, "BLE")); Application.DoEvents(); }
            Check(!context.Popup.Visible && context.ListRenderCount == 0 && context.Popup.DeviceRowCount == 0);
            Check(!context.Popup.ContentInitialized);
            Check(context.CurrentSnapshot.Devices.Single().DisplayName == "test-99");
            context.ShowPopup(); Application.DoEvents();
            Check(context.Popup.DeviceRowCount == 1 && context.ListRenderCount > 0
                && context.RenderedSnapshotVersion == context.CurrentSnapshot.Version);
            Check(context.Popup.ContentInitialized);
            context.Popup.Hide(); int renders = context.ListRenderCount;
            for (int i = 0; i < 100; i++) { coordinator.UpsertDevice(new(binding, "new-" + i, "BLE")); Application.DoEvents(); }
            Check(context.ListRenderCount == renders && context.CurrentSnapshot.Devices.Single().DisplayName == "new-99");
            coordinator.UpsertDevice(new(binding, "pending-before-open", "BLE"));
            long pendingVersion = coordinator.Snapshot.Version;
            context.ShowPopup();
            Check(context.CurrentSnapshot.Devices.Single().DisplayName == "pending-before-open"
                && context.RenderedSnapshotVersion >= pendingVersion);
            context.Popup.Hide(); Application.DoEvents();
            context.ShowPopup(); Application.DoEvents();
            Check(context.RenderedSnapshotVersion == context.CurrentSnapshot.Version && context.ListRenderCount > renders);
            context.RequestExit();
            Check(SpinWait.SpinUntil(() => context.LastExit.IsCompleted, 3000));
        });
        Run("H01", "Manual GATT rejects offline/incomplete discovery and never auto-starts or overlaps reads", () =>
        {
            var time = new ManualTime(); var registry = new BindingRegistry(); var fake = new ManualProvider();
            bool ready = false;
            var telemetry = new DiagnosticTelemetry(time);
            var session = new ManualGattSession(registry, fake, () => ready, telemetry, time);
            registry.Replace([GattBinding(ConnectionState.Disconnected)]);
            Check(session.State.Attempts == 0 && fake.Created == 0);
            Check(!session.TryRead("manual") && fake.Created == 0);
            ready = true;
            Check(!session.TryRead("manual") && fake.Created == 0);
            registry.Replace([GattBinding(ConnectionState.Connected)]);
            Check(!session.TryRead("missing") && fake.Created == 0);
            Check(session.TryRead("manual") && fake.Created == 1 && session.State.Busy);
            for (int i = 0; i < 100; i++) Check(!session.TryRead("manual"));
            Check(fake.Created == 1 && fake.LastRequest!.Reason == RefreshReason.Manual
                && fake.LastRequest.Stamp.Source == ReadingSource.GattUncached && fake.LastRequest.Budget == ManualGattSession.Budget);
            fake.Operation!.Publish!(new ValueUpdate(1, "private-device-path", "private-service", ReadOutcome.Success,
                BatteryValue.FromPercent(63), time.GetUtcNow(), time.GetTimestamp()));
            fake.Operation.Finish();
            Check(SpinWait.SpinUntil(() => !session.State.Busy, 1000));
            Check(session.State.Values.Single().Percent == 63 && !JsonSerializer.Serialize(session.State).Contains("private-"));
            Check(!session.TryRead("manual") && session.State.RetryAfterSeconds > 0);
            time.Advance(10);
            Check(session.TryRead("manual") && fake.Created == 2 && session.State.Values.Count == 0);
            fake.Operation!.Publish!(new ValueUpdate(1, "invalid", null, ReadOutcome.Error, null, time.GetUtcNow(), time.GetTimestamp()));
            fake.Operation!.Finish();
            Check(session.StopAsync().GetAwaiter().GetResult().FullyStopped);
            Check(session.State.Outcome == ReadOutcome.Error);
            time.Advance(10); Check(!session.TryRead("manual"));
        });
        Run("H02", "Manual GATT timeout keeps native occupancy, suppresses late values, and combines exit cleanup", () =>
        {
            var time = new ManualTime(); var registry = new BindingRegistry(); var fake = new ManualProvider();
            registry.Replace([GattBinding(ConnectionState.Connected)]);
            var session = new ManualGattSession(registry, fake, () => true, new(time), time);
            Check(session.TryRead("manual")); time.Advance(15); session.Tick();
            Check(session.State.TimedOut && session.State.Busy && fake.Operation!.Cancelled);
            fake.Operation!.Publish!(new ValueUpdate(1, "late", null, ReadOutcome.Success, BatteryValue.FromPercent(42), time.GetUtcNow(), time.GetTimestamp()));
            Check(session.State.Values.Count == 0 && !session.TryRead("manual") && fake.Created == 1);
            fake.Operation.Finish();
            Check(SpinWait.SpinUntil(() => !session.State.Busy, 1000));
            Check(session.State.Outcome == ReadOutcome.TimedOut && session.StopAsync().GetAwaiter().GetResult().FullyStopped);
            string prefix = @"Local\PeripheralBatteryTray.Tests." + Guid.NewGuid().ToString("N") + ".";
            using var instance = new SingleInstance(AppMode.Mock, prefix);
            using var context = new TrayApplicationContext(instance);
            int stops = 0;
            context.AdditionalShutdown = () => { stops++; return Task.FromResult(new ShutdownResult(false, 1, 2)); };
            context.RequestExit();
            var limit = Stopwatch.StartNew();
            while (!context.LastExit.IsCompleted && limit.Elapsed < TimeSpan.FromSeconds(3)) Application.DoEvents();
            Check(context.LastExit.IsCompleted && stops == 1 && context.Shutdown is { FullyStopped: false, UnresolvedOperations: 1, CleanupErrors: 2 });
        });
        Run("H03", "ATK host is explicitly selected and cannot compose Bluetooth or Mock providers", () =>
        {
            var options = StartupOptions.Parse(["--verify-atk-host", "--report", "atk.local.json"]);
            Check(options is { AtkCheck: true, Mock: false, GattCheck: false, HostCheck: false });
            foreach (string[] input in new string[][] { ["--verify-atk-host", "--mock"], ["--verify-atk-host", "--self-test"],
                ["--verify-atk-host", "--verify-gatt-host"], ["--verify-atk-host", "--verify-windows-host"],
                ["--verify-atk-host", "--measurement-report", "x"], ["--verify-atk-host", "--exit"] })
                Check(StartupOptions.Parse(input) is null);
            int called = 0; bool rejected = false;
            try { AppComposition.Create(options!, _ => { called++; throw new InvalidOperationException(); }); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && called == 0 && !StartupOptions.Parse([])!.AtkCheck);
        });
        Run("H04", "Manual ATK values require exact successful replies and expire or invalidate without device I/O", () =>
        {
            var time = new ManualTime(); var fake = new TrayAtkTransport(); var session = new AtkTraySession(fake, time);
            Check(fake.Scans == 0 && fake.Opens == 0 && session.Reading.Percent is null);
            Check(session.TryRead() && session.ActiveTask.IsCompletedSuccessfully);
            Check(session.Reading is { Percent: 95, LastPercent: 95 } && fake.Commands.Count == 3);
            Check(!session.TryRead() && fake.Commands.Count == 3);
            time.Advance(300);
            Check(session.Reading is { Percent: null, LastPercent: 95 } && fake.Commands.Count == 3);
            fake.Online = false;
            Check(session.TryRead() && session.Reading is { Percent: null, LastPercent: 95 } && fake.Commands.Count == 4);
            time.Advance(10); fake.Online = true;
            Check(session.TryRead() && session.Reading.Percent == 95);
            session.Suspend(); session.Resume();
            Check(session.Reading.Percent is null && fake.Commands.Count == 7);
            time.Advance(10); // Resume does not reset the shared minimum query interval.
            Check(session.TryRead() && session.Reading.Percent == 95 && fake.Commands.Count == 10);
            Check(session.StopAsync().GetAwaiter().GetResult().FullyStopped && !session.TryRead());
            fake = new() { Checksum = false }; session = new(fake);
            Check(session.TryRead() && session.Reading.Percent is null && !session.Reading.CanRead);
            session.StopAsync().GetAwaiter().GetResult();
        });
        Run("H05", "Tray manual ATK action updates list and minimum while ordinary refresh does not query HID", () =>
        {
            string prefix = @"Local\PeripheralBatteryTray.Tests." + Guid.NewGuid().ToString("N") + ".";
            using var instance = new SingleInstance(AppMode.Real, prefix);
            var fake = new TrayAtkTransport(); var atk = new AtkTraySession(fake);
            var coordinator = new BatteryCoordinator(BatteryMode.Real, []);
            using var context = new TrayApplicationContext(instance, coordinator, atk: atk);
            Check(fake.Scans == 0 && context.AtkMenu is not null);
            context.ShowPopup(); Application.DoEvents(); context.RequestRefresh(); Application.DoEvents();
            Check(fake.Scans == 0 && context.Popup.DeviceRowCount == 1);
            context.RequestAtkRead(); Application.DoEvents();
            Check(atk.ActiveTask.IsCompletedSuccessfully && context.DisplayedMinimumPercent == 95 && context.Popup.DeviceRowCount == 1);
            atk.Suspend(); Application.DoEvents();
            Check(context.DisplayedMinimumPercent is null && atk.Reading.LastPercent == 95);
            context.RequestExit();
            Check(SpinWait.SpinUntil(() => context.LastExit.IsCompleted, 3000) && context.Shutdown!.FullyStopped);
        });
        AtkAutoCases.Run(Run, Check);
        LowBatteryAppCases.Run(Run, Check);
        DeviceDisplayCases.Run(Run, Check);
        if (args.Length == 2 && args[0] == "--report")
        {
            string path = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { Passed = _failures == 0, BuildId = BuildInfo.Id, TimestampUtc = DateTimeOffset.UtcNow,
                Runtime = Environment.Version.ToString(), Scope = "Real parser, composition and isolated named-object contracts; no hardware or Computer Use.",
                AssertionCount = _assertions, Checks, Assemblies = new[] { typeof(Program).Assembly.Location, typeof(SingleInstance).Assembly.Location,
                    typeof(BatteryRules).Assembly.Location, typeof(PnpNative).Assembly.Location }.Select(p => new { File = Path.GetFileName(p), Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) }) }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return _failures == 0 ? 0 : 1;
    }

    private sealed class TrayAtkTransport : IAtkHidTransport
    {
        internal int Scans, Opens; internal bool Online = true, Checksum = true;
        internal List<AtkReadCommand> Commands = [];
        public Task<AtkHidScan> ScanAsync(CancellationToken cancellation)
        {
            Scans++;
            return Task.FromResult(new AtkHidScan([new("private-atk-path", new(0x373B, 0x11FD, 1, 0xFF02, 2, 17, 17, 0, true, true))], 0));
        }
        public Task<IAtkHidConnection> OpenAsync(AtkHidEndpoint endpoint, CancellationToken cancellation)
        { Opens++; return Task.FromResult<IAtkHidConnection>(new TrayAtkConnection(this)); }
        private sealed class TrayAtkConnection(TrayAtkTransport owner) : IAtkHidConnection
        {
            public Task<AtkReplyObservation> QueryAsync(AtkReadCommand command, CancellationToken cancellation)
            {
                owner.Commands.Add(command);
                return Task.FromResult(new AtkReplyObservation(command, 0, command == AtkReadCommand.Battery ? (byte)2 : (byte)3,
                    owner.Checksum, OnlineCandidate: command == AtkReadCommand.WirelessOnline ? owner.Online : null,
                    DuckbillUltraCandidate: command == AtkReadCommand.MouseIdentity ? true : null,
                    PercentCandidate: command == AtkReadCommand.Battery ? 95 : null));
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private static ResolvedBinding GattBinding(ConnectionState connection)
    {
        var binding = new DeviceBinding { DeviceId = "manual", BindingKey = "manual", BindingEpoch = 1,
            Ble = new(connection, 1), GattDatabaseEpoch = 1 };
        return new(new(binding, "test", "BLE"), [new(new("private-aep"), TransportKind.Ble, "test", true, connection, null, 63)], []);
    }
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ticks;
        public void Advance(int seconds) => _ticks += seconds * 1000;
    }
    private sealed class ManualProvider : IBatteryProvider
    {
        public ReadingSource Source => ReadingSource.GattUncached;
        public int Created; public ManualOperation? Operation; public QueryRequest? LastRequest;
        public IBatteryReadOperation CreateRead(QueryRequest request, CancellationToken lifetime)
        { Created++; LastRequest = request; return Operation = new(); }
        public ValueTask DisposeAsync() { Operation?.RequestCancel(); return Operation is null ? ValueTask.CompletedTask : new(Operation.Completion); }
    }
    private sealed class ManualOperation : IBatteryReadOperation
    {
        private readonly TaskCompletionSource<ReadCompletion> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<ProviderUpdate>? Publish; public bool Cancelled;
        public Task<ReadCompletion> Completion => _done.Task;
        public void Start(Action<ProviderUpdate> publish) => Publish = publish;
        public void RequestCancel() => Cancelled = true;
        public void Finish() => _done.TrySetResult(new(ReadOutcome.Success));
    }
}
