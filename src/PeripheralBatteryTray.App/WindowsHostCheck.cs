using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

namespace PeripheralBatteryTray.App;

/// <summary>Explicit read-only host check. No GATT provider is constructed; evidence stays in a local report.</summary>
internal static class WindowsHostCheck
{
    public static int Run(TrayApplicationContext context, WindowsBatteryRuntime runtime, NativeMetadataEvidence audit, string path)
    {
        string output = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var elapsed = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 250 };
        bool done = false;
        DiscoverySnapshot? initial = null;
        timer.Tick += (_, _) =>
        {
            bool terminal = runtime.Discovery.ClassicStatus is DiscoveryStatus.Completed or DiscoveryStatus.Failed
                && runtime.Discovery.BleStatus is DiscoveryStatus.Completed or DiscoveryStatus.Failed;
            if (!done && ((terminal && runtime.Index.Version > 0 && elapsed.Elapsed > TimeSpan.FromSeconds(3))
                || elapsed.Elapsed > TimeSpan.FromSeconds(13)))
            { done = true; initial = runtime.Discovery; timer.Stop(); context.RequestExit(); }
        };
        timer.Start(); Application.Run(context);
        var discovery = runtime.Discovery; var index = runtime.Index;
        bool passed = context.Shutdown?.FullyStopped == true && discovery.ClassicStatus == DiscoveryStatus.Stopped
            && discovery.BleStatus == DiscoveryStatus.Stopped && done
            && initial?.ClassicStatus == DiscoveryStatus.Completed && initial.BleStatus == DiscoveryStatus.Completed;
        // Capture pre-stop initial completion evidence separately from terminal status.
        var files = new[] { Environment.ProcessPath!, typeof(WindowsHostCheck).Assembly.Location,
            typeof(BatteryRules).Assembly.Location, typeof(PnpNative).Assembly.Location };
        File.WriteAllText(output, JsonSerializer.Serialize(new {
            TimestampUtc = DateTimeOffset.UtcNow, Passed = passed, Scope = "Read-only paired Windows discovery and metadata; no GATT/HID/Computer Use.",
            Runtime = Environment.Version.ToString(), ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
            Privacy = "LOCAL ONLY: includes raw AEP/PnP identities. Excluded from packages and uploads.",
            InitialDiscovery = initial, Discovery = discovery, Index = index, Bindings = runtime.Bindings.Snapshot(), Shutdown = context.Shutdown,
            BatterySnapshot = context.CurrentSnapshot, CompatibilityNativeTypes = audit.Snapshot(),
            Assemblies = files.Select(f => new { File = Path.GetFileName(f), Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) })
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}
