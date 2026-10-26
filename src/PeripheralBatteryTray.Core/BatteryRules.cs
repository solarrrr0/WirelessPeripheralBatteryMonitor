namespace PeripheralBatteryTray.Core;

/// <summary>State transformations only. Scheduling, native I/O and UI dispatch belong to later batches.</summary>
public static class BatteryRules
{
    public static bool MatchesBinding(QueryStamp stamp, DeviceBinding binding)
    {
        TransportState? transport = binding.GetTransport(stamp.Transport);
        return stamp.DeviceId == binding.DeviceId && stamp.BindingEpoch == binding.BindingEpoch
            && transport is not null && stamp.TransportEpoch == transport.Epoch
            && stamp.DatabaseEpoch == (stamp.Source == ReadingSource.WindowsProperty ? 0 : binding.GattDatabaseEpoch)
            && stamp.RequestId > 0 && Enum.IsDefined(stamp.Source)
            && (stamp.Source != ReadingSource.GattUncached || stamp.Transport == TransportKind.Ble)
            && (stamp.Source != ReadingSource.Mock || stamp.Transport == TransportKind.Mock)
            && (stamp.Source != ReadingSource.WindowsProperty || stamp.Transport != TransportKind.Mock);
    }
    private static bool SameContext(QueryStamp left, QueryStamp right) => left.DeviceId == right.DeviceId
        && left.Source == right.Source && left.Transport == right.Transport
        && left.BindingEpoch == right.BindingEpoch && left.TransportEpoch == right.TransportEpoch
        && left.DatabaseEpoch == right.DatabaseEpoch;

    public static SourceState BeginRequest(SourceState state, DeviceBinding binding, QueryStamp stamp,
        DateTimeOffset attemptedAtUtc)
    {
        if (state.Source != stamp.Source || !MatchesBinding(stamp, binding)
            || (state.LatestRequestStamp is QueryStamp previous && SameContext(stamp, previous)
                && stamp.RequestId <= previous.RequestId)) return state;
        return state with { LatestRequestStamp = stamp, LastUpdateSequence = 0,
            LastAttemptUtc = attemptedAtUtc, IsRefreshing = true };
    }
    public static SourceState ApplyResult(SourceState state, DeviceBinding binding, QueryStamp stamp,
        long sequence, ReadOutcome outcome, BatteryValue? value, DateTimeOffset observedAtUtc, long capturedTimestamp)
    {
        if (state.Source != stamp.Source || state.LatestRequestStamp != stamp || !MatchesBinding(stamp, binding)
            || sequence <= state.LastUpdateSequence || !Enum.IsDefined(outcome)) return state;
        if (outcome == ReadOutcome.Success && value is null) outcome = ReadOutcome.Error;
        BatteryReading? success = outcome == ReadOutcome.Success
            ? new(value!, state.Source, stamp, observedAtUtc, capturedTimestamp) : state.LastSuccess;
        return state with { LatestOutcome = outcome, LastSuccess = success,
            LastUpdateSequence = sequence, IsRefreshing = false };
    }
    public static ConnectionState GetConnection(DeviceBinding binding, BatteryMode mode)
    {
        if (mode == BatteryMode.Mock) return binding.Mock?.Connection switch {
            ConnectionState.Connected => ConnectionState.Connected,
            ConnectionState.Disconnected => ConnectionState.Disconnected, _ => ConnectionState.Unknown };
        if (binding.Classic?.Connection == ConnectionState.Connected || binding.Ble?.Connection == ConnectionState.Connected)
            return ConnectionState.Connected;
        bool hasTransport = binding.Classic is not null || binding.Ble is not null;
        return hasTransport && (binding.Classic is null || binding.Classic.Connection == ConnectionState.Disconnected)
            && (binding.Ble is null || binding.Ble.Connection == ConnectionState.Disconnected)
            ? ConnectionState.Disconnected : ConnectionState.Unknown;
    }
    public static bool IsCurrentValue(ComponentState component, DeviceBinding binding, BatteryMode mode,
        TimeProvider time, TimeSpan refreshPeriod)
    {
        ValidatePeriod(refreshPeriod);
        SourceState state = component.Source;
        BatteryReading? reading = state.LastSuccess;
        ReadingSource expected = mode == BatteryMode.Mock ? ReadingSource.Mock : ReadingSource.GattUncached;
        if (state.Source != expected || reading is null || reading.Value is null || reading.Source != expected
            || reading.QueryStamp.Source != expected || state.LatestOutcome != ReadOutcome.Success
            || state.ComponentKey != component.ComponentKey || !component.InCurrentGattDirectory
            || component.RemovedFromDirectory || binding.SleepInvalidated
            || !MatchesBinding(reading.QueryStamp, binding)
            || binding.GetTransport(reading.QueryStamp.Transport)?.Connection != ConnectionState.Connected
            || state.LatestRequestStamp is not QueryStamp latest || !SameContext(reading.QueryStamp, latest)
            || reading.QueryStamp.RequestId > latest.RequestId) return false;
        // Starting the next request does not invalidate a fresh success from the same epochs.
        if (mode == BatteryMode.Mock) return true;
        TimeSpan age = time.GetElapsedTime(reading.CapturedTimestamp, time.GetTimestamp());
        return age >= TimeSpan.Zero && age < refreshPeriod + refreshPeriod;
    }
    public static bool IsCurrentPercent(ComponentState component, DeviceBinding binding, BatteryMode mode,
        TimeProvider time, TimeSpan refreshPeriod) => component.Source.LastSuccess?.Value.Percent is not null
            && IsCurrentValue(component, binding, mode, time, refreshPeriod);

