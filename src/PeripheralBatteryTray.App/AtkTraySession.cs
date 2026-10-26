using PeripheralBatteryTray.Windows;

namespace PeripheralBatteryTray.App;

internal sealed record AtkTrayReading(int? Percent, int? LastPercent, DateTimeOffset? ObservedAtUtc,
    string Status, bool Busy, bool CanRead, bool Automatic = false, string AutomaticStatus = "", long Observation = 0);

internal sealed class AtkTraySession(IAtkHidTransport transport, TimeProvider? time = null)
    : AtkRefreshController(transport, time);
