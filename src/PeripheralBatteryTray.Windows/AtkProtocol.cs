namespace PeripheralBatteryTray.Windows;

// Protocol facts independently implemented from ATK HUB 3.2.29; see docs/ATK_PROTOCOL_RESEARCH.md.
// These commands request telemetry only. No arbitrary opcode or payload API is exposed.
public enum AtkReadCommand : byte { WirelessOnline = 0x03, Battery = 0x04, MouseIdentity = 0x10 }
public enum AtkReplyError { None, Length, ReportId, Command, Echo, DataLength }
public sealed record AtkReplyObservation(AtkReadCommand Command, byte RawStatus, byte DeclaredDataLength,
    bool MatchesRequestChecksumConvention, bool? OnlineCandidate = null, bool? DuckbillUltraCandidate = null,
    int? PercentCandidate = null, byte? RawCharge = null, byte? RawVoltageHigh = null, byte? RawVoltageLow = null);

public static class AtkProtocol
{
    public const byte ReportId = 8;
    public const int WindowsReportBytes = 17;
    public static bool IsReadCommand(AtkReadCommand command)
        => command is AtkReadCommand.WirelessOnline or AtkReadCommand.Battery or AtkReadCommand.MouseIdentity;

    public static byte[] BuildRequest(AtkReadCommand command)
    {
        if (!IsReadCommand(command)) throw new ArgumentOutOfRangeException(nameof(command));
        byte[] result = new byte[WindowsReportBytes];
        result[0] = ReportId; result[1] = (byte)command;
        result[16] = unchecked((byte)(0x55 - ReportId - (byte)command));
        return result;
    }

    /// <summary>Inspect a Windows report, never promote it to a trusted battery reading.
    /// Response status/checksum semantics still need target-device evidence.</summary>
    public static AtkReplyError InspectReply(ReadOnlySpan<byte> report, AtkReadCommand expected,
        out AtkReplyObservation? observation)
    {
        observation = null;
        if (!IsReadCommand(expected)) throw new ArgumentOutOfRangeException(nameof(expected));
        if (report.Length != WindowsReportBytes) return AtkReplyError.Length;
        if (report[0] != ReportId) return AtkReplyError.ReportId;
        if (report[1] != (byte)expected) return AtkReplyError.Command;
        if (report.SequenceEqual(BuildRequest(expected))) return AtkReplyError.Echo;
        // The target receiver declares two battery bytes (percentage and raw charge).
        // A longer fixed-size report does not make following bytes declared voltage data.
        int required = expected == AtkReadCommand.WirelessOnline ? 1 : 2;
        // Payload has ten data bytes. Zero-filled/synthetic timeout buffers are not telemetry.
        if (report[5] < required || report[5] > 10) return AtkReplyError.DataLength;
        int sum = 0; foreach (byte value in report) sum += value;
        bool? online = expected == AtkReadCommand.WirelessOnline
            ? report[6] switch { 0 => false, 1 => true, _ => null } : null;
        bool? model = expected == AtkReadCommand.MouseIdentity ? report[6] == 1 && report[7] == 2 : null;
        observation = new(expected, report[2], report[5], (sum & 255) == 0x55, online, model,
            expected == AtkReadCommand.Battery && report[6] <= 100 ? report[6] : null,
            expected == AtkReadCommand.Battery ? report[7] : null,
            expected == AtkReadCommand.Battery && report[5] >= 4 ? report[8] : null,
            expected == AtkReadCommand.Battery && report[5] >= 4 ? report[9] : null);
        return AtkReplyError.None;
    }
}
