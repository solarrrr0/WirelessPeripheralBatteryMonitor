namespace PeripheralBatteryTray.Core;

public interface ILegacyBatteryProvider : IDisposable
{
    ValueTask<IReadOnlyList<DeviceBatteryState>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Deterministic examples only; never opens a device or reads system battery properties.</summary>
public sealed class MockBatteryProvider : ILegacyBatteryProvider, IBatteryProvider
{
    private bool _disposed;
    private readonly TimeProvider _time;

    public MockBatteryProvider(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public ValueTask<IReadOnlyList<BatteryDeviceState>> ReadModelsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _time.GetUtcNow();
        long timestamp = _time.GetTimestamp();
        BatteryDeviceState Create(string id, string name, string transport, bool connected, BatteryValue? value)
        {
            var binding = new DeviceBinding { DeviceId = id, BindingKey = id,
                PreferenceKey = DisplaySettings.KeyFor(id),
                Mock = new(connected ? ConnectionState.Connected : ConnectionState.Disconnected, 1) };
            QueryStamp stamp = binding.Stamp(ReadingSource.Mock, 1, TransportKind.Mock);
            var source = new SourceState { Source = ReadingSource.Mock, CandidateKey = id,
                ComponentKey = id + "/battery", LatestRequestStamp = stamp,
                LatestOutcome = connected ? ReadOutcome.Success : ReadOutcome.Offline,
                LastSuccess = value is null ? null : new(value, ReadingSource.Mock, stamp, now, timestamp) };
            return new(binding, name, transport, components:
                [new(id + "/battery", id + "/service", 0, source, connected)]);
        }
        IReadOnlyList<BatteryDeviceState> devices = Array.AsReadOnly(new[] {
            Create("mock:mouse", "示例无线鼠标", "2.4 GHz", true, BatteryValue.FromPercent(73)),
            Create("mock:keyboard", "示例无线键盘", "2.4 GHz", true, BatteryValue.FromBand(BatteryBand.High)),
            Create("mock:headset", "示例蓝牙耳机", "蓝牙", false, null)
        });
        return ValueTask.FromResult(devices);
    }

    public async ValueTask<IReadOnlyList<DeviceBatteryState>> ReadAsync(CancellationToken cancellationToken)
    {
        // Compatibility projection for the unchanged Phase 1 App; batch C will consume snapshots.
        IReadOnlyList<BatteryDeviceState> models = await ReadModelsAsync(cancellationToken);
        return Array.AsReadOnly(models.Select(model =>
        {
            BatteryReading? reading = model.Components[0].Source.LastSuccess;
            bool connected = BatteryRules.GetConnection(model.Binding, BatteryMode.Mock) == ConnectionState.Connected;
            return new DeviceBatteryState { DeviceId = model.Binding.DeviceId, DisplayName = model.DisplayName,
                ConnectionType = model.ConnectionType, Connected = connected,
                ReadStatus = connected ? BatteryReadStatus.Success : BatteryReadStatus.Offline,
                BatteryPercent = reading?.Value.Percent, BatteryBand = reading?.Value.Band,
                IsCharging = model.Binding.DeviceId == "mock:mouse" ? false : null,
                Source = "Mock / 模拟", LastSuccessfulReadUtc = reading?.ObservedAtUtc,
                LastSeenUtc = connected ? reading?.ObservedAtUtc : null };
        }).ToArray());
    }

    public void Dispose() => _disposed = true;

    public ReadingSource Source => ReadingSource.Mock;
    public IBatteryReadOperation CreateRead(QueryRequest request, CancellationToken lifetime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new MockReadOperation(this, request, lifetime);
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    private sealed class MockReadOperation(MockBatteryProvider owner, QueryRequest request,
        CancellationToken lifetime) : IBatteryReadOperation
    {
        private readonly TaskCompletionSource<ReadCompletion> _completion = new();
        private int _started;
        private int _cancelled;
        public Task<ReadCompletion> Completion => _completion.Task;
        public void RequestCancel() => Interlocked.Exchange(ref _cancelled, 1);
        public void Start(Action<ProviderUpdate> publish)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Already started.");
            try
            {
                if (Volatile.Read(ref _cancelled) != 0 || lifetime.IsCancellationRequested)
                { _completion.TrySetResult(new(ReadOutcome.Cancelled)); return; }
                BatteryDeviceState model = owner.ReadModelsAsync(lifetime).Result.Single(x => x.Binding.DeviceId == request.Stamp.DeviceId);
                ComponentState component = model.Components[0];
                publish(new DirectoryUpdate(1, DirectoryScope.ServiceCharacteristics, component.ServiceKey,
                    [new(component.ComponentKey, component.ServiceKey, component.DisplayOrder)], true));
                publish(new ValueUpdate(2, component.ComponentKey, component.ServiceKey,
                    component.Source.LatestOutcome, component.Source.LastSuccess?.Value,
                    owner._time.GetUtcNow(), owner._time.GetTimestamp()));
                _completion.TrySetResult(new(ReadOutcome.Success));
            }
            catch (OperationCanceledException) { _completion.TrySetResult(new(ReadOutcome.Cancelled)); }
            catch (Exception) { _completion.TrySetResult(new(ReadOutcome.Error)); }
        }
    }
}