    public static ReadingFreshness GetFreshness(SourceState state, ComponentState? component,
        DeviceBinding binding, BatteryMode mode, TimeProvider time, TimeSpan refreshPeriod)
        => state.Source == ReadingSource.WindowsProperty ? ReadingFreshness.SystemCacheUnknownAge
            : component is not null && component.Source == state
                && IsCurrentValue(component, binding, mode, time, refreshPeriod)
                ? ReadingFreshness.Fresh : ReadingFreshness.Stale;

    public static IReadOnlyList<SourceState> GetSystemReports(BatteryDeviceState device) => Array.AsReadOnly(
        device.SystemCandidates.Where(state => state.Source == ReadingSource.WindowsProperty
            && state.LatestOutcome == ReadOutcome.Success && state.LastSuccess is { } reading
            && reading.Source == ReadingSource.WindowsProperty && reading.QueryStamp.Source == ReadingSource.WindowsProperty
            && reading.Value.Percent is not null
            && state.LatestRequestStamp is QueryStamp latest && SameContext(latest, reading.QueryStamp)
            && reading.QueryStamp.RequestId <= latest.RequestId && MatchesBinding(reading.QueryStamp, device.Binding))
        .ToArray());

    /// <summary>Reconciles one directory scope after its request stamp was accepted by the caller.</summary>
    public static IReadOnlyList<ComponentState> ReconcileDirectory(IEnumerable<ComponentState> components,
        DirectoryScope scope, string? parentServiceKey, IEnumerable<string> memberKeys, bool complete)
    {
        if (!Enum.IsDefined(scope) || (scope == DirectoryScope.ServiceCharacteristics && string.IsNullOrEmpty(parentServiceKey)))
            throw new ArgumentException("A characteristic directory requires its parent service.");
        var members = new HashSet<string>(memberKeys, StringComparer.Ordinal);
        return Array.AsReadOnly(components.Select(component =>
        {
            if (scope == DirectoryScope.ServiceCharacteristics && component.ServiceKey != parentServiceKey) return component;
            bool present = members.Contains(scope == DirectoryScope.GattServices ? component.ServiceKey : component.ComponentKey);
            if (!present) return component with { InCurrentGattDirectory = false,
                RemovedFromDirectory = complete || component.RemovedFromDirectory };
            return scope == DirectoryScope.ServiceCharacteristics
                ? component with { InCurrentGattDirectory = true, RemovedFromDirectory = false }
                : component;
        }).ToArray());
    }
    public static BatteryDeviceState InvalidateDatabase(BatteryDeviceState device) => new(
        device.Binding with { GattDatabaseEpoch = checked(device.Binding.GattDatabaseEpoch + 1) },
        device.DisplayName, device.ConnectionType, device.SystemCandidates,
        device.Components.Select(x => x with { InCurrentGattDirectory = false }));

