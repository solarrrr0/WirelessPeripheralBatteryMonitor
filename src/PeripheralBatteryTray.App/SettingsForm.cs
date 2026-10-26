using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

namespace PeripheralBatteryTray.App;

internal sealed class SettingsForm : Form
{
    internal CheckBox AtkEnabled { get; }
    internal NumericUpDown AtkInterval { get; }
    internal NumericUpDown WindowsInterval { get; }
    internal CheckBox LowBatteryEnabled { get; }
    internal NumericUpDown LowBatteryThreshold { get; }
    internal Button ApplyButton { get; }
    internal DeviceSettingsPanel DeviceSettings { get; }
    internal StartupSettingsPanel StartupSettings { get; }
    public SettingsForm(PollingSettingsStore store, BatteryMode mode, Action<PollingSettings> apply, bool atkAvailable = false,
        IReadOnlyList<PresentedDevice>? devices = null, StartupRegistration? startup = null)
    {
        SuspendLayout();
        Text = "外设电量 · 设置";
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(700, 560);
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 3 };
        outer.ColumnStyles.Add(new(SizeType.Percent, 100));
        outer.RowStyles.Add(new(SizeType.Percent, 100)); outer.RowStyles.Add(new(SizeType.AutoSize)); outer.RowStyles.Add(new(SizeType.AutoSize));
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var refreshTab = new TabPage("刷新与提醒"); var deviceTab = new TabPage("设备与托盘");
        tabs.TabPages.Add(refreshTab); tabs.TabPages.Add(deviceTab); outer.Controls.Add(tabs, 0, 0);
        DeviceSettings = new(store.Current.Display, devices ?? []); deviceTab.Controls.Add(DeviceSettings);
        var startupTab = new TabPage("登录启动"); tabs.TabPages.Add(startupTab);
        StartupSettings = new(mode == BatteryMode.Real ? startup : null); startupTab.Controls.Add(StartupSettings);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 7, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, AutoSize = true,
            Text = mode == BatteryMode.Mock ? "当前为 Mock 模拟演示，仅按需刷新示例。\n此处设置仅保存在内存，未修改用户配置。\n模拟模式不会发送系统低电量提醒，也不管理自启动。"
                : "系统电量报告时效未知。刷新周期为整数 1–30 分钟。\n仅保存成功后生效；修改设置不会额外读取电量。\n低电量提醒默认关闭；登录启动见单独标签页。" }, 0, 0);
        var interval = WindowsInterval = new NumericUpDown { Minimum = 1, Maximum = 30, Value = store.Current.Minutes, Width = 100 };
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        row.Controls.Add(new Label { Text = "Windows 刷新间隔（分钟）", AutoSize = true }); row.Controls.Add(interval); layout.Controls.Add(row, 0, 1);
        AtkEnabled = new CheckBox { Text = "自动读取 ATK Duckbill Ultra 电量", AutoSize = true,
            Checked = atkAvailable && store.Current.AtkAutoEnabled, Enabled = atkAvailable };
        AtkInterval = new NumericUpDown { Minimum = 5, Maximum = 30, Value = store.Current.AtkMinutes, Width = 100, Enabled = atkAvailable };
        var atkRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        atkRow.Controls.Add(AtkEnabled); atkRow.Controls.Add(new Label { Text = "间隔（分钟）", AutoSize = true }); atkRow.Controls.Add(AtkInterval);
        layout.Controls.Add(atkRow, 0, 2);
        layout.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 12),
            Text = atkAvailable ? "启用后及下次启动时将自动查询接收器（首次等待10秒）。\n使用 ATK 官网或驱动时可先关闭；充电状态未知。\n有效电量保留5分钟，之后显示上次读数。" : "本模式不启用 ATK 自动查询。" }, 0, 3);
        LowBatteryEnabled = new CheckBox { Text = "低电量提醒", AutoSize = true, Checked = store.Current.LowBatteryEnabled,
            Enabled = mode == BatteryMode.Real };
        LowBatteryThreshold = new NumericUpDown { Minimum = 1, Maximum = 50, Value = store.Current.LowBatteryThreshold,
            Width = 75, Enabled = mode == BatteryMode.Real };
        var notificationRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        notificationRow.Controls.Add(LowBatteryEnabled);
        notificationRow.Controls.Add(new Label { Text = "阈值（%）", AutoSize = true }); notificationRow.Controls.Add(LowBatteryThreshold);
        layout.Controls.Add(notificationRow, 0, 4);
        layout.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 10),
            Text = "仅有效直读从高于阈值降到阈值及以下时提醒一次。\n首次读数仅建基线；回升5个百分点后才再次提醒。\n离线、历史及系统缓存不触发；系统不允许时跳过，不补发。" }, 0, 5);
        var status = new Label { Text = store.Current.Notice ?? "", AutoSize = true, ForeColor = SystemColors.GrayText }; outer.Controls.Add(status, 0, 1);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var save = ApplyButton = new Button { Text = "应用", AutoSize = true, Enabled = store.Current.CanSave };
        save.Click += (_, _) => { int minutes = (int)interval.Value;
            if (!DeviceSettings.TryRead(out var display, out string error)) { status.Text = error; return; }
            if (store.TrySave(minutes, atkAvailable && AtkEnabled.Checked, (int)AtkInterval.Value,
                mode == BatteryMode.Real && LowBatteryEnabled.Checked, (int)LowBatteryThreshold.Value, display))
            { apply(store.Current); status.Text = mode == BatteryMode.Mock ? "已应用（仅本次模拟）" : "已保存并生效"; }
            else status.Text = "保存失败，继续使用原设置。"; };
        var close = new Button { Text = "关闭", AutoSize = true, Anchor = AnchorStyles.Right, DialogResult = DialogResult.OK };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(close); buttons.Controls.Add(save); outer.Controls.Add(buttons, 0, 2);
        AcceptButton = close;
        CancelButton = close;
        refreshTab.Controls.Add(layout); Controls.Add(outer);
        ResumeLayout(true);
    }
}
