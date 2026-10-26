using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.Windows;

/// <summary>Counts adapter invocations and real terminal returns, without recording their raw arguments.</summary>
public sealed class ObservedPnpNative(IPnpNative native, DiagnosticTelemetry telemetry) : IPnpNative
{
    public IReadOnlyList<(DevnodeId Id, uint DevInst)> Enumerate(string enumerator, CancellationToken cancellation)
    {
        telemetry.AdapterStarted(ReadingSource.WindowsProperty, DiagnosticStep.MetadataEnumerate);
        try { return native.Enumerate(enumerator, cancellation); }
        finally { telemetry.AdapterTerminal(ReadingSource.WindowsProperty); }
    }
    public uint Locate(DevnodeId id, out uint devInst)
    {
        telemetry.AdapterStarted(ReadingSource.WindowsProperty, DiagnosticStep.MetadataLocate);
        try { return native.Locate(id, out devInst); }
        finally { telemetry.AdapterTerminal(ReadingSource.WindowsProperty); }
    }
    public uint GetProperty(uint devInst, PropertyKey key, out uint type, byte[]? buffer, ref uint size)
    {
        telemetry.AdapterStarted(ReadingSource.WindowsProperty, DiagnosticStep.MetadataProperty);
        try { return native.GetProperty(devInst, key, out type, buffer, ref size); }
        finally { telemetry.AdapterTerminal(ReadingSource.WindowsProperty); }
    }
}
