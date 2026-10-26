using System.Text.Json;
using PeripheralBatteryTray.App;
using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

internal static class LowBatteryAppCases
{
    internal static void Run(Action<string, string, Action> run, Action<bool> check)
    {
        run("NA01", "Notification configuration migrates off, preserves ATK and applies all fields atomically", () =>
        {
            string path = Path.GetFullPath("notifications.test.json"); var files = new Files();
            foreach (int version in new[] { 1, 2 })
            {
                files.Data[path] = $$"""{"schemaVersion":{{version}},"pollIntervalMinutes":7,"atk":{"autoRefreshEnabled":true,"pollIntervalMinutes":10},"notifications":{"lowBatteryEnabled":true,"thresholdPercent":9},"future":42}""";
                var old = new PollingSettingsStore(path, files);
                check(!old.Current.LowBatteryEnabled && old.Current.LowBatteryThreshold == 20 && files.Writes == 0);
                check(old.Current.AtkAutoEnabled == (version == 2));
            }
            var store = new PollingSettingsStore(path, files);
            check(store.TrySave(8, true, 12, true, 15));
            store = new(path, files);
            check(store.Current is { Minutes: 8, AtkAutoEnabled: true, AtkMinutes: 12, LowBatteryEnabled: true, LowBatteryThreshold: 15 });
            using (var json = JsonDocument.Parse(files.Data[path])) check(json.RootElement.GetProperty("schemaVersion").GetInt32() == 4
                && json.RootElement.GetProperty("future").GetInt32() == 42);
            check(store.TrySave(6) && new PollingSettingsStore(path, files).Current.LowBatteryEnabled);
            string before = files.Data[path]; files.Fail = true;
            using (var form = new SettingsForm(store, BatteryMode.Real, _ => throw new InvalidOperationException("Applied failed save")))
            {
                form.Show(); form.LowBatteryEnabled.Checked = false; form.LowBatteryThreshold.Value = 30; form.ApplyButton.PerformClick();
                check(store.Current.LowBatteryEnabled && store.Current.LowBatteryThreshold == 15 && files.Data[path] == before); form.Close();
            }
            files.Fail = false;
            foreach (string notifications in new[] { "{}", "null", "true",
                """{"lowBatteryEnabled":true,"thresholdPercent":0}""", """{"lowBatteryEnabled":true,"thresholdPercent":51}""",
                """{"lowBatteryEnabled":true,"thresholdPercent":"20"}""", """{"lowBatteryEnabled":true,"lowBatteryEnabled":false,"thresholdPercent":20}""" })
            {
                files.Data[path] = "{\"schemaVersion\":3,\"pollIntervalMinutes\":5,\"notifications\":" + notifications + "}";
                var invalid = new PollingSettingsStore(path, files);
                check(!invalid.Current.CanSave && !invalid.Current.LowBatteryEnabled && !invalid.Current.AtkAutoEnabled);
            }
            files.Data[path] = """{"schemaVersion":3,"pollIntervalMinutes":5,"notifications":{"lowBatteryEnabled":false,"thresholdPercent":20,"future":9}}""";
            store = new(path, files); check(store.TrySave(5, false, 5, true, 1));
            using (var json = JsonDocument.Parse(files.Data[path])) check(json.RootElement.GetProperty("notifications").GetProperty("future").GetInt32() == 9);
            using var mock = new SettingsForm(new(), BatteryMode.Mock, _ => { });
            check(!mock.LowBatteryEnabled.Enabled && !mock.LowBatteryThreshold.Enabled);
        });
        run("NA02", "Alert projection rejects cache, bands, stale and offline values and shell policy fails closed", () =>
        {
            var time = TimeProvider.System; var period = TimeSpan.FromMinutes(5);
            var binding = new DeviceBinding { DeviceId = "private-device", BindingKey = "private-binding", BindingEpoch = 1,
                Ble = new(ConnectionState.Connected, 1), GattDatabaseEpoch = 1 };
            var stamp = binding.Stamp(ReadingSource.GattUncached, 1);
            var source = new SourceState { Source = ReadingSource.GattUncached, CandidateKey = "private-candidate", ComponentKey = "private-component",
                LatestOutcome = ReadOutcome.Success, LatestRequestStamp = stamp,
                LastSuccess = new(BatteryValue.FromPercent(18), ReadingSource.GattUncached, stamp, time.GetUtcNow(), time.GetTimestamp()) };
            var component = new ComponentState("private-component", "private-service", 0, source, true);
            LowBatterySample Project(ComponentState c, DeviceBinding? b = null) => LowBatteryProjection.Samples(
                new(1, time.GetUtcNow(), BatteryMode.Real, [new(b ?? binding, "Headset", "BLE", components: [c])], time, period), time, period, null).Single();
            check(Project(component).Percent == 18);
            check(Project(component, binding with { Ble = new(ConnectionState.Disconnected, 1) }).Percent is null);
            check(Project(component with { Source = source with { LatestOutcome = ReadOutcome.Error } }).Percent is null);
            check(Project(component with { Source = source with { LastSuccess = source.LastSuccess with { Value = BatteryValue.FromBand(BatteryBand.Low) } } }).Percent is null);
            check(Project(component with { Source = source with { LastSuccess = source.LastSuccess with {
                CapturedTimestamp = time.GetTimestamp() - (long)(600 * time.TimestampFrequency) } } }).Percent is null);
            var systemStamp = binding.Stamp(ReadingSource.WindowsProperty, 1);
            var system = source with { Source = ReadingSource.WindowsProperty, LatestRequestStamp = systemStamp,
                LastSuccess = source.LastSuccess with { Source = ReadingSource.WindowsProperty, QueryStamp = systemStamp } };
            check(!LowBatteryProjection.Samples(new(1, time.GetUtcNow(), BatteryMode.Real,
                [new(binding, "Cached", "BLE", [system])], time, period), time, period, null).Any());
            foreach (int state in new[] { 0, 1, 2, 3, 4, 6, 7, 99 }) check(!TrayLowBatteryNotifier.Allows(0, state));
            check(TrayLowBatteryNotifier.Allows(0, 5) && !TrayLowBatteryNotifier.Allows(-1, 5));
            string message = TrayLowBatteryNotifier.Message(Enumerable.Range(0, 30).Select(i => new LowBatteryAlert("x" + i, new string('a', 400) + "\n", 0)).ToArray());
            check(message.Length <= 240 && !message.Contains('\n'));
        });
        run("NA03", "Actual tray and settings detect one ATK crossing with no new queries or real shell notifications", () =>
        {
            string prefix = @"Local\PeripheralBatteryTray.Tests." + Guid.NewGuid().ToString("N") + ".";
            using var instance = new SingleInstance(AppMode.Real, prefix);
            var time = new Clock(); var fake = new Transport(); var atk = new AtkTraySession(fake, time); var sink = new Sink();
            var store = new PollingSettingsStore(); var coordinator = new BatteryCoordinator(BatteryMode.Real, []);
            using var context = new TrayApplicationContext(instance, coordinator, settingsStore: store, atk: atk, notifier: sink);
            using (var form = new SettingsForm(store, BatteryMode.Real, context.ApplySettings, true))
            {
                form.Show(); form.LowBatteryEnabled.Checked = true; form.LowBatteryThreshold.Value = 20;
                check(!store.Current.LowBatteryEnabled && fake.Scans == 0); form.ApplyButton.PerformClick();
                check(store.Current.LowBatteryEnabled && fake.Scans == 0 && !atk.AutoEnabled);
                var controls = All(form).Where(c => c.Visible).ToArray();
                check(controls.All(c => form.RectangleToScreen(form.ClientRectangle).Contains(c.RectangleToScreen(c.ClientRectangle))));
                form.Close();
            }
            void Read(int percent) { fake.Percent = percent; time.Advance(); context.RequestAtkRead(); Application.DoEvents(); }
            Read(70); Read(20); check(sink.Calls == 1 && sink.Last!.Single().Percent == 20);
            int scans = fake.Scans;
            for (int i = 0; i < 100; i++) { context.RequestRefresh(); Application.DoEvents(); }
            check(fake.Scans == scans && sink.Calls == 1 && context.ListRenderCount == 0);
            Read(19); fake.Online = false; Read(0); check(sink.Calls == 1 && context.DisplayedMinimumPercent is null);
            fake.Online = true; Read(18); Read(24); Read(20); check(sink.Calls == 1);
            sink.Result = NotificationDelivery.Suppressed; Read(25); Read(19); check(sink.Calls == 2);
            sink.Result = NotificationDelivery.Requested; Read(18); check(sink.Calls == 2); // No deferred notification.
            sink.Throw = true; Read(30); Read(19); check(sink.Calls == 3);
            sink.Throw = false; Read(18); check(sink.Calls == 3);
            var d = context.CaptureDiagnostics();
            check(d is { SchemaVersion: 3, LowBattery: { Enabled: true, ThresholdPercent: 20, Crossings: 3, ShellRequests: 1, Suppressed: 1, Failures: 1 } });
            string json = System.Text.Encoding.UTF8.GetString(DiagnosticExporter.Serialize(d));
            check(!json.Contains("private-") && json.Length <= DiagnosticExporter.MaxBytes);
            context.ApplySettings(store.Current with { LowBatteryThreshold = 30 }); Read(10); check(sink.Calls == 3);
            context.ApplySettings(store.Current with { LowBatteryEnabled = false }); Read(90); Read(0); check(sink.Calls == 3);
            context.RequestExit(); Application.DoEvents(); check(context.Shutdown?.FullyStopped == true);
        });
    }
    private static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x)));
    private sealed class Sink : ILowBatteryNotifier
    {
        internal int Calls; internal bool Throw; internal NotificationDelivery Result = NotificationDelivery.Requested;
        internal IReadOnlyList<LowBatteryAlert>? Last;
        public NotificationDelivery Send(IReadOnlyList<LowBatteryAlert> alerts)
        { Calls++; Last = alerts; if (Throw) throw new IOException(); return Result; }
    }
    private sealed class Clock : TimeProvider
    { private long _ticks; public override long TimestampFrequency => 1000; public override long GetTimestamp() => _ticks; internal void Advance() => _ticks += 10000; }
    private sealed class Transport : IAtkHidTransport
    {
        internal int Scans, Percent = 70; internal bool Online = true;
        public Task<AtkHidScan> ScanAsync(CancellationToken token)
        { Scans++; return Task.FromResult(new AtkHidScan([new("private-path", new(0x373B, 0x11FD, 1, 0xFF02, 2, 17, 17, 0, true, true))], 0)); }
        public Task<IAtkHidConnection> OpenAsync(AtkHidEndpoint e, CancellationToken token) => Task.FromResult<IAtkHidConnection>(new Connection(this));
        private sealed class Connection(Transport owner) : IAtkHidConnection
        {
            public Task<AtkReplyObservation> QueryAsync(AtkReadCommand c, CancellationToken token) => Task.FromResult(new AtkReplyObservation(c, 0, 2, true,
                c == AtkReadCommand.WirelessOnline ? owner.Online : null, c == AtkReadCommand.MouseIdentity ? true : null,
                c == AtkReadCommand.Battery ? owner.Percent : null));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class Files : ISettingsFiles
    {
        internal Dictionary<string, string> Data = []; internal bool Fail; internal int Writes;
        public bool Exists(string p) => Data.ContainsKey(p); public string Read(string p) => Data[p]; public void CreateDirectory(string p) { }
        public void WriteNew(string p, string text, Action created) { Data.Add(p, text); Writes++; created(); }
        public void Replace(string a, string b) { if (Fail) throw new IOException(); Data[b] = Data[a]; Data.Remove(a); }
        public void MoveNew(string a, string b) { if (Fail) throw new IOException(); Data.Add(b, Data[a]); Data.Remove(a); }
        public void DeleteOwn(string p) => Data.Remove(p);
    }
}
