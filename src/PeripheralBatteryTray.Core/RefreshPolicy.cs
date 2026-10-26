namespace PeripheralBatteryTray.Core;

/// <summary>One bounded entry per actual service path, including a discovery sentinel.</summary>
public sealed class RefreshPolicy
{
    public int ConsecutiveFailures { get; private set; }
    public bool Paused { get; private set; }
    public long? NextTimestamp { get; private set; }
    public void Reset() { ConsecutiveFailures = 0; Paused = false; NextTimestamp = null; }
    public void Complete(ReadOutcome outcome, long now, TimeSpan period, TimeProvider time)
    {
        if (outcome == ReadOutcome.Cancelled) return;
        if (outcome == ReadOutcome.Unknown) { NextTimestamp = Add(now, period, time); return; }
        if (outcome == ReadOutcome.Success) { ConsecutiveFailures = 0; Paused = false; }
        else if (outcome is ReadOutcome.AccessDenied or ReadOutcome.Unsupported) Paused = true;
        else ConsecutiveFailures = Math.Min(ConsecutiveFailures + 1, 31);
        int minutes = ConsecutiveFailures == 0 ? 0 : ConsecutiveFailures >= 6 ? 30 : 1 << (ConsecutiveFailures - 1);
        TimeSpan delay = TimeSpan.FromMinutes(minutes);
        NextTimestamp = Add(now, delay > period ? delay : period, time);
    }
    public static long Add(long timestamp, TimeSpan delay, TimeProvider time)
        => checked(timestamp + (long)Math.Ceiling(delay.TotalSeconds * time.TimestampFrequency));
}
