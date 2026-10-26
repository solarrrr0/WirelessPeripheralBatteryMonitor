using PeripheralBatteryTray.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace PeripheralBatteryTray.Windows;

/// <summary>Posts FromId to the existing UI. The dispatcher must enqueue without blocking.</summary>
public sealed class WinRtGattClient(Action<Action> postToUi) : IGattClient
{
    public IGattCall<IGattDevice?> Open(AepId id, Func<bool> mayStart)
    {
        var queued = new QueuedCall<IGattDevice?>(() => new NativeCall<BluetoothLEDevice, IGattDevice?>(
            BluetoothLEDevice.FromIdAsync(id.Value), device => device is null ? null : new Device(device)), mayStart);
        try { postToUi(queued.Start); } catch (Exception e) { queued.Fail(e); }
        return queued;
    }
    private sealed class QueuedCall<T>(Func<IGattCall<T>> start, Func<bool> mayStart) : IGattCall<T>
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<T> _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IGattCall<T>? _original;
        private bool _cancelled;
        public Task<T> Terminal => _terminal.Task;
        public void Cancel() { IGattCall<T>? original; lock (_gate) { _cancelled = true; original = _original; }
            original?.Cancel(); }
        public void Fail(Exception error) => _terminal.TrySetException(error);
        public void Start()
        {
            IGattCall<T> call;
            try
            {
                lock (_gate)
                {
                    if (_cancelled || !mayStart()) { _terminal.TrySetCanceled(); return; }
                    _original = call = start();
                }
            }
            catch (Exception e) { Fail(e); return; }
            _ = Observe(call);
        }
        private async Task Observe(IGattCall<T> call)
        {
            try { _terminal.TrySetResult(await call.Terminal.ConfigureAwait(false)); }
            catch (OperationCanceledException) { _terminal.TrySetCanceled(); }
            catch (Exception e) { Fail(e); }
        }
    }
    private sealed class NativeCall<TNative, TResult>(IAsyncOperation<TNative> original,
        Func<TNative, TResult> project) : IGattCall<TResult>
    {
        public Task<TResult> Terminal { get; } = Observe(original, project);
        public void Cancel() => original.Cancel();
        private static async Task<TResult> Observe(IAsyncOperation<TNative> operation, Func<TNative, TResult> map)
            => map(await operation.AsTask().ConfigureAwait(false)); // No cancellation/timeout wrapper on the original terminal.
    }
    private static BluetoothCacheMode Cache(GattCacheMode cache) => cache == GattCacheMode.Cached ? BluetoothCacheMode.Cached : BluetoothCacheMode.Uncached;
    private static GattStatus Status(GattCommunicationStatus status) => status switch {
        GattCommunicationStatus.Success => GattStatus.Success, GattCommunicationStatus.AccessDenied => GattStatus.AccessDenied,
        GattCommunicationStatus.ProtocolError => GattStatus.ProtocolError, _ => GattStatus.Unreachable };
    private sealed class Device : IGattDevice
    {
        private readonly BluetoothLEDevice _device;
        public Device(BluetoothLEDevice device)
        { _device = device; _device.GattServicesChanged += ServicesChangedNative; _device.ConnectionStatusChanged += ConnectionChangedNative; }
        public ConnectionState Connection => _device.ConnectionStatus == BluetoothConnectionStatus.Connected ? ConnectionState.Connected : ConnectionState.Disconnected;
        public event Action? ServicesChanged;
        public event Action? ConnectionChanged;
        private void ServicesChangedNative(BluetoothLEDevice sender, object args) => ServicesChanged?.Invoke();
        private void ConnectionChangedNative(BluetoothLEDevice sender, object args) => ConnectionChanged?.Invoke();
        public IGattCall<GattResult<IReadOnlyList<IGattService>>> Services(Guid uuid, GattCacheMode cache)
        {
            if (uuid != BatteryGattUuids.Service) throw new ArgumentException("Only BAS is permitted.");
            return new NativeCall<GattDeviceServicesResult, GattResult<IReadOnlyList<IGattService>>>(
                _device.GetGattServicesForUuidAsync(uuid, Cache(cache)), result => new(Status(result.Status),
                    result.Services.Select(s => (IGattService)new Service(s)).ToArray(), result.ProtocolError));
        }
        public void Dispose()
        {
            try { _device.GattServicesChanged -= ServicesChangedNative; }
            finally { try { _device.ConnectionStatusChanged -= ConnectionChangedNative; } finally { _device.Dispose(); } }
        }
    }
    private sealed class Service(GattDeviceService service) : IGattService
    {
        public object Identity => service;
        public ushort Handle => service.AttributeHandle;
        public IGattCall<GattResult<IReadOnlyList<IGattCharacteristic>>> Characteristics(Guid uuid, GattCacheMode cache)
        {
            if (uuid != BatteryGattUuids.Level) throw new ArgumentException("Only Battery Level is permitted.");
            return new NativeCall<GattCharacteristicsResult, GattResult<IReadOnlyList<IGattCharacteristic>>>(
                service.GetCharacteristicsForUuidAsync(uuid, Cache(cache)), result => new(Status(result.Status),
                    result.Characteristics.Select(c => (IGattCharacteristic)new Characteristic(c)).ToArray(), result.ProtocolError));
        }
        public void Dispose() => service.Dispose();
    }
    private sealed class Characteristic(GattCharacteristic characteristic) : IGattCharacteristic
    {
        public ushort Handle => characteristic.AttributeHandle;
        public bool CanRead => characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read);
        public IGattCall<GattResult<byte[]?>> Read(GattCacheMode cache)
        {
            if (cache != GattCacheMode.Uncached) throw new ArgumentException("Battery reads must be Uncached.");
            return new NativeCall<GattReadResult, GattResult<byte[]?>>(characteristic.ReadValueAsync(BluetoothCacheMode.Uncached), result =>
            {
                byte[]? bytes = null;
                if (result.Status == GattCommunicationStatus.Success && result.Value is { Length: 1 } buffer)
                { using var reader = DataReader.FromBuffer(buffer); bytes = [reader.ReadByte()]; }
                return new(Status(result.Status), bytes, result.ProtocolError);
            });
        }
    }
}
