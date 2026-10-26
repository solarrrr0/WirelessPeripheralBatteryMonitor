using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.Tests;

internal static class LowBatteryCases
{
    internal static void Run(Action<string, string, Action> run, Action<bool> check)
    {
        long sequence = 0;
        LowBatterySample Sample(int? p, string key = "a", string device = "mouse", string context = "epoch1")
            => new(key, device, "Mouse", p, context, ++sequence);
        run("LB01", "Low battery requires a fresh crossing, deduplicates observations and rearms with hysteresis", () =>
        {
            var monitor = new LowBatteryMonitor();
            check(monitor.Observe([Sample(90), Sample(0)]).Count == 0 && monitor.Tracked == 0);
            monitor.Configure(true, 20);
            check(monitor.Observe([Sample(19)]).Count == 0); // Initial low is only a baseline.
            check(monitor.Observe([Sample(21)]).Count == 0 && monitor.Observe([Sample(19)]).Count == 0);
            check(monitor.Observe([Sample(25)]).Count == 0);
            var low = Sample(20);
            check(monitor.Observe([low]).Single().Percent == 20);
            for (int i = 0; i < 100; i++) check(monitor.Observe([low]).Count == 0);
            check(monitor.Observe([Sample(21)]).Count == 0 && monitor.Observe([Sample(20)]).Count == 0);
            check(monitor.Observe([Sample(25)]).Count == 0);
            check(monitor.Observe([low]).Count == 0); // A replay is older than the rearming evidence.
            check(monitor.Observe([Sample(0)]).Single().Percent == 0 && monitor.Crossings == 2);
        });
        run("LB02", "Unknown, stale and changed contexts never synthesize lows or deferred notifications", () =>
        {
            var monitor = new LowBatteryMonitor(); monitor.Configure(true, 20);
            check(monitor.Observe([Sample(80)]).Count == 0);
            check(monitor.Observe([Sample(null)]).Count == 0 && monitor.Observe([Sample(101)]).Count == 0);
            check(monitor.Observe([Sample(19)]).Count == 1); // The next genuinely fresh value may cross.
            monitor.Observe([Sample(null)]);
            check(monitor.Observe([Sample(18)]).Count == 0); // Offline does not rearm.
            monitor.Observe([Sample(90)]);
            check(monitor.Observe([Sample(10, context: "epoch2")]).Count == 0);
            monitor.Configure(true, 10);
            check(monitor.Observe([Sample(0, context: "epoch2")]).Count == 0);
            monitor.Configure(false, 10); check(monitor.Tracked == 0 && monitor.Observe([Sample(100)]).Count == 0);
            monitor.Configure(true, 10); check(monitor.Observe([Sample(5)]).Count == 0);
            monitor.ResetBaselines(); check(monitor.Observe([Sample(4)]).Count == 0);
            bool rejected = false; try { monitor.Configure(true, 0); } catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected);
        });
        run("LB03", "Multiple components merge by device and tracked state is bounded and pruned", () =>
        {
            var monitor = new LowBatteryMonitor(); monitor.Configure(true, 20);
            monitor.Observe([Sample(90, "a"), Sample(80, "b"), Sample(75, "c", "keyboard")]);
            var alerts = monitor.Observe([Sample(19, "a"), Sample(15, "b"), Sample(0, "c", "keyboard")]);
            check(alerts.Count == 2 && alerts.Single(a => a.DeviceKey == "mouse").Percent == 15);
            check(monitor.Observe([Sample(80, "a"), Sample(0, "a")]).Count == 0); // Ambiguous duplicate rejected.
            check(monitor.Tracked == 0);
            monitor.Observe(Enumerable.Range(0, 1000).Select(i => Sample(90, "key" + i)));
            check(monitor.Tracked == LowBatteryMonitor.MaxTracked);
            check(monitor.Observe([]).Count == 0 && monitor.Tracked == 0);
            check(monitor.Observe([Sample(1)]).Count == 0);
        });
    }
}
