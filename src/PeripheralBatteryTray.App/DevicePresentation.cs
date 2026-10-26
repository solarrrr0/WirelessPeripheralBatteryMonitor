using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.App;

internal sealed record PresentedDevice(string Id, string? PreferenceKey, string OriginalName, string Name,
    bool Hidden, BatteryTrayVisual Visual, BatteryDeviceState? Model = null, AtkTrayReading? Atk = null)
{ internal int? Percent => Visual.MinimumPercent; }

internal static class DevicePresentation
{
    internal static IReadOnlyList<PresentedDevice> Project(BatterySnapshot snapshot, TimeSpan period, AtkTrayReading? atk,
        DisplaySettings settings, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        var entries = snapshot.Devices.Select(d =>
        {
            var preference = settings.For(d.Binding.PreferenceKey);
            return new PresentedDevice("bluetooth:" + d.Binding.DeviceId, d.Binding.PreferenceKey, d.DisplayName,
                preference.Alias.Length == 0 ? d.DisplayName : preference.Alias, preference.Hidden,
                BatteryRules.GetTrayVisual([d], snapshot.Mode, time, period), d);
        }).ToList();
        if (atk is not null)
        {
            var preference = settings.For(DisplaySettings.AtkKey);
            entries.Add(new("atk-duckbill", DisplaySettings.AtkKey, "ATK Duckbill Ultra",
                preference.Alias.Length == 0 ? "ATK Duckbill Ultra" : preference.Alias, preference.Hidden,
                new(atk.Percent, false, atk.Percent is null, snapshot.Mode), Atk: atk));
        }
        IEnumerable<PresentedDevice> ordered = settings.Sort switch
        {
            DeviceSort.Name => entries.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase),
            DeviceSort.Battery => entries.OrderBy(d => d.Percent is null).ThenBy(d => d.Percent),
            _ => entries
        };
        return ordered.ToArray();
    }
    internal static BatteryTrayVisual Aggregate(IEnumerable<PresentedDevice> devices, BatteryMode mode)
    {
        var visible = devices.Where(d => !d.Hidden).ToArray();
        return new(visible.Select(d => d.Percent).Min(), visible.Any(d => d.Visual.HasOffline),
            visible.Any(d => d.Visual.HasUnknown), mode);
    }
}
