using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

namespace PeripheralBatteryTray.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        StartupOptions? options = StartupOptions.Parse(args);
        if (options is null) return 3;
        if (options.Exit) return (options.ExpectedPid is int pid
            ? SingleInstance.SignalExpectedExit(pid, options.ExpectedStartTicks!.Value)
            : SingleInstance.SignalExit()) ? 0 : 1;
        try { return Run(options); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return 4; }
    }

    private static int Run(StartupOptions options)
    {
        if (SingleInstance.LegacyExists())
        {
            if (!options.SelfTest && !options.HostCheck && !options.GattCheck && !options.AtkCheck) MessageBox.Show("请先退出 Phase 1 旧版，再运行此候选版。", "外设电量");
            return 2;
        }
        using var instance = new SingleInstance(options.Mock ? AppMode.Mock : AppMode.Real);
        if (!instance.IsPrimary)
        {
            if (options.SelfTest || options.HostCheck || options.GattCheck || options.AtkCheck) return 2;
            ExistingInstanceState state = SingleInstance.Probe();
            bool same = state == (options.Mock ? ExistingInstanceState.Mock : ExistingInstanceState.Real);
            if (same) return SingleInstance.SignalShow() ? 0 : 2;
            MessageBox.Show("已有实例运行或正在启动，请先退出后切换模式。", "外设电量");
            return 2;
        }

        ApplicationConfiguration.Initialize();
        if (options.AtkCheck) return AtkHostCheck.Run(instance, options.Report ?? "atk-host.local.json");
        AppComposition composition = AppComposition.Create(options);
        using var context = new TrayApplicationContext(instance, composition.Coordinator, settingsStore: composition.Settings,
            discoveryNotice: () => composition.Runtime?.DiscoveryNotice,
            suspend: () => composition.Runtime?.Suspend(), resume: () => composition.Runtime?.Resume(),
            atk: options.AllowsAtkTray ? new AtkTraySession(new Win32AtkHidTransport()) : null,
            startup: options.AllowsAtkTray ? new StartupRegistration(Path.Combine(AppContext.BaseDirectory, "PeripheralBatteryTray.exe")) : null);
        if (options.SelfTest)
        {
            return Phase1SelfTest.Run(context, options.Report ?? "phase1-tests.local.json");
        }
        // Start only once the WinForms UI dispatch target and message loop can process work.
        context.Popup.BeginInvoke(() => composition.Runtime?.Start(composition.Coordinator));
        if (options.HostCheck) return WindowsHostCheck.Run(context, composition.Runtime!, composition.Audit!, options.Report ?? "windows-host.local.json");
        if (options.GattCheck) return GattHostCheck.Run(context, composition.Runtime!, options.Report ?? "gatt-host.local.json");
        using var measurement = options.MeasurementReport is null ? null : new MeasurementSession(context, composition.Coordinator,
            () => composition.Runtime?.DiscoveryNotice, options.MeasurementReport);
        Application.Run(context);
        measurement?.Write();
        return 0;
    }
}
