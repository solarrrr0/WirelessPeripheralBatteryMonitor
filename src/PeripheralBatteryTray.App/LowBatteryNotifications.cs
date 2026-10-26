using System.Runtime.InteropServices;
using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.App;

internal enum NotificationDelivery { Requested, Suppressed, Failed }
internal interface ILowBatteryNotifier
{ NotificationDelivery Send(IReadOnlyList<LowBatteryAlert> alerts); }

internal static class LowBatteryProjection
{
    internal static IEnumerable<LowBatterySample> Samples(BatterySnapshot snapshot, TimeProvider time,
        TimeSpan period, AtkTrayReading? atk, DisplaySettings? display = null)
    {
        if (snapshot.Mode != BatteryMode.Real) yield break;
        display ??= DisplaySettings.Default;
        foreach (var device in snapshot.Devices)
        {
            var preference = display.For(device.Binding.PreferenceKey);
            if (preference.Hidden) continue;
            foreach (var component in device.Components.Where(c => !c.RemovedFromDirectory))
            {
                var stamp = component.Source.LastSuccess?.QueryStamp;
                string context = stamp is QueryStamp q ? $"{q.BindingEpoch}:{q.TransportEpoch}:{q.DatabaseEpoch}:{q.Transport}" : "unknown";
                int? percent = BatteryRules.IsCurrentPercent(component, device.Binding, snapshot.Mode, time, period)
                    ? component.Source.LastSuccess!.Value.Percent : null;
                yield return new("gatt:" + device.Binding.DeviceId + "\0" + component.ComponentKey,
                    device.Binding.DeviceId, preference.Alias.Length == 0 ? device.DisplayName : preference.Alias, percent, context, component.Source.LastSuccess?.CapturedTimestamp ?? 0);
            }
        }
        var atkPreference = display.For(DisplaySettings.AtkKey);
        if (atk is not null && !atkPreference.Hidden) yield return new("atk-duckbill", "atk-duckbill",
            atkPreference.Alias.Length == 0 ? "ATK Duckbill Ultra" : atkPreference.Alias, atk.Percent, "atk-session", atk.Observation);
    }
}

internal sealed class TrayLowBatteryNotifier(NotifyIcon icon) : ILowBatteryNotifier
{
    internal static bool Allows(int hresult, int state) => hresult == 0 && state == 5;
    internal static string Message(IReadOnlyList<LowBatteryAlert> alerts)
    {
        static string Clean(string name) => new(name.Where(c => !char.IsControl(c)).Take(48).ToArray());
        string text = string.Join("；", alerts.Take(3).Select(a => $"{Clean(a.DisplayName)} {a.Percent}%"));
        if (alerts.Count > 3) text += $"；另有{alerts.Count - 3}个设备";
        return text.Length <= 240 ? text : text[..240];
    }
    public NotificationDelivery Send(IReadOnlyList<LowBatteryAlert> alerts)
    {
        if (alerts.Count == 0 || !icon.Visible) return NotificationDelivery.Suppressed;
        try
        {
            if (!Allows(SHQueryUserNotificationState(out int state), state)) return NotificationDelivery.Suppressed;
            icon.ShowBalloonTip(10000, "外设电量偏低", Message(alerts), ToolTipIcon.Warning);
            // A shell request is not proof that Windows displayed a notification.
            return NotificationDelivery.Requested;
        }
        catch { return NotificationDelivery.Failed; }
    }
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHQueryUserNotificationState(out int state);
}
