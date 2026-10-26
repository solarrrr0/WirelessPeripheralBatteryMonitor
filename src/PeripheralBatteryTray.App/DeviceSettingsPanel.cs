using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.App;

internal sealed class DeviceSettingsPanel : TableLayoutPanel
{
    internal DataGridView Grid { get; }
    internal ComboBox SortChoice { get; }
    internal ComboBox IconChoice { get; }
    internal DeviceSettingsPanel(DisplaySettings current, IReadOnlyList<PresentedDevice> devices)
    {
        Dock = DockStyle.Fill; Padding = new(10); ColumnCount = 1; RowCount = 4;
        ColumnStyles.Add(new(SizeType.Percent, 100));
        RowStyles.Add(new(SizeType.AutoSize)); RowStyles.Add(new(SizeType.AutoSize));
        RowStyles.Add(new(SizeType.Percent, 100)); RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill,
            Text = "别名留空使用原名。隐藏后不显示列表、图标或低电量提醒。\n隐藏不关闭底层读取；自动读取请在“刷新与提醒”中关闭。" }, 0, 0);
        var choices = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new(0, 8, 0, 8) };
        SortChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        SortChoice.Items.AddRange(["发现顺序", "设备名称", "有效电量从低到高"]); SortChoice.SelectedIndex = (int)current.Sort;
        IconChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
        IconChoice.Items.AddRange(["一个总图标", "每设备图标（保留总图标）"]); IconChoice.SelectedIndex = (int)current.IconMode;
        choices.Controls.Add(new Label { Text = "排序", AutoSize = true }); choices.Controls.Add(SortChoice);
        choices.Controls.Add(new Label { Text = "托盘", AutoSize = true }); choices.Controls.Add(IconChoice);
        Controls.Add(choices, 0, 1);
        Grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells };
        Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Device", HeaderText = "设备", ReadOnly = true, FillWeight = 45 });
        Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Alias", HeaderText = "别名（最多48字符）", MaxInputLength = DisplaySettings.MaxAliasLength, FillWeight = 40 });
        Grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Hidden", HeaderText = "隐藏", FillWeight = 15 });
        Grid.DataError += (_, e) => { e.ThrowException = false; };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            if (device.PreferenceKey is not { } key || !seen.Add(key)) continue;
            var preference = current.For(key);
            int i = Grid.Rows.Add(device.OriginalName, preference.Alias, preference.Hidden); Grid.Rows[i].Tag = key;
        }
        foreach (var pair in current.Devices.Where(p => !seen.Contains(p.Key)))
        {
            int i = Grid.Rows.Add("未发现的已保存设备", pair.Value.Alias, pair.Value.Hidden); Grid.Rows[i].Tag = pair.Key;
        }
        Controls.Add(Grid, 0, 2);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var reset = new Button { Text = "恢复所选默认", AutoSize = true };
        reset.Click += (_, _) => { if (Grid.CurrentRow is { } row) { Grid.CancelEdit(); row.Cells[1].Value = ""; row.Cells[2].Value = false; } };
        footer.Controls.Add(reset);
        footer.Controls.Add(new Label { AutoSize = true, Text = "最多保存64个设备；独立图标最多16个。\n设备列表在打开设置时更新；Windows可能收起托盘图标。", Margin = new(8) });
        Controls.Add(footer, 0, 3);
    }
    internal bool TryRead(out DisplaySettings display, out string error)
    {
        display = DisplaySettings.Default; error = "";
        if (!Grid.EndEdit()) { error = "请先完成设备设置的编辑。"; return false; }
        var preferences = new Dictionary<string, DevicePreference>(StringComparer.Ordinal);
        foreach (DataGridViewRow row in Grid.Rows)
        {
            string alias = row.Cells[1].Value?.ToString() ?? ""; bool hidden = row.Cells[2].Value is true;
            if (!DisplaySettings.ValidAlias(alias)) { error = "别名最多48字符，不能含换行或控制字符。"; return false; }
            alias = alias.Trim();
            if (alias.Length > 0 || hidden) preferences.Add((string)row.Tag!, new(alias, hidden));
        }
        if (preferences.Count > DisplaySettings.MaxDevices) { error = "最多为64个设备保存别名或隐藏设置，请先恢复部分默认值。"; return false; }
        display = new((DeviceSort)SortChoice.SelectedIndex, (TrayIconMode)IconChoice.SelectedIndex, preferences);
        return true;
    }
}
