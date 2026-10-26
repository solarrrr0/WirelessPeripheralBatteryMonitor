using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.Windows;

public sealed class WinRtEndpointWatcherFactory : IEndpointWatcherFactory
{
    public IEndpointWatcher Create(TransportKind transport, bool pairedOnly)
    {
        if (!pairedOnly || transport == TransportKind.Mock) throw new ArgumentException("Paired Bluetooth endpoints only.");
        string selector = transport == TransportKind.Ble ? BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)
            : BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        return new WinRtEndpointWatcher(DeviceInformation.CreateWatcher(selector,
            [BluetoothDiscovery.Name, BluetoothDiscovery.Connected, BluetoothDiscovery.Paired,
                BluetoothDiscovery.Container, BluetoothDiscovery.Battery, "System.Devices.Aep.ProtocolId"], DeviceInformationKind.AssociationEndpoint));
    }
    private sealed class WinRtEndpointWatcher : IEndpointWatcher
    {
        private readonly DeviceWatcher _watcher;
        public event Action<EndpointEvent>? Changed;
        public WinRtEndpointWatcher(DeviceWatcher watcher)
        {
            _watcher = watcher;
            watcher.Added += Added; watcher.Updated += Updated; watcher.Removed += Removed;
            watcher.EnumerationCompleted += Completed; watcher.Stopped += Stopped;
        }
        private static Dictionary<string, object?> Copy(IEnumerable<KeyValuePair<string, object>> properties)
            => properties.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal);
        private void Added(DeviceWatcher sender, DeviceInformation info)
        {
            var properties = Copy(info.Properties); properties[BluetoothDiscovery.Name] = info.Name;
            properties[BluetoothDiscovery.Paired] = info.Pairing.IsPaired;
            Changed?.Invoke(new(EndpointEventKind.Added, new(info.Id), properties));
        }
        private void Updated(DeviceWatcher sender, DeviceInformationUpdate info)
            => Changed?.Invoke(new(EndpointEventKind.Updated, new(info.Id), Copy(info.Properties)));
        private void Removed(DeviceWatcher sender, DeviceInformationUpdate info)
            => Changed?.Invoke(new(EndpointEventKind.Removed, new(info.Id), new Dictionary<string, object?>()));
        private void Completed(DeviceWatcher sender, object args)
            => Changed?.Invoke(new(EndpointEventKind.EnumerationCompleted, default, new Dictionary<string, object?>()));
        private void Stopped(DeviceWatcher sender, object args)
        {
            _watcher.Added -= Added; _watcher.Updated -= Updated; _watcher.Removed -= Removed;
            _watcher.EnumerationCompleted -= Completed; _watcher.Stopped -= Stopped;
            Changed?.Invoke(new(sender.Status == DeviceWatcherStatus.Aborted ? EndpointEventKind.Aborted : EndpointEventKind.Stopped,
                default, new Dictionary<string, object?>()));
        }
        public void Start() => _watcher.Start();
        public void Stop()
        {
            // Keep only terminal observation during stopping; already delivered events are rejected by epoch/lifetime.
            _watcher.Added -= Added; _watcher.Updated -= Updated; _watcher.Removed -= Removed;
            _watcher.EnumerationCompleted -= Completed;
            if (_watcher.Status is DeviceWatcherStatus.Created or DeviceWatcherStatus.Stopped or DeviceWatcherStatus.Aborted)
                Stopped(_watcher, new object());
            else if (_watcher.Status != DeviceWatcherStatus.Stopping) _watcher.Stop();
        }
    }
}
