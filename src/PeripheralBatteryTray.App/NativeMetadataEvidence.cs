using PeripheralBatteryTray.Windows;

namespace PeripheralBatteryTray.App;

internal sealed record NativePropertyObservation(DevnodeId? Node, PropertyKey Key, uint Status, uint Type, uint Length, int? Percent);
// Explicit host-check evidence only. This object is never created in normal/Mock mode or publicly exported.
internal sealed class NativeMetadataEvidence(IPnpNative inner) : IPnpNative
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, DevnodeId> _ids = [];
    private readonly List<NativePropertyObservation> _observations = [];
    public IReadOnlyList<NativePropertyObservation> Snapshot() { lock (_gate) return _observations.ToArray(); }
    public IReadOnlyList<(DevnodeId Id, uint DevInst)> Enumerate(string enumerator, CancellationToken cancellation)
    { var result = inner.Enumerate(enumerator, cancellation); lock (_gate) foreach (var node in result) _ids[node.DevInst] = node.Id; return result; }
    public uint Locate(DevnodeId id, out uint devInst)
    { uint status = inner.Locate(id, out devInst); if (status == 0) lock (_gate) _ids[devInst] = id; return status; }
    public uint GetProperty(uint devInst, PropertyKey key, out uint type, byte[]? buffer, ref uint size)
    {
        uint status = inner.GetProperty(devInst, key, out type, buffer, ref size);
        if (buffer is not null && key == BatteryPropertyKeys.Compatibility)
        {
            int? percent = status == 0 && size <= buffer.Length ? PnpPropertyReader.DecodeBattery(new(PropertyStatus.Success, type, buffer[..(int)size]), true)?.Percent : null;
            lock (_gate) if (_observations.Count < 512) _observations.Add(new(_ids.GetValueOrDefault(devInst), key, status, type, size, percent));
        }
        return status;
    }
}
