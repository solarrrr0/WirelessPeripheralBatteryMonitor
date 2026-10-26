namespace PeripheralBatteryTray.Core;

/// <summary>Only current direct percentages qualify; null preserves the latch without triggering it.</summary>
public sealed record LowBatterySample(string Key, string DeviceKey, string DisplayName, int? Percent, string Context, long Observation);
public sealed record LowBatteryAlert(string DeviceKey, string DisplayName, int Percent);

/// <summary>Event-driven, session-local crossing detection. No timers, persistence or device access.</summary>
public sealed class LowBatteryMonitor
{
    public const int MaxTracked = 256;
    private sealed class State(string context, bool armed, long observation)
    { public string Context = context; public bool Armed = armed; public long Observation = observation; }
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    public bool Enabled { get; private set; }
    public int Threshold { get; private set; } = 20;
    public long Crossings { get; private set; }
    public int Tracked => _states.Count;
    public void Configure(bool enabled, int threshold)
    {
        if (threshold is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (Enabled != enabled || Threshold != threshold) ResetBaselines();
        Enabled = enabled; Threshold = threshold;
    }
    public void ResetBaselines() => _states.Clear();
    public IReadOnlyList<LowBatteryAlert> Observe(IEnumerable<LowBatterySample> samples)
    {
        if (!Enabled) return [];
        // Conflicting duplicate keys cannot manufacture a crossing in one snapshot.
        var unique = samples.GroupBy(s => s.Key, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .Select(g => g.First()).Take(MaxTracked).ToArray();
        var present = unique.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string missing in _states.Keys.Where(k => !present.Contains(k)).ToArray()) _states.Remove(missing);
        var crossings = new List<LowBatteryAlert>();
        foreach (var sample in unique)
        {
            if (sample.Percent is not (>= 0 and <= 100)) continue;
            int percent = sample.Percent.Value;
            if (!_states.TryGetValue(sample.Key, out var state) || state.Context != sample.Context)
            { _states[sample.Key] = new(sample.Context, percent > Threshold, sample.Observation); continue; }
            if (sample.Observation <= state.Observation) continue;
            state.Observation = sample.Observation;
            if (percent >= Threshold + 5) state.Armed = true;
            if (!state.Armed || percent > Threshold) continue;
            state.Armed = false; // Consume even when the shell later suppresses or rejects the notification.
            crossings.Add(new(sample.DeviceKey, sample.DisplayName, percent));
        }
        var alerts = crossings.GroupBy(x => x.DeviceKey, StringComparer.Ordinal)
            .Select(g => new LowBatteryAlert(g.Key, g.First().DisplayName, g.Min(x => x.Percent))).ToArray();
        Crossings += alerts.Length;
        return alerts;
    }
}
