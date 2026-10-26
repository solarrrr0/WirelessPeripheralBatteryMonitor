using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

internal static class Program
{
    private static readonly List<object> Checks = [];
    private static int _assertions, _failures;
    private static void Check(bool condition) { _assertions++; if (!condition) throw new InvalidOperationException($"Assertion {_assertions} failed."); }
    private static void Wait(Func<bool> condition) => Check(SpinWait.SpinUntil(condition, 3000));
    private static void Run(string id, string name, Action body)
    {
        int count = _assertions;
        try { body(); Checks.Add(new { Id = id, Name = name, Passed = true, Assertions = _assertions - count }); Console.WriteLine($"PASS {id} {name}"); }
        catch (Exception e) { _failures++; Checks.Add(new { Id = id, Name = name, Passed = false, Error = e.Message }); Console.WriteLine($"FAIL {id} {e}"); }
    }
    private static Endpoint Ep(string id, Guid? container = null, TransportKind transport = TransportKind.Ble)
        => new(new(id), transport, "Identical name", true, ConnectionState.Disconnected, container is null ? null : new(container.Value));
    private static PnpNode Node(string id, string? aep, Guid? container, string? parent = null, bool? local = false)
        => new(new(id), aep is null ? null : new(aep), container is null ? null : new(container.Value), parent is null ? null : new(parent), local);
    private static int Main(string[] args)
    {
        Run("W01", "Only paired Classic/BLE watcher factories are requested", () =>
        { var f = new FakeWatchers(); var d = new BluetoothDiscovery(f, new SchedulerTime()); d.Start();
            Check(f.Requests.Count == 2 && f.Requests.All(r => r.Paired) && f.Requests.Select(r => r.Kind).Distinct().Count() == 2);
            f.AutoStop = true; d.StopAsync().GetAwaiter().GetResult(); });
        Run("W02", "Property deltas preserve values and null means unknown", () =>
        { var f = new FakeWatchers(); var d = new BluetoothDiscovery(f, new SchedulerTime()); d.Start(); var w = f.Watchers[1];
            w.Add("ep", true); w.Emit(EndpointEventKind.Updated, "ep", (BluetoothDiscovery.Name, "Renamed"));
            Check(d.Snapshot.Endpoints[0].Connection == ConnectionState.Connected && d.Snapshot.Endpoints[0].Name == "Renamed");
            w.Emit(EndpointEventKind.Updated, "ep", (BluetoothDiscovery.Connected, null)); Check(d.Snapshot.Endpoints[0].Connection == ConnectionState.Unknown);
            w.Emit(EndpointEventKind.Updated, "missing", (BluetoothDiscovery.Connected, true)); Check(d.Snapshot.Endpoints.Count == 1 && d.Snapshot.ResyncRequests == 1);
            f.AutoStop = true; d.StopAsync().GetAwaiter().GetResult(); });
        Run("W03", "Incomplete BLE enumeration does not replace partial list with empty", () =>
        { var t = new SchedulerTime(); var f = new FakeWatchers(); var d = new BluetoothDiscovery(f, t); d.Start(); f.Watchers[1].Add("ep", false);
            f.Watchers[0].Emit(EndpointEventKind.EnumerationCompleted); t.Advance(TimeSpan.FromSeconds(10));
            Check(d.Snapshot.ClassicStatus == DiscoveryStatus.Completed && d.Snapshot.BleStatus == DiscoveryStatus.Incomplete && d.Snapshot.Endpoints.Count == 1);
            Check(f.Watchers.Count == 2); f.AutoStop = true; d.StopAsync().GetAwaiter().GetResult(); });
        Run("W04", "Removal and aborted watcher invalidate connection evidence", () =>
        { var f = new FakeWatchers(); var d = new BluetoothDiscovery(f, new SchedulerTime()); d.Start(); f.Watchers[1].Add("ep", true);
            f.Watchers[1].Emit(EndpointEventKind.Removed, "ep"); Check(d.Snapshot.Endpoints[0].Connection == ConnectionState.Unknown && d.Snapshot.Endpoints[0].Paired);
            f.Watchers[1].Emit(EndpointEventKind.Aborted); Check(d.Snapshot.BleStatus == DiscoveryStatus.Failed);
            f.AutoStop = true; d.StopAsync().GetAwaiter().GetResult(); });
        Run("W05", "Stopped terminal remains observed after Stop returns", () =>
        { var f = new FakeWatchers(); var d = new BluetoothDiscovery(f, new SchedulerTime()); int updates = 0; d.Changed += _ => updates++; d.Start();
            Task stop = d.StopAsync(); Check(!stop.IsCompleted && ReferenceEquals(stop, d.StopAsync())); d.Start(); Check(f.Watchers.Count == 2);
            f.Watchers[0].Add("late", true); Check(updates == 0);
            foreach (var w in f.Watchers) w.Emit(EndpointEventKind.Stopped); Wait(() => stop.IsCompleted); Check(f.Watchers.All(w => w.StopCalls == 1)); });
        Run("W06", "Old watcher callbacks cannot contaminate retry generation", () =>
        { var t = new SchedulerTime(); var f = new FakeWatchers(); var d = new BluetoothDiscovery(f, t); d.Start();
            Action<EndpointEvent>? old = f.Watchers[1].Handlers; f.Watchers[1].Emit(EndpointEventKind.Aborted);
            t.Advance(TimeSpan.FromMinutes(1)); Check(f.Watchers.Count == 3); f.Watchers[2].Add("new", true);
            old?.Invoke(new(EndpointEventKind.Removed, new("new"), new Dictionary<string, object?>()));
            Check(d.Snapshot.Endpoints.Single().Present); f.AutoStop = true; d.StopAsync().GetAwaiter().GetResult(); });
        Run("W07", "AepId bridge requires exact ordinal identity", () =>
        { var g = Guid.NewGuid(); var r = new DeviceIdentityResolver(); var eps = new[] { Ep("AEP") };
            Check(r.Resolve(eps, [Node("node", "aep", g)])[0].Nodes.Count == 0);
            Check(r.Resolve(eps, [Node("node", "AEP", g)])[0].Nodes.Count == 1); });
        Run("W08", "AEP and PnP container evidence is never interchanged", () =>
        { var g = Guid.NewGuid(); var h = Guid.NewGuid(); var r = new DeviceIdentityResolver();
            Check(r.Resolve([Ep("one", g), Ep("two", h)], [Node("n1", "one", h), Node("n2", "two", g)]).Count == 2);
            var conflict = r.Resolve([Ep("one", g), Ep("two", g)], [Node("n1", "one", g), Node("n2", "two", h)]);
            Check(conflict.Count == 2); });
        Run("W09", "Names do not merge; consistent typed container merges transports", () =>
        { var r = new DeviceIdentityResolver(); Check(r.Resolve([Ep("one"), Ep("two")], []).Count == 2);
            var g = Guid.NewGuid(); var merged = r.Resolve([Ep("one", g), Ep("two", g, TransportKind.Classic)], []);
            Check(merged.Count == 1 && merged[0].Device.Binding.Classic is not null && merged[0].Device.Binding.Ble is not null);
            var rename = merged[0].Device.Binding; var renamed = r.Resolve([Ep("one", g) with { Name = "Other" }, Ep("two", g, TransportKind.Classic)], [])[0];
            Check(rename == renamed.Device.Binding); });
        Run("W10", "Parent traversal bounded; conflicts split without copying battery history", () =>
        { var g = Guid.NewGuid(); var nodes = Enumerable.Range(0, 12).Select(i => Node(i.ToString(), null, g, (i + 1).ToString())).ToArray();
            Check(DeviceIdentityResolver.BoundedParents(nodes[0], nodes).Count == 8);
            Check(DeviceIdentityResolver.BoundedParents(nodes[0], [nodes[0], nodes[1] with { Parent = nodes[0].Id }]).Count == 1);
            Check(DeviceIdentityResolver.BoundedParents(nodes[0], [nodes[0], nodes[1] with { InLocalMachineContainer = true }]).Count == 0);
            var r = new DeviceIdentityResolver(); var merged = r.Resolve([Ep("a", g), Ep("b", g)], []);
            var split = r.Resolve([Ep("a", g), Ep("b", Guid.NewGuid())], []);
            Check(merged.Count == 1 && split.Count == 2 && split.All(x => x.Device.Components.Count == 0)); });
        Run("W11", "Standard Byte only, unknown values never become direct data", () =>
        { foreach (byte b in new byte[] { 0, 100, 101, 255 })
            { var value = PnpPropertyReader.DecodeBattery(new(PropertyStatus.Success, 3, [b]), false); Check((value is not null) == (b <= 100)); }
            Check(PnpPropertyReader.DecodeBattery(new(PropertyStatus.Success, 7, [50, 0, 0, 0]), false) is null); });
        Run("W12", "Compatibility decoding rejects strings modifiers arrays truncation", () =>
        { Check(PnpPropertyReader.DecodeBattery(new(PropertyStatus.Success, 3, [90]), true)?.Percent == 90);
            Check(PnpPropertyReader.DecodeBattery(new(PropertyStatus.Success, 7, [100, 0, 0, 0]), true)?.Percent == 100);
            foreach (var raw in new[] { new RawProperty(PropertyStatus.Success, 7, [255, 0, 0, 0]), new(PropertyStatus.Success, 7, [90]),
                new(PropertyStatus.Success, 3 | 0x1000, [90]), new(PropertyStatus.Success, 18, Encoding.Unicode.GetBytes("90\0")), new(PropertyStatus.Success, 17, [1]) })
                Check(PnpPropertyReader.DecodeBattery(raw, true) is null);
            Check(PnpPropertyReader.DecodeString(new(PropertyStatus.Success, 18, [65, 0])) is null);
            Check(PnpPropertyReader.DecodeString(new(PropertyStatus.Success, 18, [0, 216, 0, 0])) is null); });
        Run("W13", "Two property sources retain conflicts; pid7 is never requested", () =>
        { var t = new SchedulerTime(); var native = new FakeNative { Battery = 90 }; var registry = new BindingRegistry();
            var resolver = new DeviceIdentityResolver(); var g = Guid.NewGuid(); var binding = resolver.Resolve([Ep("a") with { Battery = 100 }], [Node("node", "a", g)])[0];
            registry.Replace([binding]); var provider = new WindowsBatteryProvider(registry, new(native), new(), t);
            var c = new BatteryCoordinator(BatteryMode.Real, [provider], t); c.UpsertDevice(binding.Device);
            Wait(() => c.ActiveReads.Count == 0); Check(c.Snapshot.TrayVisual.MinimumPercent is null);
            Check(BatteryRules.Present(c.Snapshot.Devices[0], BatteryMode.Real, t, TimeSpan.FromMinutes(5)).SystemPercents.SequenceEqual([90, 100]));
            Check(native.Requested.All(k => k == BatteryPropertyKeys.Standard || k == BatteryPropertyKeys.Compatibility));
            c.StopAsync().GetAwaiter().GetResult(); });
        Run("W14", "Property buffers grow with bounded retries and 64KiB cap", () =>
        { var n = new FakeNative { ResizeEveryCall = true }; var r = new PnpPropertyReader(n);
            Check(r.Read(1, BatteryPropertyKeys.Standard).Status == PropertyStatus.Error && n.Calls == 4);
            n = new FakeNative { InitialLength = 65537 }; Check(new PnpPropertyReader(n).Read(1, BatteryPropertyKeys.Standard).Status == PropertyStatus.TooLarge && n.Calls == 1); });
        Run("W15", "CONFIGRET classification remains independent of Win32 last error", () =>
        { System.Runtime.InteropServices.Marshal.SetLastPInvokeError(999);
            Check(PnpPropertyReader.Classify(0x25) == PropertyStatus.Missing && PnpPropertyReader.Classify(0x33) == PropertyStatus.AccessDenied
                && PnpPropertyReader.Classify(0x0d) == PropertyStatus.NodeGone && PnpPropertyReader.Classify(999) == PropertyStatus.Error); });
        Run("W16", "Timed out synchronous metadata retains shared lane until native return", () =>
        { var t = new SchedulerTime(); var lane = new MetadataLane(); var gate = new ManualResetEventSlim(); var entered = new ManualResetEventSlim();
            var n = new FakeNative { Block = () => { entered.Set(); gate.Wait(); } }; var registry = new BindingRegistry(); var resolver = new DeviceIdentityResolver();
            var binding = resolver.Resolve([Ep("a")], [Node("node", "a", Guid.NewGuid())])[0]; registry.Replace([binding]);
            var c = new BatteryCoordinator(BatteryMode.Real, [new WindowsBatteryProvider(registry, new(n), lane, t)], t);
            try { c.UpsertDevice(binding.Device); Check(entered.Wait(3000)); t.Advance(TimeSpan.FromSeconds(2));
                for (int i = 0; i < 100; i++) c.Refresh(); Check(lane.IsBusy && c.ActiveReads.Single().Phase == QueryPhase.Draining && n.Calls == 1);
            } finally { gate.Set(); }
            Wait(() => !lane.IsBusy); Wait(() => c.ActiveReads.Count == 0); Check(c.Snapshot.TrayVisual.MinimumPercent is null); c.StopAsync().GetAwaiter().GetResult(); });
        Run("W17", "Offline nodes need exact paired bridge; unassociated caches excluded", () =>
        { var g = Guid.NewGuid(); var resolver = new DeviceIdentityResolver(); var result = resolver.Resolve([Ep("a")], [Node("node", "a", g), Node("orphan", null, Guid.NewGuid()), Node("host", "a", g, local: true)]);
            Check(result[0].Nodes.Count == 1 && result[0].Device.Binding.Ble?.Connection == ConnectionState.Disconnected); });
        Run("W18", "Index rebuild storms coalesce and preserve 30s start interval", () =>
        { var t = new SchedulerTime(); var n = new FakeNative(); var lane = new MetadataLane(); var index = new PnpIndexService(new(n), lane, t);
            index.RequestRebuild(); Wait(() => index.Snapshot.Complete); Check(n.Enumerations == 4);
            for (int i = 0; i < 100; i++) index.RequestRebuild(); t.Advance(TimeSpan.FromSeconds(29)); Check(n.Enumerations == 4);
            t.Advance(TimeSpan.FromSeconds(1)); Wait(() => n.Enumerations == 8 && index.Snapshot.Version >= 2);
            index.DisposeAsync().AsTask().GetAwaiter().GetResult(); });
        Run("W19", "Windows provider publication is one-way and cannot trigger GATT", () =>
        { var t = new SchedulerTime(); var n = new FakeNative(); var registry = new BindingRegistry(); var binding = new DeviceIdentityResolver().Resolve([Ep("a") with { Battery = 90 }], [])[0];
            registry.Replace([binding]); var c = new BatteryCoordinator(BatteryMode.Real, [new WindowsBatteryProvider(registry, new(n), new(), t)], t);
            c.UpsertDevice(binding.Device); Wait(() => c.ActiveReads.Count == 0); Check(n.Calls == 0 && c.Snapshot.Devices[0].SystemCandidates.Count == 1);
            t.Advance(TimeSpan.FromSeconds(2)); Check(c.ActiveReads.Count == 0); c.StopAsync().GetAwaiter().GetResult(); });
        Run("W20", "Watcher errors isolate transport and stop prevents publication", () =>
        { var f = new FakeWatchers(); var d = new BluetoothDiscovery(f, new SchedulerTime()); int notified = 0; d.Changed += _ => notified++;
            var conflict = ConnectionEvidence.Combine(ConnectionState.Connected, ConnectionState.Disconnected);
            Check(conflict.State == ConnectionState.Unknown && conflict.NeedsResync);
            d.Start(); f.Watchers[0].Add("classic", true); f.Watchers[1].Add("ble", false); f.Watchers[1].Emit(EndpointEventKind.Aborted);
            Check(d.Snapshot.Endpoints.Single(e => e.Id.Value == "classic").Connection == ConnectionState.Connected);
            int before = notified; f.AutoStop = true; d.StopAsync().GetAwaiter().GetResult(); f.Watchers[0].Add("late", true); Check(notified == before); });
        Run("W21", "Invalid and future settings preserve original with default", () =>
        { var files = new FakeSettingsFiles(); var path = Path.GetFullPath("config.test.json");
            Check(new PollingSettingsStore(path, files).Current.Minutes == 5 && files.Data.Count == 0);
            foreach (string text in new[] { "bad", "{\"schemaVersion\":5,\"pollIntervalMinutes\":1}", "{\"schemaVersion\":1,\"pollIntervalMinutes\":0}",
                "{\"schemaVersion\":1,\"pollIntervalMinutes\":\"5\"}", "{\"schemaVersion\":1,\"pollIntervalMinutes\":5.0}", "{\"schemaVersion\":1,\"schemaVersion\":1,\"pollIntervalMinutes\":5}" })
            { files.Data[path] = text; var store = new PollingSettingsStore(path, files); Check(store.Current.Minutes == 5 && !store.Current.CanSave && !store.TrySave(10) && files.Data[path] == text); } });
        Run("W22", "Save creates or atomically replaces only self-owned temporary files", () =>
        { var f = new FakeSettingsFiles(); string path = Path.GetFullPath("config.test.json"); var s = new PollingSettingsStore(path, f);
            Check(s.TrySave(1) && f.Moves == 1 && s.Current.Minutes == 1); Check(s.TrySave(30) && f.Replaces == 1);
            string original = f.Data[path]; f.FailReplace = true; Check(!s.TrySave(5) && s.Current.Minutes == 30 && f.Data[path] == original);
            Check(f.Data.Count == 1 && f.Temps.All(p => Path.GetDirectoryName(p) == Path.GetDirectoryName(path)));
            f.FailCreateBeforeOwnership = true; int deleted = f.Deletes; Check(!s.TrySave(2) && f.Deletes == deleted); });
        Run("F01", "Real filesystem create/replace/reload and read-only protection, including inherited EFS when present", () =>
        {
            var roots = new List<string> { Path.GetTempPath() };
            string userDirectory = Path.GetDirectoryName(PollingSettingsStore.UserConfigPath)!;
            if (Directory.Exists(userDirectory) && (File.GetAttributes(userDirectory) & FileAttributes.Encrypted) != 0)
                roots.Add(userDirectory);
            foreach (string root in roots)
            {
                string directory = Path.Combine(root, ".PeripheralBatteryTray-test-" + Guid.NewGuid().ToString("N"));
                string path = Path.Combine(directory, "config.json");
                try
                {
                    var s = new PollingSettingsStore(path); Check(!Directory.Exists(directory));
                    Check(s.TrySave(1) && new PollingSettingsStore(path).Current.Minutes == 1);
                    var attributes = File.GetAttributes(path);
                    Check(s.TrySave(30) && new PollingSettingsStore(path).Current.Minutes == 30);
                    Check((File.GetAttributes(path) & FileAttributes.Encrypted) == (attributes & FileAttributes.Encrypted));
                    Check(s.TrySave(5, true, 5) && new PollingSettingsStore(path).Current.AtkAutoEnabled);
                    string original = File.ReadAllText(path);
                    File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                    Check(!s.TrySave(7, false, 5) && s.Current.AtkAutoEnabled && s.Current.Minutes == 5 && File.ReadAllText(path) == original);
                    File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                    Check(Directory.GetFiles(directory).Length == 1);
                }
                finally
                {
                    if (File.Exists(path)) { File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly); File.Delete(path); }
                    if (Directory.Exists(directory)) Directory.Delete(directory, false);
                }
            }
        });
        Run("W23", "Serial saves preserve unknown fields and do not schedule device I/O", () =>
        { var f = new FakeSettingsFiles(); string path = Path.GetFullPath("config.test.json"); f.Data[path] = "{\"schemaVersion\":1,\"pollIntervalMinutes\":5,\"future\":{\"keep\":[1,true]}}";
            var s = new PollingSettingsStore(path, f); var c = new BatteryCoordinator(BatteryMode.Real, [], new SchedulerTime());
            foreach (int minutes in new[] { 1, 30, 7 }) { Check(s.TrySave(minutes)); c.SetRefreshMinutes(s.Current.Minutes); }
            using var json = JsonDocument.Parse(f.Data[path]); Check(json.RootElement.GetProperty("future").GetProperty("keep")[1].GetBoolean()
                && s.Current.Minutes == 7 && c.RefreshPeriod == TimeSpan.FromMinutes(7) && c.ActiveReads.Count == 0); c.StopAsync().GetAwaiter().GetResult(); });
        GattCases.Run(Run, Check);
        PlatformIntegrationCases.Run(Run, Check);
        AtkCases.Run(Run, Check);
        string? reportPath = args.Length == 2 && args[0] == "--report" ? args[1] : null;
        if (reportPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { TimestampUtc = DateTimeOffset.UtcNow, Passed = _failures == 0,
                BuildId = typeof(Program).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "BuildId").Value,
                Runtime = Environment.Version.ToString(), Scope = "Fake discovery/native metadata and isolated settings only. No real devices, GATT or Computer Use.",
                AssertionCount = _assertions, Checks, Assemblies = new[] { typeof(Program).Assembly.Location, typeof(PnpNative).Assembly.Location, typeof(BatteryRules).Assembly.Location }
                    .Select(p => new { File = Path.GetFileName(p), Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) }) }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"Windows: {Checks.Count - _failures}/{Checks.Count}, {_assertions} assertions."); return _failures == 0 ? 0 : 1;
    }

}
internal sealed class FakeWatchers : IEndpointWatcherFactory
{
    public bool AutoStop;
    public List<(TransportKind Kind, bool Paired)> Requests = [];
    public List<FakeWatcher> Watchers = [];
    public IEndpointWatcher Create(TransportKind transport, bool pairedOnly)
    { Requests.Add((transport, pairedOnly)); var watcher = new FakeWatcher(this); Watchers.Add(watcher); return watcher; }
}
internal sealed class FakeWatcher(FakeWatchers factory) : IEndpointWatcher
{
    public Action<EndpointEvent>? Handlers;
    public int StopCalls;
    public event Action<EndpointEvent>? Changed { add => Handlers += value; remove => Handlers -= value; }
    public void Start() { }
    public void Stop() { StopCalls++; if (factory.AutoStop) Emit(EndpointEventKind.Stopped); }
    public void Emit(EndpointEventKind kind, string id = "", params (string Key, object? Value)[] properties)
        => Handlers?.Invoke(new(kind, new(id), properties.ToDictionary(p => p.Key, p => p.Value)));
    public void Add(string id, bool connected) => Emit(EndpointEventKind.Added, id,
        (BluetoothDiscovery.Name, "Test device"), (BluetoothDiscovery.Paired, true), (BluetoothDiscovery.Connected, connected));
}
internal sealed class FakeNative : IPnpNative
{
    public int Calls, Enumerations;
    public byte Battery = 90;
    public uint InitialLength = 1;
    public bool ResizeEveryCall;
    public Action? Block;
    public List<PropertyKey> Requested = [];
    public IReadOnlyList<(DevnodeId Id, uint DevInst)> Enumerate(string enumerator, CancellationToken cancellation)
    { Interlocked.Increment(ref Enumerations); return []; }
    public uint Locate(DevnodeId id, out uint devInst) { devInst = 1; return 0; }
    public uint GetProperty(uint devInst, PropertyKey key, out uint type, byte[]? buffer, ref uint size)
    {
        Interlocked.Increment(ref Calls); Requested.Add(key); Block?.Invoke(); type = 3;
        if (buffer is null) { size = InitialLength; return 0x1a; }
        if (ResizeEveryCall) { size++; return 0x1a; }
        size = 1; buffer[0] = Battery; return 0;
    }
}
internal sealed class FakeSettingsFiles : ISettingsFiles
{
    public Dictionary<string, string> Data = [];
    public int Replaces, Moves, Deletes;
    public bool FailReplace, FailCreateBeforeOwnership;
    public List<string> Temps = [];
    public bool Exists(string path) => Data.ContainsKey(path);
    public string Read(string path) => Data[path];
    public void CreateDirectory(string path) { }
    public void WriteNew(string path, string text, Action created)
    { if (FailCreateBeforeOwnership) throw new IOException("Collision"); Data.Add(path, ""); created(); Temps.Add(path); Data[path] = text; }
    public void Replace(string temp, string destination) { if (FailReplace) throw new IOException("Denied"); Replaces++; Data[destination] = Data[temp]; Data.Remove(temp); }
    public void MoveNew(string temp, string destination) { Moves++; Data.Add(destination, Data[temp]); Data.Remove(temp); }
    public void DeleteOwn(string path) { Deletes++; Data.Remove(path); }
}
