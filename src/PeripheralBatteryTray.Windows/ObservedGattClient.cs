using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.Windows;

/// <summary>Only the existing read interface is exposed. Adapter-step totals do not stand for radio packets.</summary>
public sealed class ObservedGattClient(IGattClient client, DiagnosticTelemetry telemetry) : IGattClient
{
    private IGattCall<T> Call<T>(DiagnosticStep step, Func<IGattCall<T>> start)
    {
        telemetry.AdapterStarted(ReadingSource.GattUncached, step);
        try { return new ObservedCall<T>(start(), telemetry); }
        catch { telemetry.AdapterTerminal(ReadingSource.GattUncached); throw; }
    }
    private sealed class ObservedCall<T> : IGattCall<T>
    {
        private readonly IGattCall<T> _original;
        public Task<T> Terminal { get; }
        public ObservedCall(IGattCall<T> original, DiagnosticTelemetry telemetry)
        { _original = original; Terminal = Observe(original, telemetry); }
        private static async Task<T> Observe(IGattCall<T> original, DiagnosticTelemetry telemetry)
        { try { return await original.Terminal.ConfigureAwait(false); } finally { telemetry.AdapterTerminal(ReadingSource.GattUncached); } }
        public void Cancel() => _original.Cancel();
    }
    private sealed class Projected<T, R>(IGattCall<T> original, Func<T, R> map) : IGattCall<R>
    {
        public Task<R> Terminal { get; } = Map(original, map);
        private static async Task<R> Map(IGattCall<T> call, Func<T, R> project) => project(await call.Terminal.ConfigureAwait(false));
        public void Cancel() => original.Cancel();
    }
    public IGattCall<IGattDevice?> Open(AepId id, Func<bool> mayStart)
        => new Projected<IGattDevice?, IGattDevice?>(Call(DiagnosticStep.Open, () => client.Open(id, mayStart)),
            d => d is null ? null : new Device(this, d));
    private sealed class Device(ObservedGattClient owner, IGattDevice device) : IGattDevice
    {
        public ConnectionState Connection => device.Connection;
        public event Action? ServicesChanged { add => device.ServicesChanged += value; remove => device.ServicesChanged -= value; }
        public event Action? ConnectionChanged { add => device.ConnectionChanged += value; remove => device.ConnectionChanged -= value; }
        public IGattCall<GattResult<IReadOnlyList<IGattService>>> Services(Guid uuid, GattCacheMode cache)
        {
            if (uuid != BatteryGattUuids.Service) throw new ArgumentException("Only BAS is permitted.");
            return new Projected<GattResult<IReadOnlyList<IGattService>>, GattResult<IReadOnlyList<IGattService>>>(
                owner.Call(DiagnosticStep.Services, () => device.Services(uuid, cache)),
                r => new(r.Status, r.Value.Select(s => (IGattService)new Service(owner, s)).ToArray(), r.ProtocolError));
        }
        public void Dispose() => device.Dispose();
    }
    private sealed class Service(ObservedGattClient owner, IGattService service) : IGattService
    {
        public object Identity => service.Identity;
        public ushort Handle => service.Handle;
        public IGattCall<GattResult<IReadOnlyList<IGattCharacteristic>>> Characteristics(Guid uuid, GattCacheMode cache)
        {
            if (uuid != BatteryGattUuids.Level) throw new ArgumentException("Only Battery Level is permitted.");
            return new Projected<GattResult<IReadOnlyList<IGattCharacteristic>>, GattResult<IReadOnlyList<IGattCharacteristic>>>(
                owner.Call(DiagnosticStep.Characteristics, () => service.Characteristics(uuid, cache)),
                r => new(r.Status, r.Value.Select(c => (IGattCharacteristic)new Characteristic(owner, c)).ToArray(), r.ProtocolError));
        }
        public void Dispose() => service.Dispose();
    }
    private sealed class Characteristic(ObservedGattClient owner, IGattCharacteristic characteristic) : IGattCharacteristic
    {
        public ushort Handle => characteristic.Handle;
        public bool CanRead => characteristic.CanRead;
        public IGattCall<GattResult<byte[]?>> Read(GattCacheMode cache)
        {
            if (cache != GattCacheMode.Uncached) throw new ArgumentException("Reads must be Uncached.");
            return owner.Call(DiagnosticStep.Read, () => characteristic.Read(cache));
        }
    }
}
