using System.Text;
using System.Text.Json;
using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

internal static class PlatformIntegrationCases
{
    internal static void Run(Action<string, string, Action> run, Action<bool> check)
    {
        void Wait(Func<bool> p) => check(SpinWait.SpinUntil(p, 3000));
        run("J02", "Runtime power resume waits for old watcher terminals and fresh connection evidence", () =>
        {
            var factory = new FakeWatchers(); var time = new SchedulerTime();
            var runtime = new WindowsBatteryRuntime(time, factory, new FakeNative());
            var c = new BatteryCoordinator(BatteryMode.Real, [runtime.Provider], time);
            runtime.Start(c); factory.Watchers[1].Add("ble-power", true);
            factory.Watchers[1].Emit(EndpointEventKind.EnumerationCompleted);
            var original = c.Snapshot.Devices.Single().Binding;
            runtime.Suspend(); runtime.Suspend(); runtime.Resume(); runtime.Resume();
            check(factory.Watchers.Count == 2 && factory.Watchers.All(w => w.StopCalls == 1));
            check(c.Snapshot.Devices.Single().Binding.SleepInvalidated);
            factory.Watchers[1].Add("ble-power", true);
            check(c.Snapshot.Devices.Single().Binding.SleepInvalidated);
            factory.Watchers[1].Emit(EndpointEventKind.Stopped);
            factory.Watchers[0].Emit(EndpointEventKind.Stopped);
            check(factory.Watchers.Count == 4);
            factory.Watchers[1].Add("late-old", true);
            check(!runtime.Discovery.Endpoints.Any(e => e.Id.Value == "late-old"));
            // Replacement BLE watcher is #2: it is created when the old BLE watcher terminates.
            factory.Watchers[2].Emit(EndpointEventKind.Updated, "ble-power", (BluetoothDiscovery.Connected, true));
            check(!runtime.Discovery.Endpoints.Any());
            factory.Watchers[2].Add("ble-power", true);
            var current = c.Snapshot.Devices.Single().Binding;
            check(!current.SleepInvalidated && current.Ble?.Connection == ConnectionState.Connected
                && current.Ble.Epoch > original.Ble!.Epoch + 1);
            factory.AutoStop = true; check(c.StopAsync().GetAwaiter().GetResult().FullyStopped);
            runtime.Resume(); check(factory.Watchers.Count == 4);
        });
        run("G24", "Observed real read facade enforces UUID/cache whitelist and records adapter steps", () =>
        {
            var f = new GattFixture(); f.Start();
            check(f.Client.Log.SequenceEqual(["open", "services:Cached", "chars:10:Cached", "read:Uncached", "service:dispose", "device:dispose"]));
            var t = f.Coordinator.Telemetry.Capture().Counters.Single(c => c.Source == ReadingSource.GattUncached);
            check(t.AdapterCallsStarted == 4 && t.AdapterCallsTerminal == 4 && t.Queries == 1 && t.Values == 1 && t.OperationTerminals == 1);
            var facade = new ObservedGattClient(f.Client, new());
            using var device = facade.Open(new("fake"), () => true).Terminal.GetAwaiter().GetResult()!;
            bool rejected = false; try { device.Services(Guid.NewGuid(), GattCacheMode.Cached); } catch (ArgumentException) { rejected = true; }
            check(rejected);
            var services = device.Services(BatteryGattUuids.Service, GattCacheMode.Cached).Terminal.GetAwaiter().GetResult().Value;
            using var service = services.Single();
            rejected = false; try { service.Characteristics(Guid.NewGuid(), GattCacheMode.Cached); } catch (ArgumentException) { rejected = true; } check(rejected);
            var characteristic = service.Characteristics(BatteryGattUuids.Level, GattCacheMode.Cached).Terminal.GetAwaiter().GetResult().Value.Single();
            rejected = false; try { characteristic.Read(GattCacheMode.Cached); } catch (ArgumentException) { rejected = true; } check(rejected);
            check(typeof(IGattCharacteristic).GetMethods().All(m => !m.Name.Contains("Write", StringComparison.Ordinal)));
            f.Stop();
        });
        run("W24", "Export projects private Windows identities through typed aliases only", () =>
        {
            var f = new GattFixture(); f.Start();
            string raw = Encoding.UTF8.GetString(DiagnosticExporter.Serialize(f.Coordinator.CreateDiagnosticReport("test", "WindowsProperty")));
            check(!raw.Contains("fake-private-endpoint") && !raw.Contains("Private name") && !raw.Contains("device:1") && !raw.Contains("CandidateKey"));
            using var doc = JsonDocument.Parse(raw);
            check(doc.RootElement.GetProperty("Devices")[0].GetProperty("Model").GetString() == "蓝牙设备1"); f.Stop();
        });
        run("P06", "Discovery failure is retained separately from empty success and cached-only state", () =>
        {
            var factory = new FakeWatchers { AutoStop = true };
            var runtime = new WindowsBatteryRuntime(new SchedulerTime(), factory, new FakeNative());
            var c = new BatteryCoordinator(BatteryMode.Real, [runtime.Provider], new SchedulerTime());
            runtime.Start(c); factory.Watchers[0].Emit(EndpointEventKind.EnumerationCompleted); factory.Watchers[1].Emit(EndpointEventKind.EnumerationCompleted);
            check(runtime.DiscoveryNotice is null && c.Snapshot.Devices.Count == 0);
            int notices = 0; c.SnapshotChanged += _ => notices++;
            factory.Watchers[1].Emit(EndpointEventKind.Aborted);
            check(runtime.DiscoveryNotice is not null && runtime.Discovery.BleStatus == DiscoveryStatus.Failed);
            check(notices > 0);
            check(c.Snapshot.Mode == BatteryMode.Real && c.Snapshot.TrayVisual.MinimumPercent is null);
            c.StopAsync().GetAwaiter().GetResult();
        });
        run("P07", "Newest immutable snapshot occupies one UI callback and ignores same visual timestamp changes", () =>
        {
            var actions = new Queue<Action>(); var shown = new List<long>(); var time = new SchedulerTime();
            using var mailbox = new SnapshotMailbox(actions.Enqueue, s => shown.Add(s.Version));
            for (int i = 1; i <= 100; i++) mailbox.Publish(new(i, time.GetUtcNow(), BatteryMode.Real, [], time, TimeSpan.FromMinutes(5)));
            check(actions.Count == 1); actions.Dequeue()(); check(shown.SequenceEqual([100L]));
            mailbox.Publish(new(99, time.GetUtcNow(), BatteryMode.Real, [], time, TimeSpan.FromMinutes(5))); check(actions.Count == 0);
        });
        run("P08", "Suspend and reconnect refuse previous success and database epochs", () =>
        {
            var f = new GattFixture(); f.Start(); check(f.Coordinator.Snapshot.TrayVisual.MinimumPercent == 73);
            var before = f.Coordinator.Snapshot.Devices.Single(); f.Coordinator.Suspend(); f.Coordinator.Resume();
            check(f.Coordinator.Snapshot.TrayVisual.MinimumPercent is null && f.Client.Opens == 1);
            var next = new BatteryDeviceState(before.Binding with { Ble = new(ConnectionState.Connected, before.Binding.Ble!.Epoch + 2) }, before.DisplayName, before.ConnectionType);
            f.Rebind(next); check(f.Coordinator.Snapshot.TrayVisual.MinimumPercent is null);
            f.Time.Advance(TimeSpan.FromSeconds(10)); Wait(() => f.Coordinator.ActiveReads.Count == 0);
            check(f.Client.Opens == 2 && f.Coordinator.Snapshot.TrayVisual.MinimumPercent == 73); f.Stop();
        });
        run("P09", "Shared shutdown budget keeps unresolved original operation visible and rejects UI publish", () =>
        {
            var f = new GattFixture(); var pending = new FakeGattCall<IGattDevice?>(); f.Client.Pending = pending; f.Start();
            int publishes = 0; f.Coordinator.SnapshotChanged += _ => publishes++;
            var stop = f.Coordinator.StopAsync(); f.Time.Advance(TimeSpan.FromSeconds(2)); Wait(() => stop.IsCompleted);
            check(!stop.Result.FullyStopped && stop.Result.UnresolvedOperations > 0);
            pending.Fail(new IOException("secret path")); Wait(() => f.Coordinator.ActiveReads.Count == 0);
            check(publishes == 0 && ReferenceEquals(stop, f.Coordinator.StopAsync()));
        });
        run("P10", "Failed/future settings preserve bytes and period; Mock memory has no file access", () =>
        {
            var files = new FakeSettingsFiles(); string path = Path.GetFullPath("integration-settings.json");
            files.Data[path] = "{\"schemaVersion\":9,\"pollIntervalMinutes\":2}";
            var s = new PollingSettingsStore(path, files); string before = files.Data[path];
            check(!s.TrySave(3) && files.Data[path] == before && s.Current.Minutes == 5);
            var isolated = new PollingSettingsStore(null, files); check(isolated.TrySave(30) && files.Data.Count == 1 && files.Data[path] == before);
        });
        run("P11", "Diagnostic cancellation and concurrent exports leave query counts unchanged", () =>
        {
            var f = new GattFixture(); f.Start(); var exporter = new DiagnosticExportSession();
            var original = f.Coordinator.Telemetry.Capture(); int writes = 0;
            var cancelled = exporter.ExportAsync(() => null, () => throw new Exception("must not capture")).GetAwaiter().GetResult();
            check(cancelled == ExportResult.Cancelled && !exporter.IsActive);
            var gate = new TaskCompletionSource(); byte[]? saved = null;
            var active = exporter.ExportAsync(() => "fake.json", () => f.Coordinator.CreateDiagnosticReport("test", "test"),
                (_, bytes) => { writes++; saved = bytes; return gate.Task; });
            check(exporter.IsActive && !active.IsCompleted);
            var duplicate = exporter.ExportAsync(() => throw new Exception("must not prompt twice"), () => throw new Exception());
            check(duplicate.Result == ExportResult.Busy && writes == 1);
            f.Time.Advance(TimeSpan.FromSeconds(1)); f.Coordinator.SetRefreshMinutes(6);
            gate.SetResult(); check(active.Result == ExportResult.Saved && !exporter.IsActive);
            using var json = JsonDocument.Parse(saved!); check(json.RootElement.GetProperty("PollIntervalMinutes").GetInt32() == 5);
            check(f.Coordinator.Telemetry.Capture().Counters.SequenceEqual(original.Counters)); f.Stop();
        });
        run("P12", "Event storm and oversized export retain valid JSON within budget and fresh aliases", () =>
        {
            var ring = new DiagnosticRing();
            for (int i = 0; i < 2000; i++) ring.Add(ReadingSource.GattUncached, i % 2 == 0 ? DiagnosticStep.Open : DiagnosticStep.Read, ReadOutcome.Error, i);
            check(ring.Snapshot().Count == 128 && ring.Snapshot()[0].Timestamp == 1872);
            for (int i = 0; i < 100; i++) ring.Add(ReadingSource.GattUncached, DiagnosticStep.Read, ReadOutcome.Error, i);
            check(ring.Snapshot()[^1].Count == 101 && ring.Snapshot().Count == 128);
            var c = new BatteryCoordinator(BatteryMode.Real, []);
            for (int i = 0; i < 300; i++) c.UpsertDevice(new(new DeviceBinding { DeviceId = "secret-" + i, BindingKey = "MAC-private-" + i },
                new string('x', 1000) + "secret name", "BLE", Enumerable.Range(0, 8).Select(j => new SourceState {
                    Source = ReadingSource.WindowsProperty, CandidateKey = "private-path-" + j })));
            var a = c.CreateDiagnosticReport("test", "test"); var b = c.CreateDiagnosticReport("test", "test");
            check(a.Devices[0].Alias != b.Devices[0].Alias);
            byte[] bytes = DiagnosticExporter.Serialize(a); check(bytes.Length <= DiagnosticExporter.MaxBytes);
            using var doc = JsonDocument.Parse(bytes); check(doc.RootElement.GetProperty("Truncated").GetBoolean());
            check(doc.RootElement.GetProperty("OmittedDevices").GetInt32() > 0 || doc.RootElement.GetProperty("OmittedValues").GetInt32() > 0);
            string text = Encoding.UTF8.GetString(bytes); check(!text.Contains("secret") && !text.Contains("private-path"));
            c.StopAsync().GetAwaiter().GetResult();
        });
        run("P13", "Logical timeout never claims native terminal and cleanup errors retain separate counters", () =>
        {
            var f = new GattFixture(); f.Service.ThrowDispose = true; var pending = new FakeGattCall<GattResult<byte[]?>>(); f.Characteristic.Pending = pending; f.Start();
            f.Time.Advance(TimeSpan.FromSeconds(10));
            var before = f.Coordinator.Telemetry.Capture().Counters.Single(c => c.Source == ReadingSource.GattUncached);
            check(before.Queries == 1 && before.Timeouts == 1 && before.OperationTerminals == 0 && before.AdapterCallsStarted == 4 && before.AdapterCallsTerminal == 3);
            check(f.Coordinator.CreateDiagnosticReport("test", "test").Draining == 1);
            pending.Finish(new(GattStatus.Success, [73])); Wait(() => f.Coordinator.ActiveReads.Count == 0);
            var after = f.Coordinator.Telemetry.Capture().Counters.Single(c => c.Source == ReadingSource.GattUncached);
            check(after.Timeouts == 1 && after.OperationTerminals == 1 && after.AdapterCallsTerminal == 4 && after.Values == 0 && after.CleanupErrors == 1);
            check(f.Coordinator.CreateDiagnosticReport("test", "test").Draining == 0); f.Stop();
        });
    }
}
