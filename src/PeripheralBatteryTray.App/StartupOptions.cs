using System.Reflection;
using System.Globalization;
using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

namespace PeripheralBatteryTray.App;

internal sealed record StartupOptions(bool Mock, bool SelfTest, bool HostCheck, bool Exit,
    string? Report, int? ExpectedPid, long? ExpectedStartTicks, string? MeasurementReport = null, bool GattCheck = false, bool AtkCheck = false)
{
    internal bool AllowsAtkTray => !Mock && !SelfTest && !HostCheck && !GattCheck && !AtkCheck && !Exit && MeasurementReport is null;
    public static StartupOptions? Parse(string[] args)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal);
        string? report = null, measurementReport = null; int? pid = null; long? ticks = null;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!flags.Add(arg)) return null;
            if (arg is "--report" or "--measurement-report" or "--expected-pid" or "--expected-start-ticks")
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) return null;
                if (arg == "--report") { if (string.IsNullOrWhiteSpace(args[i])) return null; report = args[i]; }
                else if (arg == "--measurement-report") { if (string.IsNullOrWhiteSpace(args[i])) return null; measurementReport = args[i]; }
                else if (arg == "--expected-pid") { if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out int p) || p <= 0) return null; pid = p; }
                else { if (!long.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out long t) || t <= 0 || t > DateTime.MaxValue.Ticks) return null; ticks = t; }
            }
            else if (arg is not ("--mock" or "--self-test" or "--exit" or "--verify-windows-host" or "--verify-gatt-host" or "--verify-atk-host")) return null;
        }
        bool self = flags.Contains("--self-test"), host = flags.Contains("--verify-windows-host"), mock = self || flags.Contains("--mock"), exit = flags.Contains("--exit");
        bool gatt = flags.Contains("--verify-gatt-host");
        bool atk = flags.Contains("--verify-atk-host");
        if ((host && mock) || (gatt && (host || mock || exit || measurementReport is not null))
            || (atk && (host || gatt || mock || exit || measurementReport is not null))
            || (measurementReport is not null && (host || self || exit)) || (report is not null && !self && !host && !gatt && !atk) || pid.HasValue != ticks.HasValue
            || (pid.HasValue && !exit) || (exit && flags.Any(f => f is not ("--exit" or "--expected-pid" or "--expected-start-ticks")))) return null;
        return new(mock, self, host, exit, report, pid, ticks, measurementReport, gatt, atk);
    }
}

internal static class BuildInfo
{
    public static string Id => typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "BuildId").Value!;
    public const string Capability = "WindowsProperty; opt-in ATK and alerts; aliases, hidden devices, sorting, optional per-device icons and user login startup; automatic GATT disabled";
}

internal sealed record AppComposition(WindowsBatteryRuntime? Runtime, NativeMetadataEvidence? Audit,
    PollingSettingsStore Settings, BatteryCoordinator Coordinator)
{
    internal static AppComposition Create(StartupOptions options,
        Func<NativeMetadataEvidence?, WindowsBatteryRuntime>? windowsFactory = null,
        Func<string?, PollingSettingsStore>? settingsFactory = null)
    {
        if (options.Exit) throw new InvalidOperationException("Exit does not create providers.");
        if (options.AtkCheck) throw new InvalidOperationException("ATK diagnostics use a separate host without Bluetooth providers.");
        var settings = (settingsFactory ?? (path => new(path)))(options.Mock || options.HostCheck || options.GattCheck ? null : PollingSettingsStore.UserConfigPath);
        if (options.Mock)
        {
            var provider = new MockBatteryProvider();
            var coordinator = new BatteryCoordinator(BatteryMode.Mock, [provider]);
            foreach (var device in provider.ReadModelsAsync(default).Result) coordinator.UpsertDevice(device);
            return new(null, null, settings, coordinator);
        }
        var audit = options.HostCheck ? new NativeMetadataEvidence(new PnpNative()) : null;
        var telemetry = new DiagnosticTelemetry();
        var runtime = (windowsFactory ?? (native => new(native: native, telemetry: telemetry)))(audit);
        return new(runtime, audit, settings, new(BatteryMode.Real, [runtime.Provider], refreshMinutes: settings.Current.Minutes, telemetry: telemetry));
    }
}