    /// <summary>Only bindings already resolved to the same identity may be combined.</summary>
    public static IReadOnlyList<BatteryDeviceState> GroupResolvedDevices(IEnumerable<BatteryDeviceState> devices)
        => Array.AsReadOnly(devices.GroupBy(x => x.Binding.DeviceId, StringComparer.Ordinal).Select(group =>
        {
            BatteryDeviceState first = group.First();
            // Device states and their owned collections are immutable. A unique resolved device
            // needs no duplicate arrays, source sets or component sets on each snapshot.
            if (group.Count() == 1) return first;
            if (group.Any(x => x.Binding != first.Binding))
                throw new ArgumentException("Conflicting resolved bindings for one device.");
            return new BatteryDeviceState(first.Binding, first.DisplayName, first.ConnectionType,
                group.SelectMany(x => x.SystemCandidates).Distinct(), group.SelectMany(x => x.Components).Distinct());
        }).ToArray());

    public static BatteryTrayVisual GetTrayVisual(IEnumerable<BatteryDeviceState> devices, BatteryMode mode,
        TimeProvider time, TimeSpan refreshPeriod)
    {
        ValidatePeriod(refreshPeriod);
        BatteryDeviceState[] states = GroupResolvedDevices(devices).ToArray();
        int? minimum = states.SelectMany(device => device.Components
            .Where(x => IsCurrentPercent(x, device.Binding, mode, time, refreshPeriod))
            .Select(x => x.Source.LastSuccess!.Value.Percent)).Min();
        return new(minimum, states.Any(x => GetConnection(x.Binding, mode) == ConnectionState.Disconnected),
            states.Any(device => GetConnection(device.Binding, mode) != ConnectionState.Disconnected
                && (!device.Components.Any(x => !x.RemovedFromDirectory)
                    || device.Components.Any(x => !x.RemovedFromDirectory
                        && !IsCurrentPercent(x, device.Binding, mode, time, refreshPeriod)))), mode);
    }
    public static BatteryPresentation Present(BatteryDeviceState device, BatteryMode mode, TimeProvider time,
        TimeSpan refreshPeriod)
    {
        ValidatePeriod(refreshPeriod);
        ReadingSource expected = mode == BatteryMode.Mock ? ReadingSource.Mock : ReadingSource.GattUncached;
        ComponentState[] visible = device.Components.Where(x => !x.RemovedFromDirectory && x.Source.Source == expected)
            .OrderBy(x => x.DisplayOrder).ToArray();
        ComponentState[] current = visible.Where(x => IsCurrentValue(x, device.Binding, mode, time, refreshPeriod)).ToArray();
        int[] percents = current.Where(x => x.Source.LastSuccess!.Value.Percent.HasValue)
            .Select(x => x.Source.LastSuccess!.Value.Percent!.Value).ToArray();
        SourceState[] reports = mode == BatteryMode.Real ? GetSystemReports(device).ToArray() : [];
        int[] systemValues = reports.Select(x => x.LastSuccess!.Value.Percent!.Value).Distinct().Order().ToArray();
        bool partial = current.Length > 0 && current.Length < visible.Length;
        ConnectionState connection = GetConnection(device.Binding, mode);
        ReadOutcome? failure = visible.Select(x => x.Source.LatestOutcome)
            .Concat(mode == BatteryMode.Real ? device.SystemCandidates.Where(x => x.Source == ReadingSource.WindowsProperty)
                .Select(x => x.LatestOutcome) : []).Where(x => x is ReadOutcome.TimedOut
            or ReadOutcome.AccessDenied or ReadOutcome.Error or ReadOutcome.Unsupported or ReadOutcome.Offline)
            .Select(x => (ReadOutcome?)x).FirstOrDefault();
        string text = connection == ConnectionState.Disconnected ? "离线"
            : current.Length > 0 ? string.Join(" / ", current.Select(x => x.Source.LastSuccess!.Value.Text))
                + (partial ? " · 部分读取" : "")
            : failure is not null ? OutcomeText(failure.Value)
            : systemValues.Length > 1 ? $"系统报告冲突：{string.Join(" / ", systemValues.Select(x => $"{x}%"))}（时效未知）"
            : systemValues.Length == 1 ? $"系统报告 {systemValues[0]}% · 时效未知"
            : connection == ConnectionState.Unknown ? "连接状态未知 · 电量未知" : "电量未知";
        DateTimeOffset? directTime = mode == BatteryMode.Real
            ? device.Components.Where(x => x.Source.Source == expected).Select(x => x.Source.LastSuccessfulReadUtc).Max() : null;
        string[] history = device.Components.Where(x => x.Source.Source == expected && x.Source.LastSuccess is not null
            && x.Source.LastSuccess.Source == expected)
            .Select(x => $"电池服务 {x.DisplayOrder + 1} · "
                + (x.Source.Source == ReadingSource.Mock ? "上次模拟读取" : "上次设备读取")
                + $" {x.Source.LastSuccess!.Value.Text}").ToArray();
        int? displayPercent = null;
        if (connection != ConnectionState.Disconnected)
        {
            if (percents.Length > 0) displayPercent = percents.Min();
            else if (current.Length == 0 && failure is null && systemValues.Length > 0) displayPercent = systemValues[0];
        }
        return new(text, connection, partial, percents, systemValues, directTime, history, displayPercent);
    }
    public static string OutcomeText(ReadOutcome outcome) => outcome switch
    {
        ReadOutcome.Offline => "离线", ReadOutcome.Unsupported => "标准 BLE 电量不支持",
        ReadOutcome.TimedOut => "读取超时", ReadOutcome.AccessDenied => "无访问权限",
        ReadOutcome.Error => "读取失败", _ => "电量未知"
    };
    private static void ValidatePeriod(TimeSpan period)
    {
        if (period < TimeSpan.FromMinutes(1) || period > TimeSpan.FromMinutes(30)
            || period.Ticks % TimeSpan.TicksPerMinute != 0)
            throw new ArgumentOutOfRangeException(nameof(period), "Refresh period must be an integer from 1 to 30 minutes.");
    }
}

public sealed class BatteryPresentation
{
    public string BatteryText { get; }
    public ConnectionState Connection { get; }
    public bool IsPartial { get; }
    public IReadOnlyList<int> CurrentPercents { get; }
    public IReadOnlyList<int> SystemPercents { get; }
    /// <summary>Display-only minimum; may be a system report of unknown age, never proof of a current reading.</summary>
    public int? DisplayPercent { get; }
    public DateTimeOffset? LastSuccessfulReadUtc { get; }
    public IReadOnlyList<string> History { get; }
    internal BatteryPresentation(string text, ConnectionState connection, bool partial, int[] current,
        int[] system, DateTimeOffset? directTime, string[] history, int? displayPercent)
    {
        BatteryText = text;
        Connection = connection;
        IsPartial = partial;
        CurrentPercents = Array.AsReadOnly(current);
        SystemPercents = Array.AsReadOnly(system);
        DisplayPercent = displayPercent;
        LastSuccessfulReadUtc = directTime;
        History = Array.AsReadOnly(history);
    }
}
