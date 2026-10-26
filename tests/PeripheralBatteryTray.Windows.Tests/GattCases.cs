using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

internal static class GattCases
{
    internal static void Run(Action<string, string, Action> run, Action<bool> check)
    {
        void Wait(Func<bool> predicate) => check(SpinWait.SpinUntil(predicate, 3000));
        run("G01", "Disconnected/unknown/no candidate never opens GATT", () =>
        {
            foreach (ConnectionState state in new[] { ConnectionState.Unknown, ConnectionState.Disconnected })
            { var f = new GattFixture(state); f.Start(); check(f.Client.Opens == 0); f.Stop(); }
            var none = new GattFixture(candidate: false); none.Start(); check(none.Client.Opens == 0); none.Stop();
        });
        run("G02", "UI queued cancellation avoids FromId and null is not AccessDenied", () =>
        {
            var actions = new Queue<Action>(); var real = new WinRtGattClient(action => actions.Enqueue(action));
            var queued = real.Open(new("invalid-never-opened"), () => false); check(actions.Count == 1 && !queued.Terminal.IsCompleted);
            queued.Cancel(); actions.Dequeue()(); check(queued.Terminal.IsCanceled);
            var f = new GattFixture(); f.Client.OpenResult = null; f.Start();
            check(f.Coordinator.Snapshot.Devices[0].Components.All(c => c.Source.LatestOutcome != ReadOutcome.AccessDenied)); f.Stop();
        });
        run("G03", "Empty cached service list gets one Uncached confirmation", () =>
        {
            var f = new GattFixture(); f.Device.ServicesValue = []; f.Start();
            check(f.Client.Log.SequenceEqual(["open", "services:Cached", "services:Uncached", "device:dispose"]));
            check(f.Coordinator.Snapshot.Devices[0].Components.Single().Source.LatestOutcome == ReadOutcome.Unsupported);
            f.Time.Advance(TimeSpan.FromHours(1)); check(f.Client.Opens == 1); f.Stop();
        });
        run("G04", "Cached discovery also requires current eligibility and remaining budget", () =>
        {
            var f = new GattFixture(); f.Client.AfterOpen = () => f.Time.Advance(TimeSpan.FromSeconds(10), false); f.Start();
            check(f.Client.Log.All(x => !x.StartsWith("services:")) && f.Device.Disposes == 1); f.Stop();
        });
        run("G05", "Service list cannot stand in for B characteristic discovery", () =>
        {
            var f = new GattFixture(); var b = f.AddService(20, 81); b.PendingChars = new(); f.Start();
            check(f.Coordinator.Snapshot.TrayVisual.MinimumPercent == 73 && f.Coordinator.Snapshot.TrayVisual.HasUnknown);
            check(f.Coordinator.Snapshot.Devices[0].Components.Any(c => c.ServiceKey.EndsWith("s:20") && c.Source.LastSuccess is null));
            b.PendingChars.Finish(new(GattStatus.Success, b.Chars)); Wait(() => f.Coordinator.ActiveReads.Count == 0); f.Stop();
        });
        run("G06", "Only one valid Read Battery Level characteristic is accepted", () =>
        {
            foreach (int count in new[] { 0, 1, 2 })
            {
                var f = new GattFixture(); f.Service.Chars = Enumerable.Range(0, count).Select(i => new FakeGattCharacteristic((ushort)(11 + i), 73, f.Client.Log)).Cast<IGattCharacteristic>().ToArray();
                f.Start(); check(f.Coordinator.Snapshot.TrayVisual.MinimumPercent == (count == 1 ? 73 : null));
                check(f.Client.Log.Count(x => x == "read:Uncached") == (count == 1 ? 1 : 0)); f.Stop();
            }
            var notify = new GattFixture(); notify.Characteristic.Readable = false; notify.Start();
            check(notify.Coordinator.Snapshot.TrayVisual.MinimumPercent is null && notify.Client.Log.All(x => !x.StartsWith("read:"))); notify.Stop();
        });
        run("G07", "Success payload must have exactly one byte", () =>
        {
            foreach (byte[]? bytes in new byte[]?[] { null, [], [73], [73, 0] })
            { var f = new GattFixture(); f.Characteristic.Bytes = bytes; f.Start();
                check(f.Coordinator.Snapshot.TrayVisual.MinimumPercent == (bytes?.Length == 1 ? 73 : null)); f.Stop(); }
        });
        run("G08", "Raw 0/100 valid, 101/255 rejected without charge inference", () =>
        {
            foreach (byte value in new byte[] { 0, 100, 101, 255 })
            { var f = new GattFixture(); f.Characteristic.Bytes = [value]; f.Start();
                check(f.Coordinator.Snapshot.TrayVisual.MinimumPercent == (value <= 100 ? (int)value : null)); f.Stop(); }
        });
        run("G09", "Failed status carrying residual bytes is not a reading", () =>
        {
            var f = new GattFixture(); f.Characteristic.Status = GattStatus.ProtocolError; f.Start();
            check(f.Coordinator.Snapshot.TrayVisual.MinimumPercent is null
                && f.Coordinator.Snapshot.Devices[0].Components.All(c => c.Source.LastSuccess is null)); f.Stop();
        });
        run("G10", "Two services preserve partial A success while B times out", () =>
        {
            var f = new GattFixture(); var b = f.AddService(20, 81); var pending = new FakeGattCall<GattResult<byte[]?>>();
            ((FakeGattCharacteristic)b.Chars[0]).Pending = pending; f.Start();
            f.Time.Advance(TimeSpan.FromSeconds(10)); check(f.Coordinator.Snapshot.TrayVisual.MinimumPercent == 73 && f.Coordinator.Snapshot.TrayVisual.HasUnknown);
            check(f.Coordinator.ActiveReads.Single().Phase == QueryPhase.Draining && f.Service.Disposes == 0 && b.Disposes == 0);
            pending.Finish(new(GattStatus.Success, [81])); Wait(() => f.Coordinator.ActiveReads.Count == 0);
            check(f.Coordinator.Snapshot.TrayVisual.MinimumPercent == 73 && f.Service.Disposes == 1 && b.Disposes == 1); f.Stop();
        });
        run("G11", "Equal values retain two identities and equal tray visual on reread", () =>
        {
            var f = new GattFixture(); f.AddService(20, 73); f.Start(); var snapshot = f.Coordinator.Snapshot;
            check(snapshot.Devices[0].Components.Count(c => !c.RemovedFromDirectory) == 2);
            f.Time.Advance(TimeSpan.FromSeconds(10)); f.Coordinator.Refresh();
            check(f.Coordinator.Snapshot.TrayVisual == snapshot.TrayVisual && f.Client.Opens == 2);
            check(f.Coordinator.Snapshot.Devices[0].Components.Where(c => !c.RemovedFromDirectory).All(c => c.Source.LastSuccess?.CapturedTimestamp == f.Time.GetTimestamp())); f.Stop();
        });
        run("G12", "Total budget never resets between native steps", () =>
        {
            var f = new GattFixture(); f.Client.AfterOpen = () => f.Time.Advance(TimeSpan.FromSeconds(9), false);
            f.Device.AfterServices = () => f.Time.Advance(TimeSpan.FromSeconds(1), false); f.Start();
            check(f.Client.Log.Count(x => x.StartsWith("services:")) == 1 && f.Client.Log.All(x => !x.StartsWith("chars:"))); f.Stop();
        });
        run("G13", "Late open and service lists are still owned and released", () =>
        {
            foreach (bool open in new[] { true, false })
            {
                var f = new GattFixture(); var openCall = new FakeGattCall<IGattDevice?>(); var servicesCall = new FakeGattCall<GattResult<IReadOnlyList<IGattService>>>();
                if (open) f.Client.Pending = openCall; else f.Device.Pending = servicesCall;
                f.Start(); f.Time.Advance(TimeSpan.FromSeconds(10)); check(f.Coordinator.ActiveReads.Single().Phase == QueryPhase.Draining && f.Device.Disposes == 0);
                if (open) openCall.Finish(f.Device); else servicesCall.Finish(new(GattStatus.Success, f.Device.ServicesValue));
                Wait(() => f.Coordinator.ActiveReads.Count == 0); check(f.Device.Disposes == 1 && f.Coordinator.Snapshot.TrayVisual.MinimumPercent is null);
                if (!open) check(f.Service.Disposes == 1); f.Stop();
            }
        });
        run("G14", "Never-terminal original prevents refresh flood and busy timer", () =>
        {
            var f = new GattFixture(); var pending = new FakeGattCall<IGattDevice?>(); f.Client.Pending = pending; f.Start();
            for (int i = 0; i < 100; i++) f.Coordinator.Refresh(); f.Time.Advance(TimeSpan.FromHours(2));
            check(f.Client.Opens == 1 && f.Coordinator.ActiveReads.Count == 1); int callbacks = f.Time.Callbacks;
            f.Time.Advance(TimeSpan.FromHours(2)); check(f.Time.Callbacks == callbacks);
            pending.Finish(null); Wait(() => f.Coordinator.ActiveReads.Count == 0); f.Stop();
        });
        run("G15", "In-flight services change invalidates accepted values before late B", () =>
        {
            var f = new GattFixture(); var b = f.AddService(20, 81); var pending = new FakeGattCall<GattResult<byte[]?>>(); ((FakeGattCharacteristic)b.Chars[0]).Pending = pending;
            f.Start(); check(f.Coordinator.Snapshot.TrayVisual.MinimumPercent == 73); f.Device.ChangeServices();
            check(f.Coordinator.Snapshot.TrayVisual.MinimumPercent is null && f.Coordinator.Snapshot.Devices[0].Binding.GattDatabaseEpoch == 2);
            pending.Finish(new(GattStatus.Success, [81])); Wait(() => f.Coordinator.ActiveReads.Count == 0); check(f.Coordinator.Snapshot.TrayVisual.MinimumPercent is null); f.Stop();
        });
        run("G16", "Directory structure change advances database and cannot publish on old stamp", () =>
        {
            var f = new GattFixture(); f.Start(); f.Time.Advance(TimeSpan.FromSeconds(10)); f.Service.HandleValue = 15; f.Coordinator.Refresh();
            Wait(() => f.Coordinator.ActiveReads.Count == 0); check(f.Coordinator.Snapshot.Devices[0].Binding.GattDatabaseEpoch == 2 && f.Coordinator.Snapshot.TrayVisual.MinimumPercent is null);
            f.Time.Advance(TimeSpan.FromSeconds(10)); Wait(() => f.Client.Opens == 3 && f.Coordinator.ActiveReads.Count == 0);
            check(f.Coordinator.Snapshot.TrayVisual.MinimumPercent == 73); f.Stop();
        });
        run("G17", "Per-service denial does not pause a successful service", () =>
        {
            var f = new GattFixture(); var b = f.AddService(20, 81); b.Status = GattStatus.AccessDenied; f.Start();
            check(f.Coordinator.Snapshot.TrayVisual.MinimumPercent == 73);
            f.Time.Advance(TimeSpan.FromMinutes(5)); check(f.Client.Opens == 2 && f.Client.Log.Count(x => x == "chars:20:Cached") == 1); f.Stop();
            var denied = new GattFixture(); denied.Device.Status = GattStatus.AccessDenied; denied.Start(); denied.Time.Advance(TimeSpan.FromHours(1));
            check(denied.Client.Opens == 1); denied.Stop();
        });
        run("G18", "Status and cancellation classification preserve ownership", () =>
        {
            foreach (GattStatus status in new[] { GattStatus.Unreachable, GattStatus.ProtocolError, GattStatus.AccessDenied })
            { var f = new GattFixture(); f.Characteristic.Status = status; f.Start();
                check(f.Coordinator.Snapshot.Devices[0].Components.Where(c => !c.RemovedFromDirectory).Single().Source.LatestOutcome
                    == (status == GattStatus.AccessDenied ? ReadOutcome.AccessDenied : ReadOutcome.Error)); f.Stop(); }
            var cancel = new GattFixture(); var pending = new FakeGattCall<IGattDevice?>(); cancel.Client.Pending = pending; cancel.Start();
            var stop = cancel.Coordinator.StopAsync(); pending.Fail(new OperationCanceledException()); Wait(() => stop.IsCompleted); check(stop.Result.FullyStopped);
        });
        run("G19", "Protocol failure schedules one future Uncached directory recheck", () =>
        {
            var f = new GattFixture(); f.Characteristic.Status = GattStatus.ProtocolError; f.Start(); check(f.Client.Opens == 1);
            f.Time.Advance(TimeSpan.FromSeconds(10)); f.Coordinator.Refresh();
            check(f.Client.Log.Count(x => x == "services:Uncached") == 1 && f.Client.Opens == 2); f.Stop();
        });
        run("G20", "All services owned at 0/2/8/9 resource boundaries", () =>
        {
            foreach (int count in new[] { 0, 2, 8, 9 })
            {
                var f = new GattFixture(); var services = Enumerable.Range(0, count).Select(i => new FakeGattService((ushort)(10 + 10 * i), (byte)(73 + i), f.Client.Log)).ToArray();
                f.Device.ServicesValue = services; f.Start(); check(services.All(s => s.Disposes == 1) && f.Device.Disposes == 1);
                check(f.Client.Log.Count(x => x == "read:Uncached") == (count <= 8 ? count : 0)); f.Stop();
            }
        });
        run("G21", "Cleanup failures do not skip other owners; repeated object disposed once", () =>
        {
            var f = new GattFixture(); f.Service.ThrowDispose = true; var b = f.AddService(20, 81); f.Start();
            var result = f.Coordinator.StopAsync().GetAwaiter().GetResult(); check(result.CleanupErrors == 1 && f.Service.Disposes == 1 && b.Disposes == 1 && f.Device.Disposes == 1);
            var dup = new GattFixture(); dup.Device.ServicesValue = [dup.Service, dup.Service]; dup.Start(); check(dup.Service.Disposes == 1); dup.Stop();
        });
        run("G22", "Listeners released between queries; no service object cache", () =>
        {
            var f = new GattFixture(); f.Start(); check(f.Device.ListenerCount == 0 && f.Service.Disposes == 1); long epoch = f.Coordinator.Snapshot.Devices[0].Binding.GattDatabaseEpoch;
            f.Device.ChangeServices(); check(f.Coordinator.Snapshot.Devices[0].Binding.GattDatabaseEpoch == epoch);
            f.Time.Advance(TimeSpan.FromSeconds(10)); f.Coordinator.Refresh(); check(f.Client.Opens == 2 && f.Service.Disposes == 2 && f.Device.ListenerCount == 0); f.Stop();
        });
        run("G23", "Budgeted shutdown observes late exception and prevents UI publish", () =>
        {
            var f = new GattFixture(); var pending = new FakeGattCall<IGattDevice?>(); f.Client.Pending = pending;
            f.Start(); int callbacks = 0; f.Coordinator.SnapshotChanged += _ => callbacks++;
            var stop = f.Coordinator.StopAsync(); f.Time.Advance(TimeSpan.FromSeconds(2)); Wait(() => stop.IsCompleted);
            check(!stop.Result.FullyStopped); pending.Fail(new IOException("private identity must never be exported"));
            Wait(() => f.Coordinator.ActiveReads.Count == 0); check(callbacks == 0);
        });
    }
}
internal sealed class GattFixture
{
    public SchedulerTime Time = new();
    public FakeGattClient Client = new();
    public FakeGattDevice Device;
    public FakeGattService Service;
    public FakeGattCharacteristic Characteristic => (FakeGattCharacteristic)Service.Chars[0];
    public BatteryCoordinator Coordinator;
    private BatteryDeviceState _model;
    private bool _candidate;
    private readonly BindingRegistry _registry = new();
    public GattFixture(ConnectionState connection = ConnectionState.Connected, bool candidate = true)
    {
        _candidate = candidate; Service = new(10, 73, Client.Log); Device = new(Client.Log) { ServicesValue = [Service] };
        Client.OpenResult = Device;
        var binding = new DeviceIdentityResolver().Resolve([new(new("fake-private-endpoint"), TransportKind.Ble, "Private name", true,
            connection, null, candidate ? (byte)90 : null)], [])[0];
        _model = binding.Device; _registry.Replace([binding]);
        var telemetry = new DiagnosticTelemetry(Time);
        Coordinator = new(BatteryMode.Real, [new BleGattProvider(_registry, new ObservedGattClient(Client, telemetry), Time)], Time, telemetry: telemetry);
    }
    public void Rebind(BatteryDeviceState model)
    {
        var old = _registry.Snapshot().Single();
        _registry.Replace([new ResolvedBinding(model, old.Endpoints, old.Nodes)]);
        Coordinator.UpsertDevice(model, true);
    }
    public FakeGattService AddService(ushort handle, byte percent)
    { var service = new FakeGattService(handle, percent, Client.Log); Device.ServicesValue = Device.ServicesValue.Append(service).ToArray(); return service; }
    public void Start()
    {
        Coordinator.UpsertDevice(_model, _candidate);
        bool pending = Client.Pending is not null || Device.Pending is not null || Device.ServicesValue.OfType<FakeGattService>()
            .Any(s => s.PendingChars is not null || s.Chars.OfType<FakeGattCharacteristic>().Any(c => c.Pending is not null));
        if (!pending && !SpinWait.SpinUntil(() => Coordinator.ActiveReads.Count == 0, 3000)) throw new TimeoutException("Fake terminal was not observed.");
    }
    public void Stop() => Coordinator.StopAsync().GetAwaiter().GetResult();
}
internal sealed class FakeGattCall<T> : IGattCall<T>
{
    private readonly TaskCompletionSource<T> _terminal = new();
    public Task<T> Terminal => _terminal.Task;
    public int Cancels;
    public void Cancel() => Interlocked.Increment(ref Cancels);
    public void Finish(T value) => _terminal.TrySetResult(value);
    public void Fail(Exception error) => _terminal.TrySetException(error);
    public static FakeGattCall<T> Completed(T value) { var call = new FakeGattCall<T>(); call.Finish(value); return call; }
}
internal sealed class FakeGattClient : IGattClient
{
    public List<string> Log = [];
    public int Opens;
    public IGattDevice? OpenResult;
    public FakeGattCall<IGattDevice?>? Pending;
    public Action? AfterOpen;
    public IGattCall<IGattDevice?> Open(AepId id, Func<bool> mayStart)
    { if (!mayStart()) throw new OperationCanceledException(); Opens++; Log.Add("open"); AfterOpen?.Invoke(); return Pending ?? FakeGattCall<IGattDevice?>.Completed(OpenResult); }
}
internal sealed class FakeGattDevice(List<string> log) : IGattDevice
{
    public ConnectionState Connection { get; set; } = ConnectionState.Connected;
    private Action? _services, _connection;
    public int ListenerCount => (_services?.GetInvocationList().Length ?? 0) + (_connection?.GetInvocationList().Length ?? 0);
    public event Action? ServicesChanged { add => _services += value; remove => _services -= value; }
    public event Action? ConnectionChanged { add => _connection += value; remove => _connection -= value; }
    public IReadOnlyList<IGattService> ServicesValue = [];
    public GattStatus Status;
    public FakeGattCall<GattResult<IReadOnlyList<IGattService>>>? Pending;
    public Action? AfterServices;
    public int Disposes;
    public void ChangeServices() => _services?.Invoke();
    public void Disconnect() { Connection = ConnectionState.Disconnected; _connection?.Invoke(); }
    public IGattCall<GattResult<IReadOnlyList<IGattService>>> Services(Guid uuid, GattCacheMode cache)
    { if (uuid != BatteryGattUuids.Service) throw new InvalidOperationException("Wrong UUID"); log.Add("services:" + cache); AfterServices?.Invoke();
        return Pending ?? FakeGattCall<GattResult<IReadOnlyList<IGattService>>>.Completed(new(Status, ServicesValue)); }
    public void Dispose() { Disposes++; log.Add("device:dispose"); }
}
internal sealed class FakeGattService(ushort handle, byte percent, List<string> log) : IGattService
{
    public object Identity => this;
    public ushort HandleValue = handle;
    public ushort Handle => HandleValue;
    public IReadOnlyList<IGattCharacteristic> Chars = [new FakeGattCharacteristic((ushort)(handle + 1), percent, log)];
    public GattStatus Status;
    public int Disposes;
    public bool ThrowDispose;
    public FakeGattCall<GattResult<IReadOnlyList<IGattCharacteristic>>>? PendingChars;
    public IGattCall<GattResult<IReadOnlyList<IGattCharacteristic>>> Characteristics(Guid uuid, GattCacheMode cache)
    { if (uuid != BatteryGattUuids.Level) throw new InvalidOperationException("Wrong UUID"); log.Add($"chars:{Handle}:{cache}");
        return PendingChars ?? FakeGattCall<GattResult<IReadOnlyList<IGattCharacteristic>>>.Completed(new(Status, Chars)); }
    public void Dispose() { Disposes++; log.Add("service:dispose"); if (ThrowDispose) throw new IOException("Fake cleanup error."); }
}
internal sealed class FakeGattCharacteristic(ushort handle, byte percent, List<string> log) : IGattCharacteristic
{
    public ushort Handle => handle;
    public bool Readable = true;
    public bool CanRead => Readable;
    public byte[]? Bytes = [percent];
    public GattStatus Status;
    public FakeGattCall<GattResult<byte[]?>>? Pending;
    public IGattCall<GattResult<byte[]?>> Read(GattCacheMode cache)
    { if (cache != GattCacheMode.Uncached) throw new InvalidOperationException("Wrong read mode"); log.Add("read:" + cache);
        return Pending ?? FakeGattCall<GattResult<byte[]?>>.Completed(new(Status, Bytes)); }
}
