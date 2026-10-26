using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeripheralBatteryTray.Core;

public enum DiagnosticStep { QueryStarted, LogicalTimeout, CancelRequested, OperationTerminal, ValueAccepted,
    MetadataEnumerate, MetadataLocate, MetadataProperty, Open, Services, Characteristics, Read, CleanupFailure }
public sealed record DiagnosticEvent(ReadingSource Source, DiagnosticStep Step, ReadOutcome? Outcome, long Timestamp, int Count = 1);
public sealed class DiagnosticRing
{
    private readonly object _gate = new();
    private readonly DiagnosticEvent[] _items = new DiagnosticEvent[128];
    private int _start, _count;
    public void Add(ReadingSource source, DiagnosticStep step, ReadOutcome? outcome, long timestamp)
    {
        if (!Enum.IsDefined(source) || !Enum.IsDefined(step) || (outcome.HasValue && !Enum.IsDefined(outcome.Value)))
            throw new ArgumentOutOfRangeException(nameof(step));
        lock (_gate)
        {
            if (_count > 0)
            {
                int last = (_start + _count - 1) % _items.Length;
                var previous = _items[last];
                if (previous.Source == source && previous.Step == step && previous.Outcome == outcome)
                { _items[last] = previous with { Timestamp = timestamp, Count = (int)Math.Min(int.MaxValue, (long)previous.Count + 1) }; return; }
            }
            int index = (_start + _count) % _items.Length;
            if (_count == _items.Length) { index = _start; _start = (_start + 1) % _items.Length; } else _count++;
            _items[index] = new(source, step, outcome, timestamp);
        }
    }
    public IReadOnlyList<DiagnosticEvent> Snapshot()
    { lock (_gate) return Array.AsReadOnly(Enumerable.Range(0, _count).Select(i => _items[(_start + i) % _items.Length]).ToArray()); }
}

public sealed record SourceCounters(ReadingSource Source, long Queries, long AdapterCallsStarted, long AdapterCallsTerminal,
    long Values, long OperationTerminals, long Timeouts, long Cancellations, long CleanupErrors);
public sealed record TelemetrySnapshot(IReadOnlyList<SourceCounters> Counters, IReadOnlyList<DiagnosticEvent> Events);
public sealed class DiagnosticTelemetry(TimeProvider? time = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly DiagnosticRing _ring = new();
    private readonly SourceCounters[] _counters = Enum.GetValues<ReadingSource>().Select(s => new SourceCounters(s, 0, 0, 0, 0, 0, 0, 0, 0)).ToArray();
    public void Record(ReadingSource source, DiagnosticStep step, ReadOutcome? outcome = null, int cleanupErrors = 0)
    {
        lock (_gate)
        {
            _ring.Add(source, step, outcome, _time.GetTimestamp());
            var c = _counters[(int)source];
            _counters[(int)source] = step switch
            {
                DiagnosticStep.QueryStarted => c with { Queries = c.Queries + 1 },
                DiagnosticStep.OperationTerminal => c with { OperationTerminals = c.OperationTerminals + 1, CleanupErrors = c.CleanupErrors + Math.Max(0, cleanupErrors) },
                DiagnosticStep.ValueAccepted => c with { Values = c.Values + 1 },
                DiagnosticStep.LogicalTimeout => c with { Timeouts = c.Timeouts + 1 },
                DiagnosticStep.CancelRequested => c with { Cancellations = c.Cancellations + 1 },
                _ => c
            };
        }
    }
    public void AdapterStarted(ReadingSource source, DiagnosticStep step)
    { lock (_gate) { Record(source, step); var c = _counters[(int)source]; _counters[(int)source] = c with { AdapterCallsStarted = c.AdapterCallsStarted + 1 }; } }
    public void AdapterTerminal(ReadingSource source)
    { lock (_gate) { var c = _counters[(int)source]; _counters[(int)source] = c with { AdapterCallsTerminal = c.AdapterCallsTerminal + 1 }; } }
    public TelemetrySnapshot Capture()
    { lock (_gate) return new(Array.AsReadOnly(_counters.ToArray()), _ring.Snapshot()); }
}

public sealed record DiagnosticValue(string Alias, ReadingSource Source, ReadOutcome Outcome, int? Percent, BatteryBand? Band,
    DateTimeOffset? ObservedAtUtc, DateTimeOffset? LastDeviceReadUtc, bool Current);
