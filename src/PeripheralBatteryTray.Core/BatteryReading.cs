namespace PeripheralBatteryTray.Core;

public enum ConnectionState { Unknown, Connected, Disconnected }
public enum ReadingSource { WindowsProperty, GattUncached, Mock }
public enum ReadOutcome { Unknown, Success, Offline, Unsupported, TimedOut, AccessDenied, Error, Cancelled }
public enum ReadingFreshness { Fresh, Stale, SystemCacheUnknownAge }
public enum BatteryMode { Mock, Real }
public enum TransportKind { Classic, Ble, Mock }
public enum DirectoryScope { GattServices, ServiceCharacteristics }

/// <summary>A valid percentage or band. Absence is represented by a null BatteryValue.</summary>
public sealed record BatteryValue
{
    public int? Percent { get; }
    public BatteryBand? Band { get; }
    private BatteryValue(int? percent, BatteryBand? band) => (Percent, Band) = (percent, band);
    public static bool TryCreate(int? percent, BatteryBand? band, out BatteryValue? value)
    {
        value = null;
        if (percent.HasValue == band.HasValue || percent is < 0 or > 100
            || (band.HasValue && !Enum.IsDefined(band.Value))) return false;
        value = new(percent, band);
        return true;
    }
    public static BatteryValue FromPercent(int percent) => TryCreate(percent, null, out var value)
        ? value! : throw new ArgumentOutOfRangeException(nameof(percent));
    public static BatteryValue FromBand(BatteryBand band) => TryCreate(null, band, out var value)
        ? value! : throw new ArgumentOutOfRangeException(nameof(band));
    public string Text => Percent is int percent ? $"{percent}%" : Band switch
    {
        BatteryBand.Low => "低", BatteryBand.Medium => "中", BatteryBand.High => "高", _ => "未知"
    };
}

public readonly record struct QueryStamp(string DeviceId, long BindingEpoch, long TransportEpoch,
    long DatabaseEpoch, ReadingSource Source, long RequestId, TransportKind Transport = TransportKind.Ble);
public sealed record TransportState(ConnectionState Connection, long Epoch);

/// <summary>Identity and connection evidence already resolved by discovery; contains no native objects.</summary>
public sealed record DeviceBinding
{
    public required string DeviceId { get; init; }
    public required string BindingKey { get; init; }
    public string? PreferenceKey { get; init; }
    public long BindingEpoch { get; init; }
    public TransportState? Classic { get; init; }
    public TransportState? Ble { get; init; }
    public TransportState? Mock { get; init; }
    public long GattDatabaseEpoch { get; init; }
    public bool SleepInvalidated { get; init; }
    public TransportState? GetTransport(TransportKind transport) => transport switch
    {
        TransportKind.Classic => Classic, TransportKind.Ble => Ble, TransportKind.Mock => Mock, _ => null
    };
    public QueryStamp Stamp(ReadingSource source, long requestId, TransportKind transport = TransportKind.Ble)
        => new(DeviceId, BindingEpoch, GetTransport(transport)?.Epoch ?? 0,
            source == ReadingSource.WindowsProperty ? 0 : GattDatabaseEpoch, source, requestId, transport);
}

public sealed record BatteryReading(BatteryValue Value, ReadingSource Source, QueryStamp QueryStamp,
    DateTimeOffset ObservedAtUtc, long CapturedTimestamp, DateTimeOffset? SourceTimestampUtc = null);
public sealed record SourceState
{
    public required ReadingSource Source { get; init; }
    public required string CandidateKey { get; init; }
    public string? ComponentKey { get; init; }
    public ReadOutcome LatestOutcome { get; init; } = ReadOutcome.Unknown;
    public QueryStamp? LatestRequestStamp { get; init; }
    public long LastUpdateSequence { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public BatteryReading? LastSuccess { get; init; }
    public bool IsRefreshing { get; init; }
    public DateTimeOffset? LastSuccessfulReadUtc => Source == ReadingSource.GattUncached
        && LastSuccess?.Source == ReadingSource.GattUncached
        ? LastSuccess.ObservedAtUtc : null;
}
public sealed record ComponentState(string ComponentKey, string ServiceKey, int DisplayOrder,
    SourceState Source, bool InCurrentGattDirectory = false, bool RemovedFromDirectory = false);

/// <summary>Immutable domain state; DeviceBatteryState remains the Phase 1 UI projection until batch C.</summary>
public sealed record BatteryDeviceState
{
    public DeviceBinding Binding { get; init; }
    public string DisplayName { get; init; }
    public string ConnectionType { get; init; }
    public IReadOnlyList<SourceState> SystemCandidates { get; }
    public IReadOnlyList<ComponentState> Components { get; }
    public BatteryDeviceState(DeviceBinding binding, string displayName, string connectionType,
        IEnumerable<SourceState>? systemCandidates = null, IEnumerable<ComponentState>? components = null)
    {
        Binding = binding;
        DisplayName = displayName;
        ConnectionType = connectionType;
        SystemCandidates = Array.AsReadOnly((systemCandidates ?? []).ToArray());
        Components = Array.AsReadOnly((components ?? []).ToArray());
        if (Components.Select(x => x.ComponentKey).Distinct(StringComparer.Ordinal).Count() != Components.Count
            || SystemCandidates.Select(x => x.CandidateKey).Distinct(StringComparer.Ordinal).Count() != SystemCandidates.Count)
            throw new ArgumentException("Duplicate component or candidate key.");
    }
    public BatteryDeviceState WithComponents(IEnumerable<ComponentState> components) => new(
        Binding, DisplayName, ConnectionType, SystemCandidates, components);
    public BatteryDeviceState WithSystemCandidates(IEnumerable<SourceState> candidates) => new(
        Binding, DisplayName, ConnectionType, candidates, Components);
}

public readonly record struct BatteryTrayVisual(int? MinimumPercent, bool HasOffline, bool HasUnknown, BatteryMode Mode)
{
    public string Tooltip => $"外设电量{(Mode == BatteryMode.Mock ? "（模拟）" : "")} · "
        + (MinimumPercent is int percent ? $"最低可读电池 {percent}%" : "无有效百分比");
}
public sealed class BatterySnapshot
{
    public long Version { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public BatteryMode Mode { get; }
    public IReadOnlyList<BatteryDeviceState> Devices { get; }
    public BatteryTrayVisual TrayVisual { get; }
    public BatterySnapshot(long version, DateTimeOffset createdAtUtc, BatteryMode mode,
        IEnumerable<BatteryDeviceState> devices, TimeProvider time, TimeSpan refreshPeriod)
    {
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        Version = version;
        CreatedAtUtc = createdAtUtc;
        Mode = mode;
        Devices = Array.AsReadOnly(BatteryRules.GroupResolvedDevices(devices).ToArray());
        TrayVisual = BatteryRules.GetTrayVisual(Devices, mode, time, refreshPeriod);
    }
}
