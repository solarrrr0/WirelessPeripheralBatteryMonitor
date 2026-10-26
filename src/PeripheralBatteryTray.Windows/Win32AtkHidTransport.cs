using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBatteryTray.Windows;

/// <summary>Exact-target metadata discovery and explicitly requested telemetry. Constructor performs no I/O.</summary>
public sealed class Win32AtkHidTransport : IAtkHidTransport
{
    public Task<AtkHidScan> ScanAsync(CancellationToken cancellation) => Task.Run(() => Scan(cancellation));
    public Task<IAtkHidConnection> OpenAsync(AtkHidEndpoint endpoint, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!AtkHidPolicy.Allows(endpoint.Descriptor) || !MatchesPath(endpoint.Path))
            throw new ArgumentException("ATK interface does not match the telemetry allowlist.");
        return Task.Run<IAtkHidConnection>(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            SafeFileHandle handle = Open(endpoint.Path, 0xC0000000, 0x40000000); // READ|WRITE, OVERLAPPED; no protocol traffic yet.
            try
            {
                var current = Inspect(handle, endpoint.Path);
                if (!AtkHidPolicy.Allows(current) || current != endpoint.Descriptor)
                    throw new IOException("ATK interface changed after enumeration.");
                cancellation.ThrowIfCancellationRequested();
                if (!HidD_FlushQueue(handle)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                cancellation.ThrowIfCancellationRequested();
                var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
                return new Connection(stream);
            }
            catch { handle.Dispose(); throw; }
        });
    }

    private static AtkHidScan Scan(CancellationToken cancellation)
    {
        HidD_GetHidGuid(out Guid hid);
        using var devices = SetupDiGetClassDevsW(ref hid, 0, 0, 0x12); // PRESENT|DEVICEINTERFACE
        if (devices.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var results = new List<AtkHidEndpoint>(); int errors = 0;
        for (uint i = 0; ; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var item = new DeviceInterface { Size = (uint)Marshal.SizeOf<DeviceInterface>() };
            if (!SetupDiEnumDeviceInterfaces(devices, 0, ref hid, i, ref item))
            { int error = Marshal.GetLastPInvokeError(); if (error == 259) break; throw new Win32Exception(error); }
            _ = SetupDiGetDeviceInterfaceDetailW(devices, ref item, 0, 0, out uint required, 0);
            if (required is < 8 or > 65536) throw new IOException("Invalid HID interface detail size.");
            nint detail = Marshal.AllocHGlobal((int)required);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetailW(devices, ref item, detail, required, out _, 0))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                string? path = Marshal.PtrToStringUni(detail + 4);
                if (path is null || !MatchesPath(path)) continue;
                try
                {
                    using var metadata = Open(path, 0, 0); // Metadata only. No read/write access or HID command.
                    var descriptor = Inspect(metadata, path);
                    if (AtkHidPolicy.Allows(descriptor)) results.Add(new(path, descriptor));
                }
                catch (Exception error) when (error is Win32Exception or IOException) { errors++; }
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        return new(results.AsReadOnly(), errors);
    }

    private static bool MatchesPath(string path)
    {
        if (!path.StartsWith(@"\\?\hid#", StringComparison.OrdinalIgnoreCase)) return false;
        string[] parts = path.Split('#'); if (parts.Length < 3) return false;
        var tokens = parts[1].Split('&').ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tokens.Contains("vid_373b") && tokens.Contains("pid_11fd") && tokens.Contains("mi_01");
    }

    private static SafeFileHandle Open(string path, uint access, uint flags)
    {
        var handle = CreateFileW(path, access, 3, 0, 3, flags, 0);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error);
    }

    private static AtkHidDescriptor Inspect(SafeFileHandle handle, string path)
    {
        var attributes = new Attributes { Size = (uint)Marshal.SizeOf<Attributes>() };
        if (!HidD_GetAttributes(handle, ref attributes)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (attributes.VendorId != 0x373B || attributes.ProductId != 0x11FD || !MatchesPath(path))
            throw new IOException("HID handle identity mismatch.");
        if (!HidD_GetPreparsedData(handle, out nint preparsed)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            if (HidP_GetCaps(preparsed, out Caps caps) != 0x00110000) throw new IOException("HID capabilities unavailable.");
            // Both BUTTON_CAPS and VALUE_CAPS are 72-byte records, with ReportID at byte 2.
            bool input = HasReport8(preparsed, 0, caps.InputButtons, caps.InputValues);
            bool output = HasReport8(preparsed, 1, caps.OutputButtons, caps.OutputValues);
            return new(attributes.VendorId, attributes.ProductId, 1, caps.UsagePage, caps.Usage,
                caps.InputLength, caps.OutputLength, caps.FeatureLength, input, output);
        }
        finally { if (!HidD_FreePreparsedData(preparsed)) throw new IOException("HID preparsed data cleanup failed."); }
    }

    private static bool HasReport8(nint preparsed, int reportType, ushort buttons, ushort values)
        => ReadReportCaps(preparsed, reportType, false, buttons) || ReadReportCaps(preparsed, reportType, true, values);

    // Local function captures no handles after Inspect returns.
    private static bool ReadReportCaps(nint preparsed, int reportType, bool valueCaps, ushort count)
    {
        if (count == 0) return false;
        if (count > 1024) throw new IOException("HID capability count exceeds limit.");
        nint buffer = Marshal.AllocHGlobal(count * 72);
        try
        {
            ushort actual = count;
            int status = valueCaps ? HidP_GetValueCaps(reportType, buffer, ref actual, preparsed)
                : HidP_GetButtonCaps(reportType, buffer, ref actual, preparsed);
            if (status != 0x00110000 || actual > count) throw new IOException("HID report capability query failed.");
            for (int i = 0; i < actual; i++) if (Marshal.ReadByte(buffer, i * 72 + 2) == AtkProtocol.ReportId) return true;
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private sealed class Connection(FileStream stream) : IAtkHidConnection
    {
        private int _querying, _poisoned, _disposed;
        public async Task<AtkReplyObservation> QueryAsync(AtkReadCommand command, CancellationToken cancellation)
        {
            byte[] request = AtkProtocol.BuildRequest(command); // Validate before touching the handle.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Volatile.Read(ref _poisoned) != 0 || Interlocked.CompareExchange(ref _querying, 1, 0) != 0)
                throw new InvalidOperationException("ATK connection unavailable or already querying.");
            try
            {
                await stream.WriteAsync(request, cancellation).ConfigureAwait(false);
                for (int i = 0; i < 16; i++)
                {
                    byte[] reply = new byte[AtkProtocol.WindowsReportBytes];
                    int count = await stream.ReadAsync(reply, cancellation).ConfigureAwait(false);
                    // Ignore only the separately identified asynchronous status report; never another telemetry reply.
                    if (count == 17 && reply[0] == 8 && reply[1] == 0x0A) continue;
                    var error = AtkProtocol.InspectReply(reply.AsSpan(0, count), command, out var observation);
                    if (error != AtkReplyError.None) throw new AtkInvalidReplyException(error);
                    return observation!;
                }
                throw new IOException("Too many unrelated HID reports.");
            }
            catch { Interlocked.Exchange(ref _poisoned, 1); throw; }
            finally { Interlocked.Exchange(ref _querying, 0); }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DeviceInterface { public uint Size; public Guid ClassGuid; public uint Flags; public nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct Attributes { public uint Size; public ushort VendorId, ProductId, Version; }
    [StructLayout(LayoutKind.Sequential)] private struct Caps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkCollections, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
    private sealed class DeviceInfoHandle : SafeHandleZeroOrMinusOneIsInvalid
    { public DeviceInfoHandle() : base(true) { } protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle); }
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern DeviceInfoHandle SetupDiGetClassDevsW(ref Guid guid, nint enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(DeviceInfoHandle set, nint info, ref Guid guid, uint index, ref DeviceInterface data);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetailW(DeviceInfoHandle set, ref DeviceInterface data, nint detail, uint size, out uint required, nint info);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(nint data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(nint data, out Caps caps);
    [DllImport("hid.dll")] private static extern int HidP_GetButtonCaps(int type, nint caps, ref ushort count, nint data);
    [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int type, nint caps, ref ushort count, nint data);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FlushQueue(SafeFileHandle handle);
}
