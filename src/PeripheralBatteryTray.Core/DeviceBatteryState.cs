namespace PeripheralBatteryTray.Core;

public enum BatteryReadStatus { Success, Unknown, Offline, Unsupported }
public enum BatteryBand { Low, Medium, High }

public sealed record DeviceBatteryState
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required string ConnectionType { get; init; }
    public required bool Connected { get; init; }
    public required BatteryReadStatus ReadStatus { get; init; }
    public int? BatteryPercent { get; init; }
    public BatteryBand? BatteryBand { get; init; }
    public bool? IsCharging { get; init; }
    public required string Source { get; init; }
    public DateTimeOffset? LastSuccessfulReadUtc { get; init; }
    public DateTimeOffset? LastSeenUtc { get; init; }

    public bool HasCurrentPercent => Connected && ReadStatus == BatteryReadStatus.Success
        && BatteryPercent is >= 0 and <= 100;

    public string BatteryText => !Connected || ReadStatus == BatteryReadStatus.Offline ? "离线"
        : ReadStatus == BatteryReadStatus.Unsupported ? "不支持"
        : ReadStatus != BatteryReadStatus.Success ? "未知"
        : HasCurrentPercent ? $"{BatteryPercent}%"
        : BatteryBand switch { Core.BatteryBand.Low => "低", Core.BatteryBand.Medium => "中",
            Core.BatteryBand.High => "高", _ => "未知" };

    public string ChargingText => IsCharging switch { true => "充电中", false => "未充电", _ => "充电状态未知" };
}

public readonly record struct TrayVisual(int? MinimumPercent, bool HasOffline, bool HasUnknown)
{
    public static TrayVisual From(IEnumerable<DeviceBatteryState> devices)
    {
        DeviceBatteryState[] states = devices.ToArray();
        return new(states.Where(x => x.HasCurrentPercent).Select(x => x.BatteryPercent).Min(),
            states.Any(x => !x.Connected || x.ReadStatus == BatteryReadStatus.Offline),
            states.Any(x => x.Connected && !x.HasCurrentPercent));
    }

    public string Tooltip => MinimumPercent is int percent
        ? $"外设电量（模拟）· 最低 {percent}%" : "外设电量（模拟）· 无有效百分比";
}
