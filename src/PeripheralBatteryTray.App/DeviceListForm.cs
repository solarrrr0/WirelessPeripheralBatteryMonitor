using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.App;

internal sealed class DeviceListForm : Form
{
    private TableLayoutPanel _rows = null!;
    private Label _updated = null!;
    private Font? _titleFont;
    private Label _mode = null!;
    private readonly Action _refresh, _settings, _quit;
    private readonly Action? _readAtk;
    private readonly ToolTip _deviceDetails = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 100, ShowAlways = true };
    internal bool ContentInitialized { get; private set; }
    private bool _allowClose;
    internal int DeviceRowCount => ContentInitialized ? _rows.Controls.Count : 0;

    public DeviceListForm(Action refresh, Action settings, Action quit, Action? readAtk = null)
    {
        _refresh = refresh; _settings = settings; _quit = quit;
        _readAtk = readAtk;
        Text = "外设电量 · 模拟版";
        ShowInTaskbar = false;
        TopMost = true;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 420);
        BackColor = SystemColors.Window;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; } };
        Deactivate += (_, _) => Hide();
    }

    private void EnsureContent()
    {
        if (ContentInitialized) return;
        SuspendLayout();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _titleFont = new Font(Font.FontFamily, 14, FontStyle.Bold);
        layout.Controls.Add(new Label { Text = "外设电量", AutoSize = true,
            Font = _titleFont, Margin = new Padding(0, 0, 0, 6) }, 0, 0);
        _mode = new Label { Text = "模拟演示 · 所有数值均为 Mock，未读取真实设备。", AutoSize = true,
            ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 12) };
        layout.Controls.Add(_mode, 0, 1);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        _rows = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(_rows);
        layout.Controls.Add(scroll, 0, 2);
        _updated = new Label { Text = "正在准备模拟数据…", AutoSize = true,
            ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 0, 8) };
        layout.Controls.Add(_updated, 0, 3);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.Add(MakeButton("刷新", _refresh));
        if (_readAtk is not null) actions.Controls.Add(MakeButton("读取 ATK 电量", _readAtk));
        actions.Controls.Add(MakeButton("设置", _settings));
        actions.Controls.Add(MakeButton("退出", _quit));
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);
        ContentInitialized = true;
        ResumeLayout(true);
    }

    private static Button MakeButton(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        button.Click += (_, _) => action();
        return button;
    }

    internal void SetDevices(IReadOnlyList<DeviceBatteryState> devices)
    {
        EnsureContent();
        _rows.SuspendLayout();
        if (_rows.Controls.Count != devices.Count)
        {
            foreach (Control control in _rows.Controls.Cast<Control>().ToArray()) control.Dispose();
            _rows.RowStyles.Clear();
            _rows.RowCount = devices.Count;
            for (int i = 0; i < devices.Count; i++)
            {
                _rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                _rows.Controls.Add(new DeviceRow(_deviceDetails), 0, i);
            }
        }
        for (int i = 0; i < devices.Count; i++) ((DeviceRow)_rows.GetControlFromPosition(0, i)!).SetDevice(devices[i]);
        _rows.ResumeLayout(true);
        _updated.Text = $"已刷新 {DateTime.Now:HH:mm:ss} · 来源：Mock / 模拟";
    }

    internal void SetSnapshot(BatterySnapshot snapshot, TimeSpan refreshPeriod, AtkTrayReading? atk = null)
        => SetPresentation(snapshot, refreshPeriod, DevicePresentation.Project(snapshot, refreshPeriod, atk, DisplaySettings.Default));

    internal void SetPresentation(BatterySnapshot snapshot, TimeSpan refreshPeriod, IReadOnlyList<PresentedDevice> devices)
    {
        EnsureContent();
        Text = snapshot.Mode == BatteryMode.Mock ? "外设电量 · 模拟版" : "外设电量 · Windows 系统报告";
        _mode.Text = snapshot.Mode == BatteryMode.Mock ? "模拟演示 · 所有数值均为 Mock，未读取真实设备。" : "系统报告时效未知；当前设备直读另标来源。";
        // Reuse row allocation/layout; source-aware text and current values are supplied by Core.
        var visible = devices.Where(d => !d.Hidden).ToArray();
        SetDevices(visible.Select(device => new DeviceBatteryState {
            DeviceId = device.Id, DisplayName = device.Name, ConnectionType = device.Model?.ConnectionType ?? "2.4G",
            Connected = false, ReadStatus = BatteryReadStatus.Unknown, Source = "" })
            .ToArray());
        for (int i = 0; i < visible.Length; i++)
        {
            var row = (DeviceRow)_rows.GetControlFromPosition(0, i)!;
            if (visible[i].Model is { } model) row.SetModel(model, snapshot.Mode, refreshPeriod);
            else if (visible[i].Atk is { } reading) row.SetAtk(reading);
            row.SetName(visible[i].Name);
        }
        _updated.Text = $"快照 {snapshot.CreatedAtUtc.ToLocalTime():HH:mm:ss} · "
            + (snapshot.Mode == BatteryMode.Mock ? "Mock / 模拟" : "系统缓存与设备直读分别标注");
        int hidden = devices.Count - visible.Length;
        if (hidden > 0) _updated.Text += $" · 已隐藏 {hidden} 个（设置中恢复）";
        if (visible.Length == 0) _updated.Text += " · 无显示设备";
    }

    internal void ShowNearCursor()
    {
        EnsureContent();
        Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size maximum = new(Math.Max(1, work.Width - 16), Math.Max(1, work.Height - 16));
        MaximumSize = maximum;
        int margin = (int)Math.Round(8 * DeviceDpi / 96d);
        Location = new Point(Math.Clamp(Cursor.Position.X - Width + 20, work.Left,
            Math.Max(work.Left, work.Right - Width - margin)),
            Math.Clamp(Cursor.Position.Y - Height - margin, work.Top, Math.Max(work.Top, work.Bottom - Height)));
        Show();
        // Re-clamp after PerMonitorV2 has applied the destination monitor's DPI.
        Location = new Point(Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width)),
            Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height)));
        Activate();
    }

    internal void SetDiscoveryNotice(string? notice)
    { EnsureContent(); if (notice is not null) _mode.Text = notice; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    internal void AllowClose() => _allowClose = true;

    internal bool AllRowsFit => ContentInitialized && _rows.Parent is Panel panel && _rows.Height <= panel.ClientSize.Height;

    internal string GetDeviceDetails(int index) => _deviceDetails.GetToolTip(_rows.GetControlFromPosition(0, index)!) ?? "";

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _titleFont?.Dispose(); _deviceDetails.Dispose(); }
    }

    private sealed class DeviceRow : TableLayoutPanel
    {
        private readonly Label _name = new() { AutoSize = true, Dock = DockStyle.Fill };
        private readonly Label _battery = new() { AutoSize = true, TextAlign = ContentAlignment.TopRight };
        private readonly Label _details = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
        private readonly Font _valueFont;
        private readonly ToolTip _toolTip;

        public DeviceRow(ToolTip toolTip)
        {
            _toolTip = toolTip;
            Dock = DockStyle.Top;
            AutoSize = true;
            ColumnCount = 2;
            RowCount = 2;
            Margin = new Padding(0, 0, 0, 10);
            Padding = new Padding(8);
            BackColor = SystemColors.Control;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _valueFont = new Font(Font.FontFamily, 12, FontStyle.Bold);
            _battery.Font = _valueFont;
            Controls.Add(_name, 0, 0);
            Controls.Add(_battery, 1, 0);
            Controls.Add(_details, 0, 1);
            SetColumnSpan(_details, 2);
        }

        public void SetDevice(DeviceBatteryState device)
        {
            SetDetailsHint(""); _details.Visible = true;
            _name.Text = device.DisplayName;
            _battery.Text = device.BatteryText;
            _battery.ForeColor = device.Connected ? SystemColors.ControlText : SystemColors.GrayText;
            string time = device.LastSuccessfulReadUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "无";
            _details.Text = $"{device.ConnectionType} · {(device.Connected ? "在线" : "离线")} · {device.ChargingText}\n"
                + $"Mock / 模拟 · 上次成功：{time}";
        }

        public void SetName(string name) => _name.Text = name;

        public void SetAtk(AtkTrayReading reading)
        {
            SetDetailsHint(""); _details.Visible = true;
            _name.Text = "ATK Duckbill Ultra";
            _battery.Text = reading.Percent is int current ? $"{current}%" : reading.LastPercent is int last ? $"{last}%（上次）" : "未知";
            _battery.ForeColor = reading.Percent is not null ? SystemColors.ControlText : SystemColors.GrayText;
            _details.Text = $"2.4G 接收器 · {(reading.Automatic ? "自动" : "手动")}读取 · {reading.ObservedAtUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "尚无读数"}\n{reading.Status}\n{reading.AutomaticStatus}";
        }

        public void SetModel(BatteryDeviceState device, BatteryMode mode, TimeSpan refreshPeriod)
        {
            BatteryPresentation view = BatteryRules.Present(device, mode, TimeProvider.System, refreshPeriod);
            _name.Text = device.DisplayName;
            _battery.Text = mode == BatteryMode.Real && view.DisplayPercent is int percent ? $"{percent}%" : view.BatteryText;
            _battery.ForeColor = view.Connection == ConnectionState.Disconnected ? SystemColors.GrayText : SystemColors.ControlText;
            string connection = view.Connection switch { ConnectionState.Connected => "在线", ConnectionState.Disconnected => "离线", _ => "连接未知" };
            var details = new List<string> { $"{device.ConnectionType} · {connection} · 充电状态未知" };
            if (mode == BatteryMode.Mock) details.Add("Mock / 模拟 · 所有数值均为示例");
            else
            {
                foreach (var component in device.Components.Where(c => !c.RemovedFromDirectory).OrderBy(c => c.DisplayOrder))
                {
                    bool current = BatteryRules.IsCurrentValue(component, device.Binding, mode, TimeProvider.System, refreshPeriod);
                    string time = component.Source.LastSuccessfulReadUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "无";
                    details.Add($"电池服务 {component.DisplayOrder + 1} · " + (current ? "GATT 设备直读" : "历史 / 未读取")
                        + $" · {component.Source.LastSuccess?.Value.Text ?? "未知"} · {time}"
                        + (component.Source.IsRefreshing ? " · 刷新中" : ""));
                }
                int alias = 0;
                foreach (var candidate in BatteryRules.GetSystemReports(device))
                    details.Add($"系统节点 {++alias} · 系统报告 {candidate.LastSuccess!.Value.Text} · 时效未知 · 观察于 {candidate.LastSuccess.ObservedAtUtc.ToLocalTime():HH:mm:ss}");
            }
            string description = view.BatteryText + "\n" + string.Join("\n", details);
            if (view.DisplayPercent is not null && view.CurrentPercents.Count == 0)
                description += "\n显示系统报告中的较低电量，仅供参考；不代表实时设备读取。";
            _details.Text = mode == BatteryMode.Mock ? string.Join("\n", details) : "";
            _details.Visible = mode == BatteryMode.Mock;
            SetDetailsHint(mode == BatteryMode.Real ? description : "");
        }

        private void SetDetailsHint(string text)
        {
            foreach (Control control in new Control[] { this, _name, _battery })
            { _toolTip.SetToolTip(control, text); control.AccessibleDescription = text; }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _valueFont.Dispose();
        }
    }
}
