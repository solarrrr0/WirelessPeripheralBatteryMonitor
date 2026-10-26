using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.App;

/// <summary>Opt-in local measurement evidence; observes existing snapshots, never refreshes devices.</summary>
internal sealed class MeasurementSession : IDisposable
{
    private readonly TrayApplicationContext _context;
    private readonly BatteryCoordinator _coordinator;
    private readonly Func<string?> _notice;
    private readonly string _path;
    private readonly System.Windows.Forms.Timer _warmup = new() { Interval = 60_000 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DiagnosticReport? _baseline;
    private double? _baselineSeconds;
    private bool _onlineOnly = true, _offlineOnly = true, _complete = true;
    private int _observations;
    public MeasurementSession(TrayApplicationContext context, BatteryCoordinator coordinator, Func<string?> notice, string path)
    {
        _context = context; _coordinator = coordinator; _notice = notice; _path = Path.GetFullPath(path);
        _warmup.Tick += (_, _) =>
        {
            _warmup.Stop(); _baseline = context.CaptureDiagnostics(); _baselineSeconds = _clock.Elapsed.TotalSeconds;
            Observe();
        };
        coordinator.SnapshotChanged += Changed;
        _warmup.Start();
    }
    private void Changed(BatterySnapshot _) { if (_baseline is not null) Observe(); }
    private readonly object _gate = new();
    private void Observe()
    {
        lock (_gate)
        {
            var snapshot = _coordinator.Snapshot;
            var connections = snapshot.Devices.Select(d => BatteryRules.GetConnection(d.Binding, snapshot.Mode)).ToArray();
            _complete &= _notice() is null;
            _onlineOnly &= connections.Length > 0 && connections.All(c => c == ConnectionState.Connected);
            _offlineOnly &= connections.All(c => c == ConnectionState.Disconnected);
            _observations++;
        }
    }
    public void Write()
    {
        _warmup.Stop(); _coordinator.SnapshotChanged -= Changed;
        var final = _context.CaptureDiagnostics();
        string scenario;
        lock (_gate) scenario = final.Mode == BatteryMode.Mock ? "Mock" : _baseline is null || !_complete ? "Unclassified"
            : _offlineOnly ? "OfflineObserved" : _onlineOnly ? "OnlineObserved" : "MixedObserved";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(new {
            BuildId = BuildInfo.Id, Mode = final.Mode.ToString(), Capability = BuildInfo.Capability,
            Runtime = Environment.Version.ToString(), DurationSeconds = _clock.Elapsed.TotalSeconds,
            BaselineSeconds = _baselineSeconds, Scenario = scenario, StableObservations = _observations,
            ListRenderCount = _context.ListRenderCount,
            Baseline = _baseline, Final = final, Shutdown = _context.Shutdown,
            Assemblies = new[] { Environment.ProcessPath!, typeof(Program).Assembly.Location,
                typeof(BatteryRules).Assembly.Location, typeof(PeripheralBatteryTray.Windows.PnpNative).Assembly.Location }
                .Select(p => new { File = Path.GetFileName(p), Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) }),
            Scope = "Local snapshot/counter observation; no GATT provider. Adapter counters are not wireless packet counts."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    public void Dispose() { _warmup.Dispose(); _coordinator.SnapshotChanged -= Changed; }
}
