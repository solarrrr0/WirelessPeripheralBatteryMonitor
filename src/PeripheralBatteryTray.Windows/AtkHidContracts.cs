using System.Text.Json.Serialization;

namespace PeripheralBatteryTray.Windows;

public sealed record AtkHidDescriptor(ushort VendorId, ushort ProductId, int InterfaceNumber,
    ushort UsagePage, ushort Usage, ushort InputBytes, ushort OutputBytes, ushort FeatureBytes,
    bool InputReport8, bool OutputReport8);

public sealed record AtkHidEndpoint([property: JsonIgnore] string Path, AtkHidDescriptor Descriptor);
public sealed record AtkHidScan(IReadOnlyList<AtkHidEndpoint> Endpoints, int InspectionErrors);

public static class AtkHidPolicy
{
    public static bool Allows(AtkHidDescriptor descriptor) => descriptor is
    {
        VendorId: 0x373B, ProductId: 0x11FD, InterfaceNumber: 1, UsagePage: 0xFF02, Usage: 2,
        InputBytes: AtkProtocol.WindowsReportBytes, OutputBytes: AtkProtocol.WindowsReportBytes,
        InputReport8: true, OutputReport8: true
    };
}

/// <summary>Construction is inert; only explicit Scan/Open/Query methods perform native work.</summary>
public interface IAtkHidTransport
{
    Task<AtkHidScan> ScanAsync(CancellationToken cancellation);
    Task<IAtkHidConnection> OpenAsync(AtkHidEndpoint endpoint, CancellationToken cancellation);
}
public interface IAtkHidConnection : IAsyncDisposable
{
    // Only the three telemetry opcodes can be requested, never an arbitrary output report.
    Task<AtkReplyObservation> QueryAsync(AtkReadCommand command, CancellationToken cancellation);
}

public sealed class AtkInvalidReplyException(AtkReplyError error) : IOException("ATK reply rejected.")
{
    public AtkReplyError Error { get; } = error;
}
