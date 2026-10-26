using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.Windows;

public enum GattCacheMode { Cached, Uncached }
public enum GattStatus { Success, Unreachable, ProtocolError, AccessDenied }
public sealed record GattResult<T>(GattStatus Status, T Value, byte? ProtocolError = null);
public interface IGattCall
{
    void Cancel();
}
public interface IGattCall<T> : IGattCall { Task<T> Terminal { get; } }
public interface IGattClient { IGattCall<IGattDevice?> Open(AepId id, Func<bool> mayStart); }
public interface IGattDevice : IDisposable
{
    ConnectionState Connection { get; }
    event Action? ServicesChanged;
    event Action? ConnectionChanged;
    IGattCall<GattResult<IReadOnlyList<IGattService>>> Services(Guid uuid, GattCacheMode cache);
}
public interface IGattService : IDisposable
{
    object Identity { get; }
    ushort Handle { get; }
    IGattCall<GattResult<IReadOnlyList<IGattCharacteristic>>> Characteristics(Guid uuid, GattCacheMode cache);
}
public interface IGattCharacteristic
{
    ushort Handle { get; }
    bool CanRead { get; }
    IGattCall<GattResult<byte[]?>> Read(GattCacheMode cache);
}
public static class BatteryGattUuids
{
    public static readonly Guid Service = new("0000180f-0000-1000-8000-00805f9b34fb");
    public static readonly Guid Level = new("00002a19-0000-1000-8000-00805f9b34fb");
}

public sealed class GattDirectoryCache
{
    private readonly object _gate = new();
    private sealed class Entry(QueryStamp stamp)
    {
        public QueryStamp Stamp = stamp;
        public string? Services;
        public Dictionary<string, string> Characteristics = [];
        public bool Uncached;
    }
    private readonly Dictionary<string, Entry> _entries = [];
    private Entry Get(QueryStamp stamp)
    {
        if (!_entries.TryGetValue(stamp.DeviceId, out var entry) || entry.Stamp.BindingEpoch != stamp.BindingEpoch
            || entry.Stamp.TransportEpoch != stamp.TransportEpoch || entry.Stamp.DatabaseEpoch != stamp.DatabaseEpoch)
            _entries[stamp.DeviceId] = entry = new(stamp);
        return entry;
    }
    public bool ForceUncached(QueryStamp stamp) { lock (_gate) return Get(stamp).Uncached; }
    public void MarkProtocolFailure(QueryStamp stamp) { lock (_gate) Get(stamp).Uncached = true; }
    public bool ServicesChanged(QueryStamp stamp, string signature)
    {
        lock (_gate) { var entry = Get(stamp); bool changed = entry.Services is not null && entry.Services != signature;
            if (!changed) { entry.Services = signature; entry.Uncached = false; } return changed; }
    }
    public bool CharacteristicsChanged(QueryStamp stamp, string service, string signature)
    {
        lock (_gate) { var entry = Get(stamp); bool changed = entry.Characteristics.TryGetValue(service, out string? old) && old != signature;
            if (!changed) entry.Characteristics[service] = signature; return changed; }
    }
}