public sealed record DiagnosticDevice(string Alias, string Model, ConnectionState Connection, IReadOnlyList<DiagnosticValue> Values);
public sealed record AtkDiagnosticSummary(bool AutoEnabled, int IntervalMinutes, string State, string Outcome,
    int Batches, int BatchesTerminal, int RequestsStarted, int RequestsTerminal, int Timeouts, int CleanupErrors,
    int? Percent, int? LastPercent, DateTimeOffset? ObservedAtUtc);
public sealed record LowBatteryDiagnosticSummary(bool Enabled, int ThresholdPercent, long Crossings,
    long ShellRequests, long Suppressed, long Failures);
public sealed record DiagnosticReport(int SchemaVersion, string BuildId, string Capability, BatteryMode Mode, string Runtime,
    string OsVersion, string Architecture, DateTimeOffset CapturedAtUtc, long SnapshotVersion, int PollIntervalMinutes,
    int TotalDevices, int TotalValues, int Draining, ShutdownResult? Shutdown, IReadOnlyList<SourceCounters> Counters,
    IReadOnlyList<DiagnosticDevice> Devices, IReadOnlyList<DiagnosticEvent> Events,
    bool Truncated, int OmittedDevices, int OmittedValues, int OmittedEvents,
    AtkDiagnosticSummary? Atk = null, LowBatteryDiagnosticSummary? LowBattery = null);

public static class DiagnosticExporter
{
    public const int MaxBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static DiagnosticReport Create(BatterySnapshot snapshot, TimeProvider time, TimeSpan period, TelemetrySnapshot telemetry,
        string buildId, string capability, int draining, ShutdownResult? shutdown)
    {
        string token = Guid.NewGuid().ToString("N")[..8];
        var devices = snapshot.Devices.Select((d, i) =>
        {
            string alias = $"device-{token}-{i + 1}";
            var values = d.SystemCandidates.Select((s, j) => Value(s, $"{alias}-system-{j + 1}", false))
                .Concat(d.Components.Select((c, j) => Value(c.Source, $"{alias}-battery-{j + 1}",
                    BatteryRules.IsCurrentValue(c, d.Binding, snapshot.Mode, time, period)))).ToArray();
            string model = d.DisplayName == "MOMENTUM TW 4" ? "MOMENTUM TW 4" : $"蓝牙设备{i + 1}";
            return new DiagnosticDevice(alias, model, BatteryRules.GetConnection(d.Binding, snapshot.Mode), values);
        }).ToArray();
        return new(1, buildId, capability, snapshot.Mode, Environment.Version.ToString(), Environment.OSVersion.Version.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), DateTimeOffset.UtcNow, snapshot.Version,
            (int)period.TotalMinutes, devices.Length, devices.Sum(d => d.Values.Count), draining, shutdown,
            telemetry.Counters, devices, telemetry.Events, false, 0, 0, 0);
    }
    private static DiagnosticValue Value(SourceState s, string alias, bool current)
        => new(alias, s.Source, s.LatestOutcome, s.LastSuccess?.Value.Percent, s.LastSuccess?.Value.Band,
            s.LastSuccess?.ObservedAtUtc, s.LastSuccessfulReadUtc, current);
    public static byte[] Serialize(DiagnosticReport report)
    {
        var current = report;
        while (true)
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(current, Options);
            if (data.Length <= MaxBytes) return data;
            if (current.Events.Count > 0)
            { current = current with { Events = current.Events.Skip(1).ToArray(), Truncated = true, OmittedEvents = current.OmittedEvents + 1 }; continue; }
            if (current.Devices.Count > 0)
            {
                var last = current.Devices[^1];
                if (last.Values.Count > 0)
                    current = current with { Devices = current.Devices.Take(current.Devices.Count - 1).Append(last with { Values = last.Values.Take(last.Values.Count - 1).ToArray() }).ToArray(), Truncated = true, OmittedValues = current.OmittedValues + 1 };
                else current = current with { Devices = current.Devices.Take(current.Devices.Count - 1).ToArray(), Truncated = true, OmittedDevices = current.OmittedDevices + 1 };
                continue;
            }
            throw new InvalidOperationException("Minimal diagnostic report exceeds budget.");
        }
    }
}
