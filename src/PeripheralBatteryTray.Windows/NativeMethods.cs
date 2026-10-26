using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBatteryTray.Windows;

/// <summary>Metadata calls only. No CreateFile, HID or GATT entry points.</summary>
public sealed class PnpNative : IPnpNative
{
    public IReadOnlyList<(DevnodeId Id, uint DevInst)> Enumerate(string enumerator, CancellationToken cancellation)
    {
        if (enumerator is not ("BTHENUM" or "BTHLE" or "BTHLEDEVICE" or "BTHHFENUM")) throw new ArgumentException("Enumerator not allowed.");
        using var info = SetupDiGetClassDevsW(0, enumerator, 0, 4); // DIGCF_ALLCLASSES, deliberately includes cached devnodes.
        if (info.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var nodes = new List<(DevnodeId, uint)>();
        for (uint index = 0; ; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var data = new SpDevInfo { Size = (uint)Marshal.SizeOf<SpDevInfo>() };
            if (!SetupDiEnumDeviceInfo(info, index, ref data))
            {
                int error = Marshal.GetLastPInvokeError(); if (error == 259) break;
                throw new Win32Exception(error);
            }
            _ = SetupDiGetDeviceInstanceIdW(info, ref data, null, 0, out uint required);
            int lengthError = Marshal.GetLastPInvokeError();
            if (required is 0 or > 32768 || lengthError != 122) continue;
            var buffer = new StringBuilder((int)required);
            if (!SetupDiGetDeviceInstanceIdW(info, ref data, buffer, required, out _)) continue;
            nodes.Add((new(buffer.ToString()), data.DevInst));
        }
        return nodes.AsReadOnly();
    }
    public uint Locate(DevnodeId id, out uint devInst) => CM_Locate_DevNodeW(out devInst, id.Value, 1); // PHANTOM permits known offline cache nodes.
    public uint GetProperty(uint devInst, PropertyKey key, out uint type, byte[]? buffer, ref uint size)
    { var nativeKey = new DevPropKey { Format = key.Format, Id = key.Id };
        return CM_Get_DevNode_PropertyW(devInst, ref nativeKey, out type, buffer, ref size, 0); }
    [StructLayout(LayoutKind.Sequential)] private struct SpDevInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DevPropKey { public Guid Format; public uint Id; }
    private sealed class DevInfoHandle : SafeHandleZeroOrMinusOneIsInvalid
    { public DevInfoHandle() : base(true) { } protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle); }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern DevInfoHandle SetupDiGetClassDevsW(nint classGuid, string enumerator, nint hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(DevInfoHandle info, uint index, ref SpDevInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceIdW(DevInfoHandle info, ref SpDevInfo data, StringBuilder? buffer, uint size, out uint required);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint info);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type, byte[]? buffer, ref uint size, uint flags);
}
