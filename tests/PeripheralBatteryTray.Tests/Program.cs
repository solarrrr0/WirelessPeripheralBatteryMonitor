using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.Tests;

internal static class Program
{
    private static readonly List<object> Checks = [];
    private static int _failures;
    private static int _assertions;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(5);

    private static int Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) return 3;
        Run("C01", "Validated mutually exclusive percentage and band", () =>
        {
            foreach (int value in new[] { 0, 100 }) Assert(BatteryValue.FromPercent(value).Percent == value);
            foreach (int value in new[] { -1, 101, 255 })
            {
                Assert(!BatteryValue.TryCreate(value, null, out _));
                Throws<ArgumentOutOfRangeException>(() => BatteryValue.FromPercent(value));
            }
            Assert(!BatteryValue.TryCreate(70, BatteryBand.High, out _));
            Assert(!BatteryValue.TryCreate(null, null, out _));
            Assert(!BatteryValue.TryCreate(null, (BatteryBand)99, out _));
            Assert(BatteryValue.FromBand(BatteryBand.High).Percent is null);
            var time = new ManualTime();
            var binding = Binding();
            foreach (int value in new[] { 0, 100 })
                Assert(Tray(Device(binding, Component(binding, time, value)), time).MinimumPercent == value);
            Assert(Tray(Device(binding, Component(binding, time, 73) with
                { Source = Component(binding, time, 73).Source with
                    { LastSuccess = new(BatteryValue.FromBand(BatteryBand.High), ReadingSource.GattUncached,
                        binding.Stamp(ReadingSource.GattUncached, 1), time.GetUtcNow(), time.GetTimestamp()) } }), time).MinimumPercent is null);
        });
        Run("C02", "Repeated Windows observation never becomes direct data", () =>
        {
            var time = new ManualTime(); var binding = Binding();
            SourceState state = Empty(ReadingSource.WindowsProperty, "node-a");
            for (int i = 1; i <= 3; i++)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                state = Success(state, binding, binding.Stamp(ReadingSource.WindowsProperty, i), time, 90);
                Assert(state.LastSuccess!.ObservedAtUtc == time.GetUtcNow());
                Assert(state.LastSuccessfulReadUtc is null && state.LastSuccess.SourceTimestampUtc is null);
                Assert(BatteryRules.GetFreshness(state, null, binding, BatteryMode.Real, time, Period)
                    == ReadingFreshness.SystemCacheUnknownAge);
                Assert(Tray(new(binding, "headset", "BLE", [state]), time).MinimumPercent is null);
            }
        });
        Run("C03", "Conflicting system candidates are retained", () =>
        {
            var time = new ManualTime(); var binding = Binding();
            var a = SystemReport(binding, time, "node-a", 90);
            time.Advance(TimeSpan.FromSeconds(5));
            var b = SystemReport(binding, time, "node-b", 100);
            var device = new BatteryDeviceState(binding, "headset", "BLE", [a, b]);
            var view = BatteryRules.Present(device, BatteryMode.Real, time, Period);
            Assert(view.BatteryText.Contains("冲突") && view.SystemPercents.SequenceEqual([90, 100]));
            Assert(device.SystemCandidates.Count == 2 && Tray(device, time).MinimumPercent is null);
            Assert(view.LastSuccessfulReadUtc is null);
            Assert(view.DisplayPercent == 90 && view.CurrentPercents.Count == 0);
            Assert(BatteryRules.Present(new(binding, "headset", "BLE", [b, a]), BatteryMode.Real, time, Period).DisplayPercent == 90);
            foreach (int percent in new[] { 0, 80, 100 })
            {
                var single = new BatteryDeviceState(binding, "headset", "BLE", [SystemReport(binding, time, "node", percent)]);
                Assert(BatteryRules.Present(single, BatteryMode.Real, time, Period).DisplayPercent == percent);
                Assert(Tray(single, time).MinimumPercent is null);
            }
            var disconnected = device with { Binding = binding with { Ble = new(ConnectionState.Disconnected, 2) } };
            Assert(BatteryRules.Present(disconnected, BatteryMode.Real, time, Period).DisplayPercent is null);
            var failed = device.WithSystemCandidates([a, b with { LatestOutcome = ReadOutcome.Error }]);
            Assert(BatteryRules.Present(failed, BatteryMode.Real, time, Period).DisplayPercent is null);
        });
        Run("C04", "Current direct value wins without replacing system candidate", () =>
        {
            var time = new ManualTime(); var binding = Binding();
            var device = Device(binding, Component(binding, time, 73)).WithSystemCandidates(
                [SystemReport(binding, time, "node-a", 90)]);
            var view = BatteryRules.Present(device, BatteryMode.Real, time, Period);
            Assert(view.CurrentPercents.SequenceEqual([73]) && view.SystemPercents.SequenceEqual([90]));
            Assert(view.BatteryText == "73%" && Tray(device, time).MinimumPercent == 73);
            Assert(device.SystemCandidates[0].LastSuccess!.Value.Percent == 90);
            Assert(view.DisplayPercent == 73);
        });
        Run("C05", "Strict monotonic freshness boundary and redraw projection", () =>
        {
            var time = new ManualTime(); var binding = Binding();
            var component = Component(binding, time, 73); var device = Device(binding, component);
            time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromMilliseconds(1));
            Assert(Current(component, binding, time));
            BatteryTrayVisual before = Tray(device, time);
            time.Advance(TimeSpan.FromMilliseconds(1));
            Assert(!Current(component, binding, time) && Tray(device, time).MinimumPercent is null);
            Assert(Tray(device, time) != before);
            var younger = Component(binding, time, 73);
            time.JumpUtc(TimeSpan.FromHours(1));
            Assert(Current(younger, binding, time));
            time.JumpUtc(TimeSpan.FromHours(-2));
            Assert(Current(younger, binding, time));
            Assert(!Current(younger with { Source = younger.Source with
                { LastSuccess = younger.Source.LastSuccess! with { CapturedTimestamp = time.GetTimestamp() + 1 } } }, binding, time));
            Throws<ArgumentOutOfRangeException>(() => BatteryRules.IsCurrentValue(component, binding,
                BatteryMode.Real, time, TimeSpan.FromMinutes(0)));
        });
        Run("C06", "Classic connection cannot qualify disconnected BLE data", () =>
        {
            var time = new ManualTime(); var binding = Binding(); var component = Component(binding, time, 73);
            binding = binding with { Classic = new(ConnectionState.Connected, 7), Ble = new(ConnectionState.Disconnected, 2) };
            Assert(BatteryRules.GetConnection(binding, BatteryMode.Real) == ConnectionState.Connected);
            Assert(!Current(component, binding, time) && Tray(Device(binding, component), time).MinimumPercent is null);
            Assert(BatteryRules.GetConnection(binding with { Classic = new(ConnectionState.Unknown, 7) }, BatteryMode.Real)
                == ConnectionState.Unknown);
            Assert(BatteryRules.GetConnection(binding with { Classic = new(ConnectionState.Disconnected, 7) }, BatteryMode.Real)
                == ConnectionState.Disconnected);
            Assert(BatteryRules.GetConnection(new() { DeviceId = "empty", BindingKey = "empty" }, BatteryMode.Real)
                == ConnectionState.Unknown);
            ConnectionState?[] connections = [null, ConnectionState.Unknown, ConnectionState.Disconnected, ConnectionState.Connected];
            foreach (var classic in connections)
                foreach (var ble in connections)
                {
                    var sample = new DeviceBinding { DeviceId = "matrix", BindingKey = "matrix",
                        Classic = classic is { } c ? new(c, 1) : null, Ble = ble is { } b ? new(b, 1) : null };
                    var expected = classic == ConnectionState.Connected || ble == ConnectionState.Connected ? ConnectionState.Connected
                        : (classic is not null || ble is not null) && classic is null or ConnectionState.Disconnected
                            && ble is null or ConnectionState.Disconnected ? ConnectionState.Disconnected : ConnectionState.Unknown;
                    Assert(BatteryRules.GetConnection(sample, BatteryMode.Real) == expected);
                    Assert(BatteryRules.GetConnection(sample, BatteryMode.Mock) == ConnectionState.Unknown);
                }
        });
        Run("C07", "Reconnect requires a result in the new epoch", () =>
        {
            var time = new ManualTime(); var binding = Binding(); var component = Component(binding, time, 73);
            var disconnected = binding with { Ble = new(ConnectionState.Disconnected, 3) };
            time.Advance(TimeSpan.FromSeconds(1));
            var reconnected = disconnected with { Ble = new(ConnectionState.Connected, 4) };
            Assert(!Current(component, disconnected, time) && !Current(component, reconnected, time));
            var stamp = reconnected.Stamp(ReadingSource.GattUncached, 2);
            var next = Success(component.Source, reconnected, stamp, time, 70);
            Assert(Current(component with { Source = next }, reconnected, time));
            Assert(Tray(Device(reconnected, component with { Source = next }), time).MinimumPercent == 70);
            Assert(!Current(component, binding with { SleepInvalidated = true }, time));
        });
        Run("C08", "Only resolved identity combines multi-transport entries", () =>
        {
            var binding = Binding() with { Classic = new(ConnectionState.Connected, 1) };
            var sameName = new[] { new BatteryDeviceState(binding, "same name", "BLE"),
                new BatteryDeviceState(binding with { DeviceId = "another", BindingKey = "another" }, "same name", "BLE") };
            Assert(BatteryRules.GroupResolvedDevices(sameName).Count == 2);
            var sameIdentity = new[] { new BatteryDeviceState(binding, "name A", "Classic"),
                new BatteryDeviceState(binding, "name B", "BLE") };
            Assert(BatteryRules.GroupResolvedDevices(sameIdentity).Count == 1);
            var time = new ManualTime();
            var first = Device(binding, Component(binding, time, 73));
            var merged = BatteryRules.GroupResolvedDevices([first, Device(binding, Component(binding, time, 81, "other", 1))]).Single();
            Assert(merged.Components.Count == 2 && Tray(merged, time).MinimumPercent == 73 && first.Components.Count == 1);
            Assert(BatteryRules.GroupResolvedDevices([first]).Single().Components.Single().Source.LastSuccess!.Value.Percent == 73);
            Throws<ArgumentException>(() => BatteryRules.GroupResolvedDevices([
                sameIdentity[0], sameIdentity[1] with { Binding = binding with { BindingEpoch = 9 } }]));
        });
        Run("C09", "Partial failure preserves successful component and history", () =>
        {
            var time = new ManualTime(); var binding = Binding();
            var a = Component(binding, time, 73);
            var b = Component(binding, time, 81, "b", 1);
            var stamp = binding.Stamp(ReadingSource.GattUncached, 2);
            var pending = BatteryRules.BeginRequest(b.Source, binding, stamp, time.GetUtcNow());
            Assert(Current(b with { Source = pending }, binding, time));
            var failure = BatteryRules.ApplyResult(pending, binding, stamp, 1, ReadOutcome.TimedOut,
                BatteryValue.FromPercent(0), time.GetUtcNow(), time.GetTimestamp());
            var device = Device(binding, a, b with { Source = failure });
            var view = BatteryRules.Present(device, BatteryMode.Real, time, Period);
            Assert(view.IsPartial && view.BatteryText.Contains("部分读取"));
            Assert(view.CurrentPercents.SequenceEqual([73]) && Tray(device, time).MinimumPercent == 73);
            Assert(failure.LastSuccess!.Value.Percent == 81 && failure.LatestOutcome == ReadOutcome.TimedOut);
            Assert(view.History.Any(x => x.Contains("81%")));
        });
        Run("C10", "Directory completeness is scoped to its parent", () =>
        {
            var time = new ManualTime(); var binding = Binding();
            var a = Component(binding, time, 73); var b = Component(binding, time, 81, "b", 1);
            var incomplete = BatteryRules.ReconcileDirectory([a, b], DirectoryScope.GattServices, null, [a.ServiceKey], false);
            Assert(!incomplete[1].InCurrentGattDirectory && !incomplete[1].RemovedFromDirectory);
            Assert(incomplete[1].Source.LastSuccess!.Value.Percent == 81);
            var complete = BatteryRules.ReconcileDirectory([a, b], DirectoryScope.GattServices, null, [a.ServiceKey], true);
            Assert(complete[1].RemovedFromDirectory && !complete[1].InCurrentGattDirectory);
            var scoped = BatteryRules.ReconcileDirectory([a, b], DirectoryScope.ServiceCharacteristics, a.ServiceKey, [], true);
            Assert(scoped[0].RemovedFromDirectory && scoped[1] == b);
            var placeholders = new[] { a with { InCurrentGattDirectory = false }, b with { InCurrentGattDirectory = false } };
            var services = BatteryRules.ReconcileDirectory(placeholders, DirectoryScope.GattServices, null, [a.ServiceKey, b.ServiceKey], true);
            Assert(services.All(x => !x.InCurrentGattDirectory));
            var characteristics = BatteryRules.ReconcileDirectory(services, DirectoryScope.ServiceCharacteristics,
                a.ServiceKey, [a.ComponentKey], true);
            Assert(characteristics[0].InCurrentGattDirectory && !characteristics[1].InCurrentGattDirectory);
            var changed = BatteryRules.InvalidateDatabase(Device(binding, a, b));
            Assert(changed.Binding.GattDatabaseEpoch == binding.GattDatabaseEpoch + 1);
            Assert(Tray(changed, time).MinimumPercent is null && changed.Components.All(x => x.Source.LastSuccess is not null));
        });
        Run("C11", "Stale context, request and sequence cannot replace data", () =>
        {
            var time = new ManualTime(); var binding = Binding(); var source = Component(binding, time, 73).Source;
            QueryStamp stamp = binding.Stamp(ReadingSource.GattUncached, 1);
            QueryStamp[] invalid = [stamp with { BindingEpoch = 0 }, stamp with { TransportEpoch = 0 },
                stamp with { DatabaseEpoch = 0 }, stamp with { DeviceId = "other" }, stamp with { RequestId = 0 },
                stamp with { Source = ReadingSource.WindowsProperty }];
            foreach (var old in invalid)
            {
                Assert(BatteryRules.ApplyResult(source, binding, old, 9, ReadOutcome.Success,
                    BatteryValue.FromPercent(1), time.GetUtcNow(), time.GetTimestamp()) == source);
            }
            foreach (int sequence in new[] { 0, 1 })
                Assert(BatteryRules.ApplyResult(source, binding, stamp, sequence, ReadOutcome.Success,
                    BatteryValue.FromPercent(1), time.GetUtcNow(), time.GetTimestamp()) == source);
            var newer = BatteryRules.ApplyResult(source, binding, stamp, 2, ReadOutcome.Success,
                BatteryValue.FromPercent(72), time.GetUtcNow(), time.GetTimestamp());
            Assert(newer.LastSuccess!.Value.Percent == 72 && newer.LastUpdateSequence == 2);
            var request = binding.Stamp(ReadingSource.GattUncached, 2);
            newer = BatteryRules.BeginRequest(newer, binding, request, time.GetUtcNow());
            Assert(BatteryRules.ApplyResult(newer, binding, stamp, 99, ReadOutcome.Success,
                BatteryValue.FromPercent(1), time.GetUtcNow(), time.GetTimestamp()) == newer);
        });
        Run("C25", "Mode isolation and unthrottled Mock compatibility", () =>
        {
            var time = new ManualTime(); using var provider = new MockBatteryProvider(time);
            var models = provider.ReadModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(models.Count == 3 && models.All(x => x.Binding.DeviceId.StartsWith("mock:")));
            Assert(BatteryRules.GetTrayVisual(models, BatteryMode.Mock, time, Period).MinimumPercent == 73);
            Assert(BatteryRules.GetTrayVisual(models, BatteryMode.Real, time, Period).MinimumPercent is null);
            var real = Device(Binding(), Component(Binding(), time, 66));
            Assert(BatteryRules.GetTrayVisual([real], BatteryMode.Mock, time, Period).MinimumPercent is null);
            var wrongMode = BatteryRules.Present(real, BatteryMode.Mock, time, Period);
            Assert(wrongMode.CurrentPercents.Count == 0 && wrongMode.History.Count == 0
                && wrongMode.LastSuccessfulReadUtc is null);
            var wrongRealMode = BatteryRules.Present(models[0], BatteryMode.Real, time, Period);
            Assert(wrongRealMode.CurrentPercents.Count == 0 && wrongRealMode.History.Count == 0);
            time.Advance(TimeSpan.FromDays(2));
            Assert(BatteryRules.GetTrayVisual(models, BatteryMode.Mock, time, Period).MinimumPercent == 73);
            for (int i = 0; i < 100; i++)
            {
                var pending = provider.ReadAsync(CancellationToken.None);
                Assert(pending.IsCompletedSuccessfully);
                var legacy = pending.GetAwaiter().GetResult();
                Assert(legacy.Count == 3 && legacy[0].BatteryText == "73%" && legacy[1].BatteryText == "高"
                    && legacy[2].BatteryText == "离线" && legacy.All(x => x.Source == "Mock / 模拟"));
            }
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Throws<OperationCanceledException>(() => provider.ReadModelsAsync(cancellation.Token).GetAwaiter().GetResult());
            provider.Dispose();
            Throws<ObjectDisposedException>(() => provider.ReadModelsAsync(CancellationToken.None).GetAwaiter().GetResult());
        });
        Run("A01", "Snapshots own immutable collections and visual ignores timestamps", () =>
        {
            var time = new ManualTime(); var binding = Binding(); var component = Component(binding, time, 73);
            var components = new List<ComponentState> { component };
            var device = new BatteryDeviceState(binding, "headset", "BLE", components: components);
            var devices = new List<BatteryDeviceState> { device };
            var snapshot = new BatterySnapshot(1, time.GetUtcNow(), BatteryMode.Real, devices, time, Period);
            components.Clear(); devices.Clear();
            Assert(snapshot.Devices.Count == 1 && snapshot.Devices[0].Components.Count == 1);
            Throws<NotSupportedException>(() => ((IList<BatteryDeviceState>)snapshot.Devices).Clear());
            Throws<NotSupportedException>(() => ((IList<ComponentState>)device.Components).Clear());
            time.Advance(TimeSpan.FromSeconds(1));
            var next = Component(binding, time, 73);
            Assert(snapshot.TrayVisual == Tray(Device(binding, next), time));
        });
        Run("A02", "Invalid success retains history and cannot regain current status", () =>
        {
            var time = new ManualTime(); var binding = Binding(); var component = Component(binding, time, 73);
            var stamp = binding.Stamp(ReadingSource.GattUncached, 2);
            var pending = BatteryRules.BeginRequest(component.Source, binding, stamp, time.GetUtcNow());
            var failed = BatteryRules.ApplyResult(pending, binding, stamp, 1, ReadOutcome.Success, null,
                time.GetUtcNow(), time.GetTimestamp());
            Assert(failed.LatestOutcome == ReadOutcome.Error && failed.LastSuccess == component.Source.LastSuccess);
            Assert(!Current(component with { Source = failed }, binding, time));
            var nextPending = BatteryRules.BeginRequest(failed, binding, stamp with { RequestId = 3 }, time.GetUtcNow());
            Assert(nextPending.LatestOutcome == ReadOutcome.Error && nextPending.IsRefreshing);
        });
        Run("A03", "Source consistency, failed properties and removed paths", () =>
        {
            var time = new ManualTime(); var binding = Binding(); var component = Component(binding, time, 73);
            var forged = component.Source with { Source = ReadingSource.WindowsProperty };
            var malformed = new BatteryDeviceState(binding, "headset", "BLE", [forged]);
            Assert(BatteryRules.GetSystemReports(malformed).Count == 0 && forged.LastSuccessfulReadUtc is null);
            var valid = SystemReport(binding, time, "node", 90);
            var stamp = binding.Stamp(ReadingSource.WindowsProperty, 2);
            var pending = BatteryRules.BeginRequest(valid, binding, stamp, time.GetUtcNow());
            var failed = BatteryRules.ApplyResult(pending, binding, stamp, 1, ReadOutcome.Error, null,
                time.GetUtcNow(), time.GetTimestamp());
            var failedDevice = new BatteryDeviceState(binding, "headset", "BLE", [failed]);
            Assert(BatteryRules.Present(failedDevice, BatteryMode.Real, time, Period).BatteryText == "读取失败");
            Assert(failed.LastSuccess!.Value.Percent == 90 && BatteryRules.GetSystemReports(failedDevice).Count == 0);
            var removed = Component(binding, time, 81, "b", 1) with
                { RemovedFromDirectory = true, InCurrentGattDirectory = false };
            var view = Tray(Device(binding, component, removed), time);
            Assert(view.MinimumPercent == 73 && !view.HasUnknown);
            var dbChanged = BatteryRules.InvalidateDatabase(Device(binding, component).WithSystemCandidates([valid]));
            Assert(BatteryRules.GetSystemReports(dbChanged).Count == 1);
            var connectionChanged = binding with { Ble = new(ConnectionState.Connected, 99) };
            Assert(BatteryRules.ApplyResult(component.Source, connectionChanged, component.Source.LatestRequestStamp!.Value,
                2, ReadOutcome.Success, BatteryValue.FromPercent(1), time.GetUtcNow(), time.GetTimestamp()) == component.Source);
        });

        CoordinatorCases.Run(Run, Assert);
        LowBatteryCases.Run(Run, Assert);
        Console.WriteLine($"Core: {Checks.Count - _failures}/{Checks.Count} cases passed; {_assertions} assertions.");
        if (args.Length == 2)
        {
            string report = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            File.WriteAllText(report, JsonSerializer.Serialize(new {
                TimestampUtc = DateTimeOffset.UtcNow,
                BuildId = typeof(Program).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "BuildId").Value,
                Scope = "Core A/B; fake clock/operations and Mock only; no native device API or App UI execution.",
                Runtime = Environment.Version.ToString(), Passed = _failures == 0, AssertionCount = _assertions,
                Checks, Assemblies = new[] { typeof(Program).Assembly.Location, typeof(BatteryRules).Assembly.Location }
                    .Select(path => new { File = Path.GetFileName(path), Sha256 = Hash(path) })
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return _failures == 0 ? 0 : 1;
    }

    private static void Run(string id, string name, Action action)
    {
        int before = _assertions;
        try { action(); Checks.Add(new { Id = id, Name = name, Passed = true, Assertions = _assertions - before }); Console.WriteLine($"PASS {id} {name}"); }
        catch (Exception error) { _failures++; Checks.Add(new { Id = id, Name = name, Passed = false, Error = error.Message }); Console.WriteLine($"FAIL {id}: {error.Message}"); }
    }
    private static void Assert(bool condition) { _assertions++; if (!condition) throw new InvalidOperationException($"Assertion {_assertions} failed."); }
    private static void Throws<T>(Action action) where T : Exception
    {
        _assertions++;
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static DeviceBinding Binding() => new() { DeviceId = "device-1", BindingKey = "resolved-1",
        BindingEpoch = 1, Ble = new(ConnectionState.Connected, 2), GattDatabaseEpoch = 3 };
    private static SourceState Empty(ReadingSource source, string key, string? component = null)
        => new() { Source = source, CandidateKey = key, ComponentKey = component };
    private static SourceState Success(SourceState source, DeviceBinding binding, QueryStamp stamp, ManualTime time, int percent)
        => BatteryRules.ApplyResult(BatteryRules.BeginRequest(source, binding, stamp, time.GetUtcNow()), binding,
            stamp, 1, ReadOutcome.Success, BatteryValue.FromPercent(percent), time.GetUtcNow(), time.GetTimestamp());
    private static SourceState SystemReport(DeviceBinding binding, ManualTime time, string node, int percent)
        => Success(Empty(ReadingSource.WindowsProperty, node), binding, binding.Stamp(ReadingSource.WindowsProperty, 1), time, percent);
    private static ComponentState Component(DeviceBinding binding, ManualTime time, int percent, string key = "a", int order = 0)
        => new(key, "service-" + key, order, Success(Empty(ReadingSource.GattUncached, "candidate-" + key, key), binding,
            binding.Stamp(ReadingSource.GattUncached, 1), time, percent), true);
    private static BatteryDeviceState Device(DeviceBinding binding, params ComponentState[] components)
        => new(binding, "headset", "BLE", components: components);
    private static bool Current(ComponentState component, DeviceBinding binding, ManualTime time)
        => BatteryRules.IsCurrentPercent(component, binding, BatteryMode.Real, time, Period);
    private static BatteryTrayVisual Tray(BatteryDeviceState device, ManualTime time)
        => BatteryRules.GetTrayVisual([device], BatteryMode.Real, time, Period);
}

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _utc = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
    private long _timestamp = 100_000;
    public override long TimestampFrequency => 1000;
    public override DateTimeOffset GetUtcNow() => _utc;
    public override long GetTimestamp() => _timestamp;
    public void Advance(TimeSpan duration) { _timestamp += (long)(duration.TotalSeconds * TimestampFrequency); _utc += duration; }
    public void JumpUtc(TimeSpan duration) => _utc += duration;
}
