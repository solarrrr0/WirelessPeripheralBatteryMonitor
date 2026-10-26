using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Cryptography;
using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.App;

/// <summary>Opt-in integration checks exercise this application's own controls, without desktop automation.</summary>
internal static class Phase1SelfTest
{
    internal static int Run(TrayApplicationContext context, string reportPath)
    {
        var checks = new List<object>();
        var failures = new List<string>();
        int testedDpi = context.Popup.DeviceDpi;
        string output = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        void Check(string name, bool passed)
        {
            checks.Add(new { Name = name, Passed = passed });
            if (!passed) failures.Add(name);
        }

        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                await context.LastRefresh;
                Check("Three explicitly simulated devices", context.Devices.Count == 3
                    && context.Devices.All(x => x.Source == "Mock / 模拟" && x.DeviceId.StartsWith("mock:"))
                    && context.Popup.DeviceRowCount == 3);
                Check("Tray configured visible; startup popup hidden", context.TrayVisible && !context.Popup.Visible
                    && !context.Popup.ShowInTaskbar);
                Check("Band remains a band; offline remains offline", context.Devices[1].BatteryText == "高"
                    && context.Devices[1].BatteryPercent is null && context.Devices[2].BatteryText == "离线"
                    && context.Devices[2].BatteryPercent is null);

                DeviceBatteryState current = context.Devices[0];
                var offlineCache = current with { Connected = false, ReadStatus = BatteryReadStatus.Offline, BatteryPercent = 0 };
                var unknown = current with { ReadStatus = BatteryReadStatus.Unknown, BatteryPercent = 0 };
                Check("Offline/unknown cached zero excluded from aggregation",
                    TrayVisual.From([offlineCache, unknown]).MinimumPercent is null
                    && TrayVisual.From([current, offlineCache, unknown]).MinimumPercent == 73
                    && unknown.BatteryText == "未知" && offlineCache.BatteryText == "离线");
                Check("Out-of-range percentages rejected", !(current with { BatteryPercent = 101 }).HasCurrentPercent
                    && !(current with { BatteryPercent = -1 }).HasCurrentPercent);
                Check("Real zero and 100 remain valid", (current with { BatteryPercent = 0 }).BatteryText == "0%"
                    && (current with { BatteryPercent = 100 }).BatteryText == "100%"
                    && TrayVisual.From([]).MinimumPercent is null);
                using (var provider = new MockBatteryProvider())
                {
                    bool cancelled = false;
                    using var cancellation = new CancellationTokenSource();
                    cancellation.Cancel();
                    try { await provider.ReadAsync(cancellation.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    provider.Dispose();
                    bool disposed = false;
                    try { await provider.ReadAsync(CancellationToken.None); }
                    catch (ObjectDisposedException) { disposed = true; }
                    Check("Provider observes cancellation and disposal", cancelled && disposed);
                }

                context.TogglePopup();
                Check("Left-click callback opens list", context.Popup.Visible);
                context.Popup.Close();
                Check("Closing list keeps application resident", !context.Popup.Visible && !context.Popup.IsDisposed
                    && !context.IsExiting && context.TrayVisible);
                context.TogglePopup();
                context.TogglePopup();
                Check("Left-click callback toggles list", !context.Popup.Visible);
                context.SettingsMenu.PerformClick();
                Check("Settings callback opens one settings window", context.SettingsVisible);
                context.SettingsMenu.PerformClick();
                context.CloseSettings();
                Check("Closing settings keeps tray alive", !context.SettingsVisible && context.TrayVisible);

                int renders = context.IconRenderCount;
                int refreshes = context.RefreshCount;
                int gdiBefore = GuiObjects(0);
                int userBefore = GuiObjects(1);
                for (int i = 0; i < 100; i++)
                {
                    context.RefreshMenu.PerformClick();
                    await context.LastRefresh;
                }
                Check("100 manual refreshes complete without icon redraw", context.RefreshCount == refreshes + 100
                    && context.IconRenderCount == renders && context.CurrentSnapshot.Mode == BatteryMode.Mock
                    && context.CurrentSnapshot.TrayVisual.MinimumPercent == 73);
                int recoveries = context.RecoveryCount;
                context.SimulateTaskbarCreated();
                Check("Synthetic TaskbarCreated re-registers tray without repaint", context.RecoveryCount == recoveries + 1
                    && context.TrayVisible && context.IconRenderCount == renders);
                for (int i = 0; i < 20; i++)
                    foreach (int size in new[] { 16, 20, 24, 32 })
                        using (Icon icon = TrayIconRenderer.Create(TrayVisual.From(context.Devices), size))
                            if (icon.Width != size || icon.Height != size) throw new InvalidOperationException("Icon size mismatch");
                Check("Repeated refresh/icon creation releases GDI and USER handles",
                    GuiObjects(0) <= gdiBefore + 4 && GuiObjects(1) <= userBefore + 4);

                foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
                {
                    using var form = new DeviceListForm(() => { }, () => { }, () => { });
                    _ = form.Handle;
                    form.SetDevices(context.Devices);
                    form.Show();
                    form.Scale(new SizeF(scale, scale));
                    form.PerformLayout();
                    form.Update();
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(Path.Combine(Path.GetDirectoryName(output)!, $"mock-layout-{scale * 100:0}.png"));
                    form.Hide();
                    Check($"Geometry scale {scale * 100:0}% fits three rows at current monitor DPI", form.DeviceRowCount == 3
                        && form.Width >= 480 * scale && form.AllRowsFit);
                }

                using (var duplicate = new Process { StartInfo = new ProcessStartInfo(Environment.ProcessPath!) { Arguments = "--mock", UseShellExecute = false, CreateNoWindow = true } })
                {
                    duplicate.Start();
                    await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Check("Second launch exits successfully", duplicate.ExitCode == 0);
                }
                await Task.Delay(150);
                Check("Second launch signals existing popup", context.Popup.Visible);
            }
            catch (Exception error)
            {
                failures.Add(error.ToString());
            }
            finally { context.ExitMenu.PerformClick(); }
        };
        timer.Start();
        Application.Run(context);
        Check("Exit menu stops message loop and hides tray", context.IsExiting && !context.TrayVisible
            && context.LastExit.IsCompleted && context.Shutdown?.FullyStopped == true);
        context.Dispose();
        Check("Application-owned resources disposed", context.ResourcesDisposed && context.Popup.IsDisposed);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            BuildId = BuildInfo.Id, Mode = "Mock", Capability = BuildInfo.Capability,
            Assemblies = new[] { Environment.ProcessPath!, typeof(Program).Assembly.Location,
                typeof(BatteryRules).Assembly.Location, typeof(PeripheralBatteryTray.Windows.PnpNative).Assembly.Location }
                .Select(p => new { File = Path.GetFileName(p), Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) }),
            Environment = new { Runtime = System.Environment.Version.ToString(), Os = System.Environment.OSVersion.VersionString,
                MonitorDpi = testedDpi },
            Phase2Cases = new[] { new { Id = "C30", Passed = checks.Count == 23 && failures.Count == 0,
                Scope = "100 Mock coordinator refreshes, unchanged tray visual, original 23 in-process regressions and async shutdown." } },
            Scope = "Mock only; in-process callbacks, synthetic shell message and scaled layout rendering. No desktop automation, hardware query, actual Explorer restart or physical DPI change.",
            Passed = failures.Count == 0, Checks = checks, Failures = failures
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failures.Count == 0 ? 0 : 1;
    }

    private static int GuiObjects(uint kind)
    {
        using Process process = Process.GetCurrentProcess();
        return GetGuiResources(process.Handle, kind);
    }

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(nint process, uint flags);
}
