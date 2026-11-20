using System.Runtime.InteropServices;
using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

namespace PeripheralBatteryTray.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly BatteryCoordinator _coordinator;
    private readonly SnapshotMailbox _mailbox;
    private readonly PollingSettingsStore _settingsStore;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ShellEventWindow _shell;
    private readonly RegisteredWaitHandle _showWait;
    private readonly RegisteredWaitHandle _exitWait;
    private readonly RegisteredWaitHandle _exactExitWait;
    private Icon? _ownedIcon;
    private BatteryTrayVisual? _lastVisual;
    private int _lastIconSize;
    private TrayVisual? _lastIconVisual;
    private TrayIconStyle? _lastIconStyle;
    private bool _refreshing;
    private bool _exiting;
    private bool _disposed;
    private SettingsForm? _settings;
    private readonly DiagnosticExportSession _export = new();
    private readonly Func<string?> _discoveryNotice;
    private readonly AtkTraySession? _atk;
    private readonly LowBatteryMonitor _lowBattery = new();
    private readonly ILowBatteryNotifier _notifier;
    private DisplaySettings _display;
    private readonly DeviceTrayIcons _deviceIcons;
    private readonly StartupRegistration? _startup;
    internal int DeviceIconCount => _deviceIcons.Count;
    internal IReadOnlyList<PresentedDevice> PresentedDevices => DevicePresentation.Project(CurrentSnapshot, _coordinator.RefreshPeriod, _atk?.Reading, _display);
    private long _notificationRequests, _notificationSuppressed, _notificationFailures;
    internal ToolStripMenuItem? AtkMenu { get; }
    internal ToolStripMenuItem? AtkAutomaticMenu { get; }
    internal int? DisplayedMinimumPercent => _lastVisual?.MinimumPercent;
    internal string? DisplayedTrayNumber => _lastIconStyle == TrayIconStyle.Number && _lastIconVisual is { } visual
        ? TrayIconRenderer.NumberText(visual) : null;
    internal DeviceListForm Popup { get; }
    internal IReadOnlyList<DeviceBatteryState> Devices { get; private set; } = [];
    internal int IconRenderCount { get; private set; }
    internal int RecoveryCount { get; private set; }
    internal int RefreshCount { get; private set; }
    internal int ListRenderCount { get; private set; }
    internal long RenderedSnapshotVersion { get; private set; } = -1;
    internal bool IsExiting => _exiting;
    internal bool TrayVisible => _tray.Visible;
    internal bool SettingsVisible => _settings?.Visible == true;
    internal bool ResourcesDisposed => _disposed;
    internal Task LastRefresh { get; private set; } = Task.CompletedTask;
    internal Task LastExit { get; private set; } = Task.CompletedTask;
    internal ShutdownResult? Shutdown { get; private set; }
    internal Func<Task<ShutdownResult>>? AdditionalShutdown { get; set; }
    internal BatterySnapshot CurrentSnapshot { get; private set; }
    internal ToolStripMenuItem RefreshMenu { get; }
    internal ToolStripMenuItem SettingsMenu { get; }
    internal ToolStripMenuItem ExitMenu { get; }

    public TrayApplicationContext(SingleInstance instance, BatteryCoordinator? coordinator = null,
        IEnumerable<BatteryDeviceState>? initialDevices = null, PollingSettingsStore? settingsStore = null,
        Func<string?>? discoveryNotice = null, Action? suspend = null, Action? resume = null, AtkTraySession? atk = null,
        ILowBatteryNotifier? notifier = null, Func<IDeviceTrayIcon>? deviceIconFactory = null, StartupRegistration? startup = null)
    {
        var mock = coordinator is null ? new MockBatteryProvider() : null;
        _coordinator = coordinator ?? new BatteryCoordinator(BatteryMode.Mock, [mock!]);
        _settingsStore = settingsStore ?? new PollingSettingsStore();
        _display = _settingsStore.Current.Display;
        _discoveryNotice = discoveryNotice ?? (() => null);
        CurrentSnapshot = _coordinator.Snapshot;
        _startup = CurrentSnapshot.Mode == BatteryMode.Real ? startup : null;
        _atk = CurrentSnapshot.Mode == BatteryMode.Mock ? null : atk;
        Popup = new DeviceListForm(RequestRefresh, ShowSettings, RequestExit, _atk is null ? null : RequestAtkRead);
        _ = Popup.Handle; // UI dispatch target exists even while the popup is hidden.
        Popup.DpiChanged += (_, _) => UpdateIcon();
        _menu = new ContextMenuStrip();
        _menu.Items.Add(new ToolStripMenuItem(CurrentSnapshot.Mode == BatteryMode.Mock ? "模拟演示 · 未读取真实设备" : "Windows 系统报告 · 时效未知") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        RefreshMenu = new ToolStripMenuItem("刷新", null, (_, _) => RequestRefresh());
        SettingsMenu = new ToolStripMenuItem("设置", null, (_, _) => ShowSettings());
        ExitMenu = new ToolStripMenuItem("退出", null, (_, _) => RequestExit());
        _menu.Items.AddRange([RefreshMenu, SettingsMenu, new ToolStripSeparator(), ExitMenu]);
        _menu.Items.Insert(4, new ToolStripMenuItem("导出诊断…", null, async (_, _) => await ExportDiagnosticsAsync()));
        if (_atk is not null)
        {
            AtkMenu = new ToolStripMenuItem("读取 ATK 电量", null, (_, _) => RequestAtkRead());
            _menu.Items.Insert(2, AtkMenu);
            AtkAutomaticMenu = new ToolStripMenuItem("ATK 自动刷新：关闭") { Enabled = false };
            _menu.Items.Insert(3, AtkAutomaticMenu);
        }
        _tray = new NotifyIcon { ContextMenuStrip = _menu, Text = "外设电量（模拟）" };
        _deviceIcons = new DeviceTrayIcons(deviceIconFactory ?? (() => new ShellDeviceTrayIcon(_menu, TogglePopup)));
        _notifier = notifier ?? new TrayLowBatteryNotifier(_tray);
        _lowBattery.Configure(CurrentSnapshot.Mode == BatteryMode.Real && _settingsStore.Current.LowBatteryEnabled,
            _settingsStore.Current.LowBatteryThreshold);
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePopup(); };
        _shell = new ShellEventWindow(RecoverTray, () => UpdateIcon(),
            () => { if (!_exiting) { _lowBattery.ResetBaselines(); _atk?.Suspend(); suspend?.Invoke(); } },
            () => { if (!_exiting) { _lowBattery.ResetBaselines(); _atk?.Resume(); resume?.Invoke(); } });
        _showWait = ThreadPool.RegisterWaitForSingleObject(instance.ShowEvent!, (_, _) => PostToUi(ShowPopup), null, Timeout.Infinite, false);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(instance.ExitEvent!, (_, _) => PostToUi(RequestExit), null, Timeout.Infinite, false);
        _exactExitWait = ThreadPool.RegisterWaitForSingleObject(instance.ExactExitEvent!, (_, _) => PostToUi(RequestExit), null, Timeout.Infinite, false);
        _mailbox = new SnapshotMailbox(action => Popup.BeginInvoke(action), DisplaySnapshot);
        _coordinator.SnapshotChanged += _mailbox.Publish;
        if (_atk is not null)
        {
            _atk.Changed += AtkChanged;
            _atk.AttachDispatcher(PostToUi);
            _atk.Configure(_settingsStore.Current.AtkAutoEnabled, _settingsStore.Current.AtkMinutes);
        }
        foreach (var device in initialDevices ?? mock?.ReadModelsAsync(default).Result ?? []) _coordinator.UpsertDevice(device);
        RequestRefresh();
        _tray.Visible = true;
    }

    internal void RequestAtkRead()
    {
        if (!_exiting) _atk?.TryRead();
    }

    private void AtkChanged()
    {
        if (_exiting || _disposed || _atk is null) return;
        var reading = _atk.Reading;
        if (AtkMenu is not null) { AtkMenu.Enabled = reading.CanRead; AtkMenu.Text = reading.Busy ? "正在读取 ATK 电量…" : "读取 ATK 电量"; }
        if (AtkAutomaticMenu is not null) AtkAutomaticMenu.Text = reading.AutomaticStatus;
        if (Popup.Visible) RenderList();
        UpdateIcon();
        EvaluateLowBattery();
    }

    private void PostToUi(Action action)
    {
        if (_exiting || _disposed) return;
        try { Popup.BeginInvoke(() => { if (!_exiting) action(); }); }
        catch (InvalidOperationException) when (_exiting || _disposed) { }
    }

    internal void RequestRefresh()
    {
        if (!_exiting && !_refreshing) LastRefresh = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _refreshing = true;
        RefreshMenu.Enabled = false;
        try
        {
            _coordinator.Refresh();
            // One UI barrier follows the mailbox callback; no blocking wait on the UI thread.
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Popup.BeginInvoke(() => shown.TrySetResult());
            await shown.Task;
            if (_exiting) return;
            RefreshCount++;
        }
        catch (InvalidOperationException) when (_exiting || _disposed) { }
        finally
        {
            _refreshing = false;
            if (!_exiting) RefreshMenu.Enabled = true;
        }
    }

    private void DisplaySnapshot(BatterySnapshot snapshot)
    {
        if (_exiting || _disposed || snapshot.Version < CurrentSnapshot.Version) return;
        CurrentSnapshot = snapshot;
        var presented = PresentedDevices;
        if (snapshot.Mode == BatteryMode.Mock || Popup.Visible) RenderList(presented);
        // Kept only for the original Mock regression harness; real UI/aggregation use the domain snapshot.
        Devices = snapshot.Mode == BatteryMode.Mock ? snapshot.Devices.Select(model =>
        {
            var reading = model.Components.FirstOrDefault()?.Source.LastSuccess;
            bool connected = BatteryRules.GetConnection(model.Binding, BatteryMode.Mock) == ConnectionState.Connected;
            return new DeviceBatteryState { DeviceId = model.Binding.DeviceId, DisplayName = model.DisplayName,
                ConnectionType = model.ConnectionType, Connected = connected,
                ReadStatus = connected ? BatteryReadStatus.Success : BatteryReadStatus.Offline,
                BatteryPercent = reading?.Value.Percent, BatteryBand = reading?.Value.Band, Source = "Mock / 模拟",
                LastSuccessfulReadUtc = reading?.ObservedAtUtc };
        }).ToArray() : [];
        UpdateIcon(presented);
        EvaluateLowBattery();
    }

    private void EvaluateLowBattery()
    {
        if (_exiting || _disposed || !_lowBattery.Enabled || CurrentSnapshot.Mode != BatteryMode.Real) return;
        var alerts = _lowBattery.Observe(LowBatteryProjection.Samples(CurrentSnapshot, TimeProvider.System,
            _coordinator.RefreshPeriod, _atk?.Reading, _display));
        if (alerts.Count == 0) return;
        NotificationDelivery delivery;
        try { delivery = _notifier.Send(alerts); } catch { delivery = NotificationDelivery.Failed; }
        switch (delivery)
        {
            case NotificationDelivery.Requested: _notificationRequests++; break;
            case NotificationDelivery.Suppressed: _notificationSuppressed++; break;
            default: _notificationFailures++; break;
        }
    }

    private void RenderList(IReadOnlyList<PresentedDevice>? presented = null)
    {
        Popup.SetPresentation(CurrentSnapshot, _coordinator.RefreshPeriod, presented ?? PresentedDevices);
        Popup.SetDiscoveryNotice(_discoveryNotice());
        RenderedSnapshotVersion = CurrentSnapshot.Version;
        ListRenderCount++;
    }

    private void UpdateIcon(IReadOnlyList<PresentedDevice>? presented = null)
    {
        if (_exiting || _disposed) return;
        var devices = presented ?? PresentedDevices;
        BatteryTrayVisual visual = DevicePresentation.Aggregate(devices, CurrentSnapshot.Mode);
        TrayVisual iconVisual = _display.IconStyle == TrayIconStyle.Number ? DevicePresentation.NumberAggregate(devices)
            : new(visual.MinimumPercent, visual.HasOffline, visual.HasUnknown);
        string tooltip = _display.IconStyle == TrayIconStyle.Number && iconVisual.MinimumPercent is int shown
            ? (CurrentSnapshot.Mode == BatteryMode.Mock ? "模拟 · " : "") + $"外设电量 · 显示最低 {shown}%"
                + (devices.Any(d => !d.Hidden && d.IconUsesSystemReport) ? " · 含系统参考" : "") : visual.Tooltip;
        _tray.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
        uint dpi = GetDpiForSystem();
        int size = Math.Clamp(GetSystemMetricsForDpi(49, dpi), 16, 64);
        _deviceIcons.Update(devices, _display.IconMode, size, _display.IconStyle);
        _lastVisual = visual;
        if (_lastIconVisual == iconVisual && _lastIconStyle == _display.IconStyle && _lastIconSize == size) return;
        Icon next = TrayIconRenderer.Create(iconVisual, size, _display.IconStyle);
        _tray.Icon = next;
        Icon? previous = _ownedIcon;
        _ownedIcon = next;
        previous?.Dispose();
        _lastIconVisual = iconVisual;
        _lastIconStyle = _display.IconStyle;
        _lastIconSize = size;
        IconRenderCount++;
    }

    private void RecoverTray()
    {
        if (_exiting) return;
        _tray.Visible = false;
        UpdateIcon();
        _tray.Visible = true;
        _deviceIcons.Recover();
        RecoveryCount++;
    }

    internal void SimulateTaskbarCreated() => _shell.SimulateTaskbarCreated();
    internal void TogglePopup() { if (Popup.Visible) Popup.Hide(); else ShowPopup(); }
    internal void ShowPopup()
    {
        if (_exiting) return;
        _settings?.Close();
        // Input can overtake a queued mailbox callback. Read one coherent latest snapshot
        // before the first paint, updating the tray and list from the same version.
        DisplaySnapshot(_coordinator.Snapshot);
        if (CurrentSnapshot.Mode != BatteryMode.Mock && !Popup.Visible) RenderList();
        Popup.ShowNearCursor();
        _coordinator.Refresh(reason: RefreshReason.ListOpened);
    }

    private void ShowSettings()
    {
        Popup.Hide();
        if (_settings is null || _settings.IsDisposed)
        {
            _settings = new SettingsForm(_settingsStore, CurrentSnapshot.Mode, ApplySettings, _atk is not null, PresentedDevices, _startup);
            _settings.FormClosed += (_, _) => _settings = null;
        }
        _settings.Show();
        _settings.Activate();
    }

    internal void CloseSettings() => _settings?.Close();
    internal void ApplySettings(PollingSettings settings)
    {
        if (_exiting || _disposed) return;
        if (_coordinator.RefreshPeriod != TimeSpan.FromMinutes(settings.Minutes)) _lowBattery.ResetBaselines();
        _display = settings.Display;
        _lowBattery.Configure(CurrentSnapshot.Mode == BatteryMode.Real && settings.LowBatteryEnabled, settings.LowBatteryThreshold);
        _coordinator.SetRefreshMinutes(settings.Minutes); _atk?.Configure(settings.AtkAutoEnabled, settings.AtkMinutes);
        if (Popup.Visible) RenderList();
        UpdateIcon();
        EvaluateLowBattery();
    }

    internal DiagnosticReport CaptureDiagnostics() => _coordinator.CreateDiagnosticReport(BuildInfo.Id, BuildInfo.Capability, Shutdown)
        with { SchemaVersion = 3, Atk = _atk?.CaptureDiagnostics(), LowBattery = new(_lowBattery.Enabled,
            _lowBattery.Threshold, _lowBattery.Crossings, _notificationRequests, _notificationSuppressed, _notificationFailures) };
    private async Task ExportDiagnosticsAsync()
    {
        if (_exiting || _export.IsActive) return;
        var result = await _export.ExportAsync(() =>
        {
            using var dialog = new SaveFileDialog { Filter = "JSON 诊断 (*.json)|*.json", FileName = "battery-diagnostics.json", AddExtension = true, OverwritePrompt = true };
            return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
        }, CaptureDiagnostics);
        if (_exiting || result is ExportResult.Cancelled or ExportResult.Busy) return;
        MessageBox.Show(result == ExportResult.Saved ? "诊断已保存。发送给他人前请再次检查内容。" : "诊断保存失败，请检查目标位置。", "外设电量");
    }

    internal void RequestExit()
    {
        if (_exiting) return;
        _exiting = true;
        _mailbox.Dispose();
        RefreshMenu.Enabled = SettingsMenu.Enabled = ExitMenu.Enabled = false;
        if (AtkMenu is not null) AtkMenu.Enabled = false;
        _tray.Visible = false;
        _deviceIcons.Dispose();
        _settings?.Close();
        LastExit = ExitAsync();
    }

    private async Task ExitAsync()
    {
        try
        {
            var stopping = new List<Task<ShutdownResult>> { _coordinator.StopAsync() };
            if (AdditionalShutdown is not null) stopping.Add(AdditionalShutdown());
            if (_atk is not null) stopping.Add(_atk.StopAsync());
            var all = await Task.WhenAll(stopping);
            Shutdown = new(all.All(s => s.FullyStopped), all.Sum(s => s.UnresolvedOperations), all.Sum(s => s.CleanupErrors));
        }
        catch (Exception) { Shutdown = new(false, 0, 1); }
        if (_disposed) return;
        Popup.AllowClose();
        Popup.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _exiting = true;
            _mailbox.Dispose();
            _coordinator.SnapshotChanged -= _mailbox.Publish;
            _ = _coordinator.StopAsync(); // Bounded async stop already initiated; never block the UI Dispose.
            if (_atk is not null) { _atk.Changed -= AtkChanged; _ = _atk.StopAsync(); }
            _showWait.Unregister(null);
            _exitWait.Unregister(null);
            _exactExitWait.Unregister(null);
            _shell.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _deviceIcons.Dispose();
            _menu.Dispose();
            _ownedIcon?.Dispose();
            _settings?.Dispose();
            Popup.AllowClose();
            Popup.Dispose();
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
