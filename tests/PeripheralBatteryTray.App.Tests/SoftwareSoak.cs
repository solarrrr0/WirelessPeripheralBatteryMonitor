using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PeripheralBatteryTray.App;
using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

/// <summary>Opt-in development workload. No native discovery, GATT, HID, startup or real notifier is constructed.</summary>
internal static class SoftwareSoak
{
    internal static int Benchmark(string[] args)
    {
        if (args.Length != 3 || args[1] != "--report" || File.Exists(args[2])) return 3;
        var devices = Enumerable.Range(0, 20).Select(i => Device(i, 0)).ToArray();
        long checksum = 0;
        void Work(int i)
        {
            var snapshot = new BatterySnapshot(i, TimeProvider.System.GetUtcNow(), BatteryMode.Real, devices,
                TimeProvider.System, TimeSpan.FromMinutes(5));
            var projected = DevicePresentation.Project(snapshot, TimeSpan.FromMinutes(5), null, DisplaySettings.Default);
            checksum += projected.Count + (DevicePresentation.Aggregate(projected, BatteryMode.Real).MinimumPercent ?? -1);
        }
        for (int i = 0; i < 100; i++) Work(i);
        long before = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
        for (int i = 0; i < 5000; i++) Work(i);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        File.WriteAllText(args[2], JsonSerializer.Serialize(new { BuildId = BuildInfo.Id, Iterations = 5000, Devices = 20,
            AllocatedBytes = bytes, BytesPerIteration = bytes / 5000d, ElapsedSeconds = clock.Elapsed.TotalSeconds,
            Checksum = checksum, Scope = "Warmed synthetic snapshot/projection workload; allocation comparison only, not process RAM or hardware performance." },
            new JsonSerializerOptions { WriteIndented = true }));
        return checksum == 102000 ? 0 : 1;
    }
    internal static int Run(string[] args)
    {
        if (args.Length != 7 || args[1] != "--report" || args[3] != "--seconds" || args[5] != "--profile"
            || !int.TryParse(args[4], out int seconds) || seconds is < 10 or > 86400
            || args[6] is not ("idle" or "stress")) return 3;
        string path = Path.GetFullPath(args[2]);
        if (File.Exists(path)) return 3;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bool stress = args[6] == "stress";
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetDefaultFont(new System.Drawing.Font("Microsoft YaHei UI", 9));
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        var clock = Stopwatch.StartNew();
        var samples = new List<Sample>();
        var notifier = new SimulatedNotifier();
        var store = new PollingSettingsStore(); // Memory only.
        string prefix = @"Local\PeripheralBatteryTray.Soak." + Guid.NewGuid().ToString("N") + ".";
        using var instance = new SingleInstance(AppMode.Real, prefix);
        var coordinator = new BatteryCoordinator(BatteryMode.Real, []);
        for (int i = 0; i < 20; i++) coordinator.UpsertDevice(Device(i, 0));
        var context = new TrayApplicationContext(instance, coordinator, settingsStore: store, notifier: notifier);
        int iterations = 0, removals = 0, resumes = 0, settings = 0, recoveries = 0, projections = 0;
        int maxIcons = 0, maxRows = 0;
        long allocationStart = GC.GetTotalAllocatedBytes();
        string? error = null;
        using var pulse = new System.Windows.Forms.Timer { Interval = stress ? 100 : 1000 };
        double nextSample = 0;
        void Capture()
        {
            using var p = Process.GetCurrentProcess();
            samples.Add(new(clock.Elapsed.TotalSeconds, p.TotalProcessorTime.TotalSeconds, p.WorkingSet64,
                p.PrivateMemorySize64, p.HandleCount, GetGuiResources(p.Handle, 0), GetGuiResources(p.Handle, 1),
                GC.GetTotalMemory(false), GC.GetTotalAllocatedBytes(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                iterations, context.DeviceIconCount, context.ListRenderCount));
            // A checkpoint survives an interrupted test. It is not a completed acceptance report.
            File.WriteAllText(path + ".checkpoint.local.json", JsonSerializer.Serialize(new { Completed = false, BuildId = BuildInfo.Id,
                Profile = args[6], DurationSeconds = clock.Elapsed.TotalSeconds, Iterations = iterations, LastSample = samples[^1] }));
        }
        pulse.Tick += (_, _) =>
        {
            try
            {
                if (clock.Elapsed.TotalSeconds >= seconds) { pulse.Stop(); context.RequestExit(); return; }
                // Keep the last 30 seconds quiet for cleanup observation; workload time is always real wall time.
                bool active = stress && clock.Elapsed.TotalSeconds < seconds - Math.Min(30, seconds / 4);
                if (active)
                {
                    int i = iterations % 20;
                    coordinator.RemoveDevice("soak:" + i); removals++;
                    coordinator.UpsertDevice(Device(i, iterations));
                    if (iterations % 100 == 0)
                    {
                        coordinator.Suspend(); coordinator.Resume(); resumes++;
                        for (int j = 0; j < 20; j++) coordinator.UpsertDevice(Device(j, iterations));
                    }
                    if (iterations % 40 == 0)
                    {
                        var prefs = Enumerable.Range(0, 20).Select(j => new KeyValuePair<string, DevicePreference>(
                            DisplaySettings.KeyFor("soak:" + j), new("模拟设备 " + j, (iterations / 40 % 3 == 1) && j % 4 == 0)));
                        var display = new DisplaySettings((DeviceSort)(iterations / 40 % 3),
                            iterations / 40 % 2 == 0 ? TrayIconMode.PerDevice : TrayIconMode.Aggregate, prefs);
                        if (!store.TrySave(5, false, 5, true, 20, display)) throw new InvalidOperationException("Memory settings rejected.");
                        context.ApplySettings(store.Current);
                    }
                    if (iterations % 120 == 0)
                    {
                        // Actual production controls; no desktop automation and no startup registration service.
                        using var form = new SettingsForm(store, BatteryMode.Real, context.ApplySettings,
                            devices: context.PresentedDevices);
                        form.CreateControl();
                        form.DeviceSettings.IconChoice.SelectedIndex = 1;
                        if (!form.DeviceSettings.TryRead(out _, out _)) throw new InvalidOperationException("Settings controls rejected synthetic values.");
                        form.Dispose(); settings++;
                        context.SimulateTaskbarCreated(); recoveries++;
                    }
                    if (iterations % 20 == 0)
                    {
                        // Populate the actual hidden list without bringing any test window to the foreground.
                        context.Popup.SetPresentation(context.CurrentSnapshot, coordinator.RefreshPeriod, context.PresentedDevices);
                        projections++; maxRows = Math.Max(maxRows, context.Popup.DeviceRowCount);
                    }
                    iterations++;
                    maxIcons = Math.Max(maxIcons, context.DeviceIconCount);
                    if (context.DeviceIconCount > DeviceTrayIcons.MaximumIcons) throw new InvalidOperationException("Icon bound exceeded.");
                }
                if (clock.Elapsed.TotalSeconds >= nextSample) { Capture(); nextSample = clock.Elapsed.TotalSeconds + 5; }
            }
            catch (Exception e) { error = e.ToString(); pulse.Stop(); context.RequestExit(); }
        };
        try { pulse.Start(); Application.Run(context); }
        catch (Exception e) { error = e.ToString(); context.RequestExit(); }
        pulse.Stop(); Capture();
        var shutdown = context.Shutdown;
        context.Dispose();
        var finalSample = ResourceSample(clock, iterations, 0, context.ListRenderCount); samples.Add(finalSample);
        bool completed = error is null && clock.Elapsed.TotalSeconds >= seconds && shutdown is { FullyStopped: true, CleanupErrors: 0 }
            && context.ResourcesDisposed && context.DeviceIconCount == 0;
        bool exercised = !stress || iterations > 0 && removals > 0 && settings > 0 && resumes > 0 && recoveries > 0 && maxIcons == 16 && maxRows > 0;
        var stable = samples.Where(s => s.Seconds >= Math.Min(60, seconds / 2)).ToArray();
        var first = stable.FirstOrDefault(); var last = stable.LastOrDefault();
        File.WriteAllText(path, JsonSerializer.Serialize(new {
            SchemaVersion = 1, BuildId = BuildInfo.Id, Passed = completed && exercised, Completed = completed, Profile = args[6],
            Runtime = Environment.Version.ToString(), RequestedSeconds = seconds, DurationSeconds = clock.Elapsed.TotalSeconds,
            Iterations = iterations, Removals = removals, SimulatedResumeCycles = resumes, SettingsForms = settings,
            SyntheticShellRecoveries = recoveries, HiddenListProjections = projections, MaximumIcons = maxIcons, MaximumRows = maxRows,
            SimulatedNotificationCalls = notifier.Calls, AllocationBytes = GC.GetTotalAllocatedBytes() - allocationStart,
            HandleDeltaAfterWarmup = first is null || last is null ? (int?)null : last.Handles - first.Handles,
            GdiDeltaAfterWarmup = first is null || last is null ? (long?)null : (long)last.Gdi - first.Gdi,
            UserDeltaAfterWarmup = first is null || last is null ? (long?)null : (long)last.User - first.User,
            Shutdown = shutdown, ResourcesDisposed = context.ResourcesDisposed, Error = error, Samples = samples,
            HardwareConstructed = false, PersistentSettings = false, StartupRegistration = false, RealNotifications = false,
            ActualExplorerRestart = false, ActualSystemSleep = false, TwentyFourHourGate = "NotRun",
            Scope = "Synthetic states using production coordinator, projections, WinForms controls and Shell icons in an isolated test host. Not shipped-EXE performance or hardware/system/24-hour acceptance. No forced GC or working-set trimming.",
            Assemblies = new[] { typeof(SoftwareSoak).Assembly.Location, typeof(TrayApplicationContext).Assembly.Location,
                typeof(BatteryRules).Assembly.Location, typeof(PnpNative).Assembly.Location }.Select(p => new { File = Path.GetFileName(p), Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) })
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Software {args[6]}: {clock.Elapsed.TotalSeconds:F1}s, {iterations} iterations, passed={completed && exercised}. {path}");
        return completed && exercised ? 0 : 1;
    }

    private static Sample ResourceSample(Stopwatch clock, int iterations, int icons, int renders)
    {
        using var p = Process.GetCurrentProcess();
        return new(clock.Elapsed.TotalSeconds, p.TotalProcessorTime.TotalSeconds, p.WorkingSet64, p.PrivateMemorySize64,
            p.HandleCount, GetGuiResources(p.Handle, 0), GetGuiResources(p.Handle, 1), GC.GetTotalMemory(false),
            GC.GetTotalAllocatedBytes(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), iterations, icons, renders);
    }
    private sealed record Sample(double Seconds, double CpuSeconds, long WorkingSetBytes, long PrivateBytes,
        int Handles, uint Gdi, uint User, long ManagedHeapBytes, long AllocatedBytes, int Gen0, int Gen1, int Gen2,
        int Iterations, int Icons, int ListRenders);
    private sealed class SimulatedNotifier : ILowBatteryNotifier
    { internal int Calls; public NotificationDelivery Send(IReadOnlyList<LowBatteryAlert> alerts) { Calls++; return NotificationDelivery.Suppressed; } }
    private static BatteryDeviceState Device(int i, int version)
    {
        string id = "soak:" + i;
        var connection = version % 9 == 3 ? ConnectionState.Disconnected : ConnectionState.Connected;
        var binding = new DeviceBinding { DeviceId = id, BindingKey = id, BindingEpoch = 1,
            PreferenceKey = DisplaySettings.KeyFor(id), Ble = new(connection, version / 100 + 1), GattDatabaseEpoch = 1 };
        var time = TimeProvider.System; var stamp = binding.Stamp(ReadingSource.GattUncached, version + 1);
        int value = (i * 7 + version / 20) % 101;
        var source = new SourceState { Source = ReadingSource.GattUncached, CandidateKey = id, ComponentKey = id + "/battery",
            LatestRequestStamp = stamp, LatestOutcome = ReadOutcome.Success,
            LastSuccess = new(BatteryValue.FromPercent(value), ReadingSource.GattUncached, stamp, time.GetUtcNow(), time.GetTimestamp()) };
        return new(binding, "模拟设备 " + i, "Synthetic BLE", components: [new(id + "/battery", id + "/service", 0, source, true)]);
    }
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
