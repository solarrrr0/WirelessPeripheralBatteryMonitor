using Microsoft.Win32;

namespace PeripheralBatteryTray.App;

internal sealed record StartupValue(string? Command, bool Readable = true);
internal interface IStartupRegistry
{
    StartupValue Read();
    bool TryChange(StartupValue expected, string? command);
}
internal sealed record StartupState(bool Enabled, bool CanChange, string Notice);

/// <summary>Explicit, user-level login registration. Construction and ordinary Apply do not write the registry.</summary>
internal sealed class StartupRegistration
{
    private readonly IStartupRegistry _registry;
    internal string Command { get; }
    internal StartupRegistration(string executable, IStartupRegistry? registry = null)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') || executable.Any(char.IsControl)
            || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A full executable path is required.");
        Command = "\"" + Path.GetFullPath(executable) + "\"";
        _registry = registry ?? new UserStartupRegistry();
    }
    internal StartupState Read() => Describe(_registry.Read());
    private StartupState Describe(StartupValue value)
    {
        if (!value.Readable) return new(false, false, "无法读取当前用户的启动项。请检查系统权限。");
        if (value.Command is null) return Command.Length <= 260 ? new(false, true, "登录自启动已关闭。")
            : new(false, false, "当前目录过长，启动命令超过260字符。请解压到较短路径后再启用。");
        if (string.Equals(value.Command, Command, StringComparison.OrdinalIgnoreCase)) return new(true, true, "此版本已注册登录自启动。");
        return new(false, false, "已有启动项指向其他位置。请先在原版本关闭自启动，再在此版本开启。");
    }
    internal StartupState SetEnabled(bool enabled)
    {
        var value = _registry.Read(); var current = Describe(value);
        if (!current.CanChange) return current;
        if (current.Enabled == enabled) return current;
        if (!_registry.TryChange(value, enabled ? Command : null))
            return Read() with { Notice = "启动项保存失败或已被其他程序修改；请重新打开此页检查。" };
        var actual = Read();
        return actual.Enabled == enabled && actual.CanChange ? actual
            : actual with { Notice = "启动项未能确认，请重新打开此页检查。" };
    }
}

internal sealed class UserStartupRegistry : IStartupRegistry
{
    private const string PathKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PeripheralBatteryTray";
    public StartupValue Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PathKey, false);
            object? value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value is null ? new(null) : value is string text && key!.GetValueKind(ValueName) == RegistryValueKind.String
                ? new(text) : new(null, false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { return new(null, false); }
    }
    public bool TryChange(StartupValue expected, string? command)
    {
        try
        {
            // Do not replace another path or value observed after the user opened the page.
            if (Read() != expected) return false;
            using var key = command is null ? Registry.CurrentUser.OpenSubKey(PathKey, true) : Registry.CurrentUser.CreateSubKey(PathKey, true);
            if (command is null) key?.DeleteValue(ValueName, false); else key!.SetValue(ValueName, command, RegistryValueKind.String);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { return false; }
    }
}

internal sealed class StartupSettingsPanel : TableLayoutPanel
{
    internal CheckBox EnabledChoice { get; }
    internal Button SaveButton { get; }
    internal Label Status { get; }
    internal StartupSettingsPanel(StartupRegistration? startup)
    {
        Dock = DockStyle.Fill; Padding = new(18); ColumnCount = 1; RowCount = 5;
        ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) RowStyles.Add(new(SizeType.AutoSize));
        RowStyles.Add(new(SizeType.Percent, 100));
        Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill,
            Text = "登录时仅启动此托盘程序，默认关闭。\n启用后请保留当前解压目录；移动或删除前先关闭自启动。\nWindows 可能在“启动应用”中另行禁用此启动项。", Margin = new(0, 0, 0, 16) }, 0, 0);
        EnabledChoice = new CheckBox { Text = "登录 Windows 时启动此版本", AutoSize = true, Enabled = startup is not null };
        Controls.Add(EnabledChoice, 0, 1);
        SaveButton = new Button { Text = "保存登录启动设置", AutoSize = true, Enabled = startup is not null, Margin = new(0, 12, 0, 12) };
        Controls.Add(SaveButton, 0, 2);
        Status = new Label { AutoSize = true, Dock = DockStyle.Fill,
            Text = "此模式不管理登录自启动。", ForeColor = SystemColors.GrayText };
        Controls.Add(Status, 0, 3);
        Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill,
            Text = "此页使用上方专用按钮立即保存。\n窗口底部“应用”只保存刷新、提醒、设备及托盘设置。", Margin = new(0, 18, 0, 0) }, 0, 4);
        void ShowState(StartupState state)
        { EnabledChoice.Checked = state.Enabled; EnabledChoice.Enabled = SaveButton.Enabled = state.CanChange; Status.Text = state.Notice; }
        if (startup is not null) ShowState(startup.Read());
        SaveButton.Click += (_, _) => { if (startup is not null) ShowState(startup.SetEnabled(EnabledChoice.Checked)); };
    }
}
