using System.Buffers.Binary;
using System.Text;
using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.Windows;

public readonly record struct PropertyKey(Guid Format, uint Id);
public enum PropertyStatus { Success, Missing, NodeGone, AccessDenied, TooLarge, TypeMismatch, Error }
public sealed record RawProperty(PropertyStatus Status, uint Type, byte[] Data, uint NativeCode = 0);
public static class BatteryPropertyKeys
{
    public static readonly PropertyKey Standard = new(new("49cd1f76-5626-4b17-a4e8-18b4aa1a2213"), 10);
    public static readonly PropertyKey Compatibility = new(new("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), 2);
    public static readonly PropertyKey Aep = new(new("3b2ce006-5e61-4fde-bab8-9b8aac9b26df"), 8);
    public static readonly PropertyKey Container = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    public static readonly PropertyKey Local = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 4);
    public static readonly PropertyKey Parent = new(new("4340a6c5-93fa-4706-972c-7b648008a5a7"), 8);
    public static readonly PropertyKey Service = new(new("2bd67d8b-8beb-48d5-87e0-6cda3428040a"), 2);
}
public interface IPnpNative
{
    IReadOnlyList<(DevnodeId Id, uint DevInst)> Enumerate(string enumerator, CancellationToken cancellation);
    uint Locate(DevnodeId id, out uint devInst);
    uint GetProperty(uint devInst, PropertyKey key, out uint type, byte[]? buffer, ref uint size);
}
public sealed class PnpPropertyReader(IPnpNative native)
{
    public const uint TypeByte = 3, TypeUInt32 = 7, TypeGuid = 13, TypeBoolean = 17, TypeString = 18;
    public const uint Success = 0, BufferSmall = 0x1a, NoSuchDevnode = 0x0d, NoSuchValue = 0x25, AccessDenied = 0x33;
    public RawProperty Read(uint devInst, PropertyKey key)
    {
        uint size = 0; uint status = native.GetProperty(devInst, key, out uint type, null, ref size);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (status != Success && status != BufferSmall) return new(Classify(status), type, [], status);
            if (size > 65536) return new(PropertyStatus.TooLarge, type, [], status);
            if (size == 0) return new(PropertyStatus.Missing, type, [], status);
            byte[] buffer = new byte[size]; uint actual = size;
            status = native.GetProperty(devInst, key, out type, buffer, ref actual);
            if (status == BufferSmall) { size = actual; continue; }
            if (status != Success) return new(Classify(status), type, [], status);
            if (actual > buffer.Length) return new(PropertyStatus.Error, type, [], status);
            return new(PropertyStatus.Success, type, buffer[..(int)actual]);
        }
        return new(PropertyStatus.Error, type, [], status);
    }
    public RawProperty Read(DevnodeId id, PropertyKey key)
    {
        uint status = native.Locate(id, out uint instance);
        return status == Success ? Read(instance, key) : new(Classify(status), 0, [], status);
    }
    public static PropertyStatus Classify(uint configRet) => configRet switch
    {
        Success => PropertyStatus.Success, NoSuchDevnode or 5 => PropertyStatus.NodeGone,
        NoSuchValue => PropertyStatus.Missing, AccessDenied => PropertyStatus.AccessDenied, _ => PropertyStatus.Error
    };
    public static BatteryValue? DecodeBattery(RawProperty property, bool compatibility)
    {
        if (property.Status != PropertyStatus.Success) return null;
        uint? number = property.Type == TypeByte && property.Data.Length == 1 ? property.Data[0]
            : compatibility && property.Type == TypeUInt32 && property.Data.Length == 4
                ? BinaryPrimitives.ReadUInt32LittleEndian(property.Data) : null;
        return number is <= 100 ? BatteryValue.FromPercent((int)number.Value) : null;
    }
    public static string? DecodeString(RawProperty property)
    {
        if (property.Status != PropertyStatus.Success || property.Type != TypeString || property.Data.Length < 2
            || property.Data.Length % 2 != 0 || property.Data[^1] != 0 || property.Data[^2] != 0) return null;
        try { string result = new UnicodeEncoding(false, false, true).GetString(property.Data[..^2]);
            return result.Contains('\0') ? null : result; } catch (DecoderFallbackException) { return null; }
    }
    public static Guid? DecodeGuid(RawProperty p) => p.Status == PropertyStatus.Success && p.Type == TypeGuid && p.Data.Length == 16 ? new Guid(p.Data) : null;
    public static bool? DecodeBoolean(RawProperty p) => p.Status == PropertyStatus.Success && p.Type == TypeBoolean && p.Data.Length == 1
        ? p.Data[0] == 0 ? false : p.Data[0] == 255 ? true : null : null;
    public IReadOnlyList<PnpNode> BuildIndex(CancellationToken cancellation, Action<PnpNode>? publish = null)
    {
        var nodes = new List<PnpNode>();
        foreach (string enumerator in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE", "BTHHFENUM" })
            foreach (var (id, devInst) in native.Enumerate(enumerator, cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                string? aep = DecodeString(Read(devInst, BatteryPropertyKeys.Aep)); cancellation.ThrowIfCancellationRequested();
                Guid? container = DecodeGuid(Read(devInst, BatteryPropertyKeys.Container)); cancellation.ThrowIfCancellationRequested();
                string? parent = DecodeString(Read(devInst, BatteryPropertyKeys.Parent)); cancellation.ThrowIfCancellationRequested();
                bool? local = DecodeBoolean(Read(devInst, BatteryPropertyKeys.Local)); cancellation.ThrowIfCancellationRequested();
                Guid? service = DecodeGuid(Read(devInst, BatteryPropertyKeys.Service));
                var node = new PnpNode(id, aep is null ? null : new(aep), container is null ? null : new(container.Value),
                    parent is null ? null : new(parent), local, service);
                nodes.Add(node); publish?.Invoke(node);
            }
        return nodes.AsReadOnly();
    }
}
