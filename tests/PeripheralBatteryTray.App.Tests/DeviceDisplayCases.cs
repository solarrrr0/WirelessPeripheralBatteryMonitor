using System.Text.Json;
using PeripheralBatteryTray.App;
using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

internal static class DeviceDisplayCases
{
    internal static void Run(Action<string, string, Action> run, Action<bool> check)
    {
        run("DP01", "Preference identity survives enumeration order and name changes without merging same-name devices", () =>
        {
            Endpoint A(string id, string name = "Same name") => new(new(id), TransportKind.Ble, name, true, ConnectionState.Connected, null);
            var first = new DeviceIdentityResolver().Resolve([A("private-a"), A("private-b")], []);
            var reversed = new DeviceIdentityResolver().Resolve([A("private-b"), A("private-a", "Renamed")], []);
            check(first.Count == 2 && first[0].Device.Binding.PreferenceKey != first[1].Device.Binding.PreferenceKey);
            check(first[0].Device.Binding.DeviceId == reversed[0].Device.Binding.DeviceId);
            check(first[0].Device.Binding.PreferenceKey == reversed[1].Device.Binding.PreferenceKey
                && first[1].Device.Binding.PreferenceKey == reversed[0].Device.Binding.PreferenceKey);
            check(first.All(d => DisplaySettings.ValidKey(d.Device.Binding.PreferenceKey)));
            var shared = new AepContainerId(Guid.Parse("11111111-2222-3333-4444-555555555555"));
            var merged = new DeviceIdentityResolver().Resolve([A("private-a") with { Container = shared },
                A("private-c") with { Container = shared, Transport = TransportKind.Classic }], []);
            var again = new DeviceIdentityResolver().Resolve([A("private-c") with { Container = shared, Transport = TransportKind.Classic },
                A("private-a") with { Container = shared }], []);
            check(merged.Count == 1 && merged[0].Device.Binding.PreferenceKey == again[0].Device.Binding.PreferenceKey);
            var coordinator = new BatteryCoordinator(BatteryMode.Real, []);
            coordinator.UpsertDevice(first[0].Device); coordinator.UpsertDevice(first[0].Device with { DisplayName = "Another name" });
            check(coordinator.Snapshot.Devices.Single().Binding.PreferenceKey == first[0].Device.Binding.PreferenceKey);
            coordinator.Suspend(); coordinator.Resume();
            check(coordinator.Snapshot.Devices.Single().Binding.PreferenceKey == first[0].Device.Binding.PreferenceKey);
            coordinator.StopAsync().GetAwaiter().GetResult();
        });
        run("DP02", "Schema4 display settings migrate safely, retain unknown fields and reject malformed or oversized preferences", () =>
        {
            var files = new Files(); string path = Path.GetFullPath("display.test.json"); string key = DisplaySettings.KeyFor("private-identity");
            foreach (int version in new[] { 1, 2, 3 })
            {
                files.Data[path] = $$"""{"schemaVersion":{{version}},"pollIntervalMinutes":7,"atk":{"autoRefreshEnabled":true,"pollIntervalMinutes":10},"notifications":{"lowBatteryEnabled":true,"thresholdPercent":9},"display":{"iconMode":"PerDevice"},"future":42}""";
                var legacy = new PollingSettingsStore(path, files);
                check(legacy.Current.CanSave && legacy.Current.Display.IconMode == TrayIconMode.Aggregate && legacy.Current.Display.Devices.Count == 0);
                check(legacy.Current.AtkAutoEnabled == (version >= 2) && legacy.Current.LowBatteryEnabled == (version == 3));
            }
            files.Data[path] = """{"schemaVersion":4,"pollIntervalMinutes":5,"future":42,"display":{"sort":"Name","iconMode":"PerDevice","future":17,"devices":{"__KEY__":{"alias":"My mouse","hidden":false,"future":18}}}}""".Replace("__KEY__", key);
            var store = new PollingSettingsStore(path, files);
            check(store.Current.Display.For(key).Alias == "My mouse" && store.TrySave(6));
            using (var json = JsonDocument.Parse(files.Data[path]))
            {
                var root = json.RootElement;
                check(root.GetProperty("schemaVersion").GetInt32() == 4 && root.GetProperty("future").GetInt32() == 42
                    && root.GetProperty("display").GetProperty("future").GetInt32() == 17
                    && root.GetProperty("display").GetProperty("devices").GetProperty(key).GetProperty("future").GetInt32() == 18);
            }
            string before = files.Data[path]; files.Fail = true;
            check(!store.TrySave(9, true, 8, true, 10, DisplaySettings.Default)
                && files.Data[path] == before && store.Current.Minutes == 6 && store.Current.Display.IconMode == TrayIconMode.PerDevice);
            files.Fail = false;
            foreach (string bad in new[] { "null", "{}", """{"sort":"Name","sort":"Battery","iconMode":"Aggregate","devices":{}}""",
                """{"sort":"1","iconMode":"Aggregate","devices":{}}""", """{"sort":"Name","iconMode":"aggregate","devices":{}}""",
                """{"sort":"Name","iconMode":"Aggregate","devices":{"private-path":{"alias":"x","hidden":false}}}""",
                """{"sort":"Name","iconMode":"Aggregate","devices":{"__KEY__":{"alias":"x\n","hidden":false}}}""".Replace("__KEY__", key),
                """{"sort":"Name","iconMode":"Aggregate","devices":{"__KEY__":{"alias":"x","hidden":"true"}}}""".Replace("__KEY__", key) })
            {
                files.Data[path] = "{\"schemaVersion\":4,\"pollIntervalMinutes\":5,\"display\":" + bad + "}";
                var invalid = new PollingSettingsStore(path, files);
                check(!invalid.Current.CanSave && !invalid.Current.AtkAutoEnabled && !invalid.Current.LowBatteryEnabled && !invalid.TrySave(5));
            }
            check(!DisplaySettings.ValidAlias(new string('x', 49)) && !DisplaySettings.ValidAlias("x\u202E") && !DisplaySettings.ValidAlias("\uD800"));
            check(DisplaySettings.ValidAlias("鼠标 🖱️") && !DisplaySettings.ValidKey("device:1"));
            var source = new Dictionary<string, DevicePreference> { [key] = new("Alias", true) };
            var immutable = new DisplaySettings(devices: source); source.Clear(); check(immutable.For(key).Hidden);
            bool rejected = false;
            try { _ = new DisplaySettings(devices: Enumerable.Range(0, 65).Select(i => new KeyValuePair<string, DevicePreference>(DisplaySettings.KeyFor(i.ToString()), new("x")))); }
            catch (ArgumentException) { rejected = true; } check(rejected);
            var memory = new PollingSettingsStore(); check(memory.TrySave(5, false, 5, false, 20, immutable));
            check(!JsonSerializer.Serialize(memory.Current.Display).Contains("private-identity"));
            string realPath = Path.Combine(AppContext.BaseDirectory, "display-" + Guid.NewGuid().ToString("N"), "config.local.json");
            var disk = new PollingSettingsStore(realPath);
            check(disk.TrySave(7, false, 8, false, 15, immutable));
            check(new PollingSettingsStore(realPath).Current.Display.For(key) == new DevicePreference("Alias", true));
        });
        run("DP03", "Presentation hides everywhere, sorts only current percentages and keeps aliases out of diagnostics", () =>
        {
            var time = TimeProvider.System; var period = TimeSpan.FromMinutes(5);
            var a = Device("a", "Z", 0); var b = Device("b", "A", 70);
            var cached = Device("cache", "Cache", null);
            var stamp = cached.Binding.Stamp(ReadingSource.WindowsProperty, 1);
            cached = cached.WithSystemCandidates([new() { Source = ReadingSource.WindowsProperty, CandidateKey = "cache", LatestOutcome = ReadOutcome.Success,
                LatestRequestStamp = stamp, LastSuccess = new(BatteryValue.FromPercent(1), ReadingSource.WindowsProperty, stamp, time.GetUtcNow(), time.GetTimestamp()) }]);
            var snapshot = new BatterySnapshot(1, time.GetUtcNow(), BatteryMode.Real, [b, cached, a], time, period);
            var preferences = new DisplaySettings(DeviceSort.Battery, TrayIconMode.PerDevice,
                new Dictionary<string, DevicePreference> { [a.Binding.PreferenceKey!] = new("PrivateAlias", true), [b.Binding.PreferenceKey!] = new("Mouse") });
            var projected = DevicePresentation.Project(snapshot, period, null, preferences);
            check(projected.Select(p => p.Id).SequenceEqual(new[] { "bluetooth:a", "bluetooth:b", "bluetooth:cache" }));
            check(projected[0].Hidden && projected[1].Name == "Mouse" && projected[2].Percent is null);
            check(DevicePresentation.Aggregate(projected, BatteryMode.Real).MinimumPercent == 70);
            check(LowBatteryProjection.Samples(snapshot, time, period, null, preferences).Single().DisplayName == "Mouse");
            using (var list = new DeviceListForm(() => { }, () => { }, () => { }))
            {
                list.SetPresentation(snapshot, period, projected); check(list.DeviceRowCount == 2);
                check(All(list).OfType<Label>().Any(l => l.Text == "Mouse") && !All(list).OfType<Label>().Any(l => l.Text == "PrivateAlias"));
                list.SetPresentation(snapshot, period, projected.Select(p => p with { Name = new string('名', 48) }).ToArray());
                list.Show(); list.PerformLayout();
                using (var preview = new Bitmap(list.Width, list.Height))
                { list.DrawToBitmap(preview, new(Point.Empty, preview.Size)); preview.Save(Path.Combine(AppContext.BaseDirectory, "long-alias-list.local.png")); }
                check(All(list).OfType<Label>().All(l => l.Width <= list.ClientSize.Width));
                list.Hide();
                list.SetPresentation(snapshot, period, projected.Select(p => p with { Hidden = true }).ToArray()); check(list.DeviceRowCount == 0);
            }
            check(DevicePresentation.Aggregate(projected.Select(p => p with { Hidden = true }), BatteryMode.Real).MinimumPercent is null);
            string json = System.Text.Encoding.UTF8.GetString(DiagnosticExporter.Serialize(DiagnosticExporter.Create(snapshot, time, period,
                new DiagnosticTelemetry().Capture(), "test", "test", 0, null)));
            check(!json.Contains("PrivateAlias") && !json.Contains(a.Binding.PreferenceKey!));
            var atk = new AtkTrayReading(null, 3, time.GetUtcNow(), "上次", false, true);
            check(DevicePresentation.Project(snapshot, period, atk, DisplaySettings.Default).Last().Percent is null);

            // Compact rows keep system provenance in hover/accessibility details, not in the percentage label.
            SourceState Report(int percent, string node) => new() { Source = ReadingSource.WindowsProperty, CandidateKey = node,
                LatestOutcome = ReadOutcome.Success, LatestRequestStamp = stamp,
                LastSuccess = new(BatteryValue.FromPercent(percent), ReadingSource.WindowsProperty, stamp, time.GetUtcNow(), time.GetTimestamp()) };
            using var compact = new DeviceListForm(() => { }, () => { }, () => { });
            BatterySnapshot CompactSnapshot(BatteryDeviceState device) => new(2, time.GetUtcNow(), BatteryMode.Real, [device], time, period);
            foreach (int percent in new[] { 0, 80, 100 })
            {
                compact.SetSnapshot(CompactSnapshot(cached.WithSystemCandidates([Report(percent, "one")])), period);
                check(All(compact).OfType<Label>().Any(l => l.Text == $"{percent}%"));
                check(!All(compact).OfType<Label>().Any(l => l.Text.Contains("系统节点") || l.Text.Contains("系统报告冲突")));
                check(compact.GetDeviceDetails(0).Contains("时效未知") && compact.GetDeviceDetails(0).Contains("仅供参考"));
            }
            var conflicting = cached.WithSystemCandidates([Report(100, "one"), Report(90, "two")]);
            var compactSnapshot = CompactSnapshot(conflicting);
            compact.SetSnapshot(compactSnapshot, period);
            check(All(compact).OfType<Label>().Any(l => l.Text == "90%"));
            check(compact.GetDeviceDetails(0).Contains("系统报告冲突") && compact.GetDeviceDetails(0).Contains("100%"));
            check(DevicePresentation.Project(compactSnapshot, period, null, DisplaySettings.Default).Single().Percent is null
                && !LowBatteryProjection.Samples(compactSnapshot, time, period, null, DisplaySettings.Default).Any());
            using (var diagnostic = JsonDocument.Parse(DiagnosticExporter.Serialize(DiagnosticExporter.Create(compactSnapshot, time, period,
                new DiagnosticTelemetry().Capture(), "test", "test", 0, null))))
                check(diagnostic.RootElement.GetProperty("Devices")[0].GetProperty("Values").EnumerateArray()
                    .Select(v => v.GetProperty("Percent").GetInt32()).Order().SequenceEqual([90, 100]));
            for (int i = 0; i < 20; i++) compact.SetSnapshot(compactSnapshot, period);
            check(compact.DeviceRowCount == 1 && All(compact).OfType<Label>().Count(l => l.Text == "90%") == 1);
            compact.Show(); compact.PerformLayout();
            using (var preview = new Bitmap(compact.Width, compact.Height))
            { compact.DrawToBitmap(preview, new(Point.Empty, preview.Size)); preview.Save(Path.Combine(AppContext.BaseDirectory, "compact-system-report.local.png")); }
            compact.Hide();
            compact.SetSnapshot(CompactSnapshot(conflicting with { Binding = conflicting.Binding with { Ble = new(ConnectionState.Disconnected, 1) } }), period);
            check(All(compact).OfType<Label>().Any(l => l.Text == "离线") && !All(compact).OfType<Label>().Any(l => l.Text == "90%"));
            compact.SetSnapshot(CompactSnapshot(cached), period);
            check(All(compact).OfType<Label>().Any(l => l.Text == "1%"));
            compact.SetSnapshot(CompactSnapshot(Device("missing", "Missing", null)), period);
            check(!All(compact).OfType<Label>().Any(l => l.Text == "0%") && !compact.GetDeviceDetails(0).Contains("系统节点"));
        });
        run("DP04", "Optional icon owners reuse, bound, recover and dispose icons; unchanged shell state does not repaint", () =>
        {
            var made = new List<Icon>();
            using var manager = new DeviceTrayIcons(() => { var icon = new Icon(); made.Add(icon); return icon; });
            PresentedDevice[] entries = Enumerable.Range(0, 20).Select(i => new PresentedDevice(i.ToString(), null, "X", "X", false,
                new(i, false, false, BatteryMode.Mock))).ToArray();
            manager.Update(entries, TrayIconMode.Aggregate, 16); check(manager.Count == 0 && made.Count == 0);
            manager.Update(entries, TrayIconMode.PerDevice, 16); check(manager.Count == 16 && manager.Omitted == 4 && made.Count == 16);
            for (int i = 0; i < 100; i++) manager.Update(entries, TrayIconMode.PerDevice, 16);
            check(made.Count == 16);
            manager.Recover(); check(made.All(i => i.Recoveries == 1));
            manager.Update(entries.Select(e => e with { Hidden = e.Id == "0" }), TrayIconMode.PerDevice, 24);
            check(made[0].Disposed && manager.Count == 16 && made.Count == 17);
            manager.Update([], TrayIconMode.Aggregate, 16); check(manager.Count == 0 && made.All(i => i.Disposed));
            manager.Dispose(); manager.Recover(); manager.Update(entries, TrayIconMode.PerDevice, 16); check(manager.Count == 0);
            using var menu = new ContextMenuStrip(); using var shell = new ShellDeviceTrayIcon(menu, () => { });
            for (int i = 0; i < 100; i++) shell.Update(entries[0], 16);
            check(shell.RenderCount == 1); shell.Update(entries[0] with { Name = "New alias" }, 16); check(shell.RenderCount == 1);
            shell.Update(entries[0], 32); check(shell.RenderCount == 2); shell.Recover(); check(shell.RenderCount == 2);
            string tooltip = ShellDeviceTrayIcon.Tooltip(new string('a', 39) + "🖱️\n", "100%", BatteryMode.Mock);
            check(tooltip.Length <= 63 && !tooltip.Contains('\n') && !tooltip.Any(char.IsSurrogate));
        });
        run("DP05", "Actual settings commit edits atomically and update tray/list without starting any ATK query", () =>
        {
            string prefix = @"Local\PeripheralBatteryTray.Tests." + Guid.NewGuid().ToString("N") + ".";
            using var instance = new SingleInstance(AppMode.Real, prefix);
            var coordinator = new BatteryCoordinator(BatteryMode.Real, []); var device = Device("live-test", "Headset", 11);
            coordinator.UpsertDevice(device);
            var store = new PollingSettingsStore(); var transport = new NoDevice(); var atk = new AtkTraySession(transport);
            var made = new List<Icon>();
            using var context = new TrayApplicationContext(instance, coordinator, settingsStore: store, atk: atk,
                deviceIconFactory: () => { var icon = new Icon(); made.Add(icon); return icon; });
            Application.DoEvents(); check(context.DisplayedMinimumPercent == 11 && context.DeviceIconCount == 0);
            using (var form = new SettingsForm(store, BatteryMode.Real, context.ApplySettings, true, context.PresentedDevices))
            {
                form.Show(); var tab = All(form).OfType<TabControl>().Single(); tab.SelectedIndex = 1;
                var grid = form.DeviceSettings.Grid; grid.CurrentCell = grid.Rows[0].Cells[1]; check(grid.BeginEdit(true));
                ((TextBox)grid.EditingControl!).Text = "My headset";
                form.DeviceSettings.IconChoice.SelectedIndex = 1; form.DeviceSettings.SortChoice.SelectedIndex = 1;
                form.ApplyButton.PerformClick();
                check(store.Current.Display.For(device.Binding.PreferenceKey).Alias == "My headset" && context.DeviceIconCount == 2 && transport.Scans == 0);
                grid.Rows[0].Cells[2].Value = true; form.ApplyButton.PerformClick();
                check(context.DisplayedMinimumPercent is null && context.DeviceIconCount == 1 && made.Count(i => i.Disposed) == 1);
                check(All(form).Where(c => c.Visible).All(c => form.RectangleToScreen(form.ClientRectangle).Contains(c.RectangleToScreen(c.ClientRectangle))));
                string preview = Path.Combine(AppContext.BaseDirectory, "device-settings.local.png");
                using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new(Point.Empty, image.Size)); image.Save(preview); }
                tab.SelectedIndex = 0;
                using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new(Point.Empty, image.Size)); image.Save(Path.Combine(AppContext.BaseDirectory, "refresh-settings.local.png")); }
                form.Close();
            }
            context.ShowPopup(); Application.DoEvents(); check(context.Popup.DeviceRowCount == 1 && transport.Scans == 0);
            context.SimulateTaskbarCreated(); check(made.Single(i => !i.Disposed).Recoveries == 1);
            context.RequestExit(); Application.DoEvents(); check(context.Shutdown?.FullyStopped == true && made.All(i => i.Disposed));
        });
        run("DP06", "Login startup writes only through the explicit control and rejects foreign values, failures and isolated modes", () =>
        {
            var registry = new StartupRegistry();
            var registration = new StartupRegistration(@"C:\Test Folder\中文\PeripheralBatteryTray.exe", registry);
            check(registry.Reads == 0 && registry.Writes == 0 && registration.Command == "\"C:\\Test Folder\\中文\\PeripheralBatteryTray.exe\"");
            var store = new PollingSettingsStore();
            using (var form = new SettingsForm(store, BatteryMode.Real, _ => { }, startup: registration))
            {
                form.Show(); All(form).OfType<TabControl>().Single().SelectedIndex = 2;
                check(!form.StartupSettings.EnabledChoice.Checked && registry.Writes == 0);
                form.StartupSettings.EnabledChoice.Checked = true;
                form.ApplyButton.PerformClick(); check(registry.Writes == 0);
                form.StartupSettings.SaveButton.PerformClick(); check(registry.Writes == 1 && registry.Value.Command == registration.Command);
                check(form.StartupSettings.EnabledChoice.Checked);
                form.StartupSettings.EnabledChoice.Checked = false; form.StartupSettings.SaveButton.PerformClick();
                check(registry.Writes == 2 && registry.Value.Command is null && !form.StartupSettings.EnabledChoice.Checked);
                registry.Fail = true; form.StartupSettings.EnabledChoice.Checked = true; form.StartupSettings.SaveButton.PerformClick();
                check(!form.StartupSettings.EnabledChoice.Checked && form.StartupSettings.Status.Text.Contains("失败")); form.Close();
            }
            registry.Fail = false; registry.Value = new("\"C:\\Other Version\\app.exe\""); int before = registry.Writes;
            check(!registration.Read().CanChange && !registration.SetEnabled(true).CanChange && registry.Writes == before);
            registry.Value = new(null, false); check(!registration.Read().CanChange && !registration.SetEnabled(false).CanChange);
            int reads = registry.Reads;
            using (var form = new SettingsForm(store, BatteryMode.Mock, _ => { }, startup: registration))
                check(!form.StartupSettings.SaveButton.Enabled && !form.StartupSettings.EnabledChoice.Enabled && registry.Reads == reads);
            bool rejected = false; try { _ = new StartupRegistration("relative.exe", registry); } catch (ArgumentException) { rejected = true; } check(rejected);
            registry.Value = new(null);
            var longPath = new StartupRegistration(@"C:\" + string.Concat(Enumerable.Repeat("long-folder\\", 30)) + "app.exe", registry);
            check(!longPath.Read().CanChange && registry.Writes == before);
        });
    }
    private static BatteryDeviceState Device(string id, string name, int? percent)
    {
        var binding = new DeviceBinding { DeviceId = id, BindingKey = id, PreferenceKey = DisplaySettings.KeyFor(id), BindingEpoch = 1,
            Ble = new(ConnectionState.Connected, 1), GattDatabaseEpoch = 1 };
        var stamp = binding.Stamp(ReadingSource.GattUncached, 1); var time = TimeProvider.System;
        return new(binding, name, "BLE", components: percent is null ? [] : [new("battery", "service", 0, new() {
            Source = ReadingSource.GattUncached, CandidateKey = "battery", ComponentKey = "battery", LatestOutcome = ReadOutcome.Success,
            LatestRequestStamp = stamp, LastSuccess = new(BatteryValue.FromPercent(percent.Value), ReadingSource.GattUncached, stamp, time.GetUtcNow(), time.GetTimestamp()) }, true)]);
    }
    private static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x)));
    private sealed class Icon : IDeviceTrayIcon
    {
        internal bool Disposed; internal int Recoveries;
        public void Update(PresentedDevice device, int size) { if (Disposed) throw new ObjectDisposedException("icon"); }
        public void Recover() => Recoveries++;
        public void Dispose() => Disposed = true;
    }
    private sealed class NoDevice : IAtkHidTransport
    {
        internal int Scans;
        public Task<AtkHidScan> ScanAsync(CancellationToken token) { Scans++; throw new InvalidOperationException("No device access"); }
        public Task<IAtkHidConnection> OpenAsync(AtkHidEndpoint endpoint, CancellationToken token) => throw new InvalidOperationException("No device access");
    }
    private sealed class Files : ISettingsFiles
    {
        internal Dictionary<string, string> Data = []; internal bool Fail;
        public bool Exists(string path) => Data.ContainsKey(path); public string Read(string path) => Data[path];
        public void CreateDirectory(string path) { }
        public void WriteNew(string path, string text, Action created) { Data.Add(path, text); created(); }
        public void Replace(string a, string b) { if (Fail) throw new IOException(); Data[b] = Data[a]; Data.Remove(a); }
        public void MoveNew(string a, string b) { if (Fail) throw new IOException(); Data.Add(b, Data[a]); Data.Remove(a); }
        public void DeleteOwn(string path) => Data.Remove(path);
    }
    private sealed class StartupRegistry : IStartupRegistry
    {
        internal int Reads, Writes; internal bool Fail; internal StartupValue Value = new(null);
        public StartupValue Read() { Reads++; return Value; }
        public bool TryChange(StartupValue expected, string? command)
        { if (Fail || expected != Value) return false; Writes++; Value = new(command); return true; }
    }
}
