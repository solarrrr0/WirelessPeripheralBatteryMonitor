using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.App;

internal interface IDeviceTrayIcon : IDisposable
{
    void Update(PresentedDevice device, int size);
    void Recover();
}

/// <summary>Event-driven ownership of a bounded number of optional Shell icons.</summary>
internal sealed class DeviceTrayIcons(Func<IDeviceTrayIcon> factory) : IDisposable
{
    internal const int MaximumIcons = 16;
    private readonly Dictionary<string, IDeviceTrayIcon> _icons = new(StringComparer.Ordinal);
    private bool _disposed;
    internal int Count => _icons.Count;
    internal int Omitted { get; private set; }
    internal void Update(IEnumerable<PresentedDevice> devices, TrayIconMode mode, int size)
    {
        if (_disposed) return;
        if (mode == TrayIconMode.Aggregate && _icons.Count == 0) { Omitted = 0; return; }
        var visible = mode == TrayIconMode.PerDevice ? devices.Where(d => !d.Hidden).ToArray() : [];
        var selected = visible.Take(MaximumIcons).ToArray();
        Omitted = Math.Max(0, visible.Length - selected.Length);
        var retained = selected.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string key in _icons.Keys.Where(k => !retained.Contains(k)).ToArray())
        { _icons[key].Dispose(); _icons.Remove(key); }
        foreach (var device in selected)
        {
            if (!_icons.TryGetValue(device.Id, out var icon)) _icons.Add(device.Id, icon = factory());
            icon.Update(device, size);
        }
    }
    internal void Recover() { if (!_disposed) foreach (var icon in _icons.Values) icon.Recover(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear(); Omitted = 0;
    }
}

internal sealed class ShellDeviceTrayIcon : IDeviceTrayIcon
{
    private readonly NotifyIcon _icon;
    private Icon? _owned;
    private BatteryTrayVisual? _last;
    private string? _tooltip;
    private int _size;
    private bool _disposed;
    internal int RenderCount { get; private set; }
    internal ShellDeviceTrayIcon(ContextMenuStrip menu, Action show)
    {
        _icon = new NotifyIcon { ContextMenuStrip = menu };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) show(); };
    }
    public void Update(PresentedDevice device, int size)
    {
        if (_disposed) return;
        string value = device.Percent is int p ? $"{p}%" : device.Visual.HasOffline ? "离线" : "无有效百分比";
        string tooltip = Tooltip(device.Name, value, device.Visual.Mode);
        if (_tooltip != tooltip) { _icon.Text = tooltip; _tooltip = tooltip; }
        if (_last != device.Visual || _size != size)
        {
            Icon next = TrayIconRenderer.Create(new(device.Percent, device.Visual.HasOffline, device.Visual.HasUnknown), size);
            _icon.Icon = next; Icon? old = _owned; _owned = next; old?.Dispose();
            _last = device.Visual; _size = size; RenderCount++;
        }
        _icon.Visible = true;
    }
    internal static string Tooltip(string name, string value, BatteryMode mode)
    {
        static string Trim(string text, int limit)
        {
            if (text.Length <= limit) return text;
            int count = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
            return text[..count];
        }
        return Trim((mode == BatteryMode.Mock ? "模拟 · " : "")
            + Trim(new string(name.Where(c => !char.IsControl(c)).ToArray()), 40) + " · " + value, 63);
    }
    public void Recover() { if (_disposed) return; _icon.Visible = false; _icon.Visible = true; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _icon.Visible = false; _icon.Dispose(); _owned?.Dispose(); _owned = null;
    }
}
