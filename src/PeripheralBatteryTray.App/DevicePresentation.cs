using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.App;

internal sealed record PresentedDevice(string Id, string? PreferenceKey, string OriginalName, string Name,
    bool Hidden, BatteryTrayVisual Visual, BatteryDeviceState? Model = null, AtkTrayReading? Atk = null, int? DisplayPercent = null)
{
    internal int? Percent => Visual.MinimumPercent;
    internal int? IconPercent => Percent ?? DisplayPercent;
    internal bool IconUsesSystemReport => Percent is null && DisplayPercent is not null;
    internal TrayVisual IconVisual(TrayIconStyle style) => style == TrayIconStyle.Number
        ? new(IconPercent, Visual.HasOffline, Visual.HasUnknown && IconPercent is null)
        : new(Percent, Visual.HasOffline, Visual.HasUnknown);
}

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
                BatteryRules.GetTrayVisual([d], snapshot.Mode, time, period), d,
                DisplayPercent: BatteryRules.Present(d, snapshot.Mode, time, period).DisplayPercent);
        }).ToList();
        if (atk is not null)
        {
            var preference = settings.For(DisplaySettings.AtkKey);
            entries.Add(new("atk-duckbill", DisplaySettings.AtkKey, "ATK Duckbill Ultra",
                preference.Alias.Length == 0 ? "ATK Duckbill Ultra" : preference.Alias, preference.Hidden,
                new(atk.Percent, atk.Offline, atk.Percent is null && !atk.Offline, snapshot.Mode), Atk: atk));
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

    // Display-only aggregation never feeds the coordinator, battery sorting or notifications.
    internal static TrayVisual NumberAggregate(IEnumerable<PresentedDevice> devices)
    {
        var visible = devices.Where(d => !d.Hidden).ToArray();
        return new(visible.Select(d => d.IconPercent).Min(), visible.Length > 0 && visible.All(d => d.Visual.HasOffline),
            visible.Any(d => d.IconPercent is null));
    }
}
