namespace PeripheralBatteryTray.Core;

/// <summary>Serial state commits; all provider, timer and subscriber calls run outside the state lock.</summary>
public sealed class BatteryCoordinator : IAsyncDisposable
{
    private const string DiscoveryPath = "$directory";
    private readonly object _gate = new(), _timerGate = new();
    private readonly TimeProvider _time;
    private readonly BatteryMode _mode;
    private readonly Dictionary<ReadingSource, IBatteryProvider> _providers;
    private readonly Dictionary<string, BatteryDeviceState> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, ReadingSource), Route> _routes = [];
    private readonly Dictionary<ReadingSource, Ticket> _lanes = [];
    private readonly CancellationTokenSource _lifetime = new();
    private ITimer? _timer;
    private long _timerVersion, _snapshotVersion, _order;
    private long? _lastGattStart;
    private bool _stopping, _sleeping;
    private TaskCompletionSource<ShutdownResult>? _stop;
    private int _cleanupErrors;
    private TimeSpan _period;
    public DiagnosticTelemetry Telemetry { get; }
    public DiagnosticReport CreateDiagnosticReport(string buildId, string capability, ShutdownResult? shutdown = null)
    {
        lock (_gate) return DiagnosticExporter.Create(MakeSnapshot(false), _time, _period, Telemetry.Capture(), buildId,
            capability, _lanes.Values.Count(t => t.Phase == QueryPhase.Draining), shutdown);
    }
    public event Action<BatterySnapshot>? SnapshotChanged;
    public BatterySnapshot Snapshot { get { lock (_gate) return MakeSnapshot(false); } }
    public TimeSpan RefreshPeriod { get { lock (_gate) return _period; } }
    public long RejectedUpdates { get; private set; }
    public IReadOnlyList<ActiveRead> ActiveReads { get { lock (_gate) return _lanes.Values
        .Select(t => new ActiveRead(t.Request.Stamp, t.Phase)).ToArray(); } }

    private sealed class Route(string deviceId, ReadingSource source, long order)
    {
        public string DeviceId = deviceId;
        public ReadingSource Source = source;
        public long Order = order, RequestId;
        public long? PendingAt;
        public RefreshReason Reasons;
        public bool Enabled = true;
        public readonly Dictionary<string, RefreshPolicy> Paths = new(StringComparer.Ordinal);
    }
    private sealed class Ticket(Route route, QueryRequest request)
    {
        public readonly Route Route = route;
        public readonly QueryRequest Request = request;
        public readonly TaskCompletionSource Terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IBatteryReadOperation? Operation;
        public QueryPhase Phase;
        public long Sequence;
        public readonly HashSet<string> FinishedComponents = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ReadOutcome> PathResults = new(StringComparer.Ordinal);
        public readonly HashSet<string> ConfirmedComponents = new(StringComparer.Ordinal);
        public readonly HashSet<string> Services = new(StringComparer.Ordinal);
        public bool SawDirectory;
    }
    public BatteryCoordinator(BatteryMode mode, IEnumerable<IBatteryProvider> providers,
        TimeProvider? time = null, int refreshMinutes = 5, DiagnosticTelemetry? telemetry = null)
    {
        if (refreshMinutes is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(refreshMinutes));
        _time = time ?? TimeProvider.System; _mode = mode; _period = TimeSpan.FromMinutes(refreshMinutes);
        Telemetry = telemetry ?? new(_time);
        _providers = providers.ToDictionary(x => x.Source);
        if (_providers.Keys.Any(x => mode == BatteryMode.Mock ? x != ReadingSource.Mock : x == ReadingSource.Mock))
            throw new ArgumentException("Provider does not match mode.");
    }

    public void UpsertDevice(BatteryDeviceState device, bool gattCandidate = false)
        => Apply(effects =>
        {
            bool existed = _devices.TryGetValue(device.Binding.DeviceId, out var previous);
            // Preserve acquired readings on metadata-only updates; identity epochs determine eligibility.
            if (previous is not null) device = new(device.Binding with {
                GattDatabaseEpoch = device.Binding.BindingEpoch == previous.Binding.BindingEpoch
                    ? Math.Max(device.Binding.GattDatabaseEpoch, previous.Binding.GattDatabaseEpoch) : device.Binding.GattDatabaseEpoch }, device.DisplayName, device.ConnectionType,
                previous.SystemCandidates, previous.Components);
            _devices[device.Binding.DeviceId] = device;
            foreach (ReadingSource source in _providers.Keys)
            {
                var key = (device.Binding.DeviceId, source);
                if (source == ReadingSource.GattUncached && !gattCandidate && !_routes.ContainsKey(key)) continue;
                if (!_routes.TryGetValue(key, out var route)) _routes[key] = route = new(key.Item1, source, ++_order);
                bool wasEnabled = route.Enabled;
                route.Enabled = source != ReadingSource.GattUncached || gattCandidate;
                bool changed = previous?.Binding != device.Binding;
                if (changed)
                {
                    bool newContext = previous is not null && (previous.Binding.BindingEpoch != device.Binding.BindingEpoch
                        || previous.Binding.Ble?.Epoch != device.Binding.Ble?.Epoch
                        || previous.Binding.GattDatabaseEpoch != device.Binding.GattDatabaseEpoch);
                    if (newContext) route.Paths.Clear();
                    if (_lanes.TryGetValue(source, out var ticket) && ticket.Route == route
                        && !BatteryRules.MatchesBinding(ticket.Request.Stamp, device.Binding)) Drain(ticket, ReadOutcome.Cancelled, effects);
                    if (!Eligible(route)) route.PendingAt = null;
                }
                if (!Eligible(route))
                {
                    route.PendingAt = null;
                    if (_lanes.TryGetValue(source, out var unavailable) && unavailable.Route == route) Drain(unavailable, ReadOutcome.Cancelled, effects);
                }
                if ((!existed || changed || !wasEnabled) && Eligible(route)) Queue(route, existed ? RefreshReason.Connection : RefreshReason.Discovery, Now);
            }
        });
    public void RemoveDevice(string deviceId) => Apply(effects =>
    {
        _devices.Remove(deviceId);
        foreach (var route in _routes.Values.Where(r => r.DeviceId == deviceId).ToArray())
        {
            route.PendingAt = null; _routes.Remove((route.DeviceId, route.Source));
            if (_lanes.TryGetValue(route.Source, out var ticket) && ticket.Route == route) Drain(ticket, ReadOutcome.Cancelled, effects);
        }
    });
    public void Refresh(string? deviceId = null, RefreshReason reason = RefreshReason.Manual)
        => Apply(_ =>
        {
            foreach (var route in _routes.Values.Where(r => deviceId is null || r.DeviceId == deviceId))
            {
                if (reason == RefreshReason.Property && route.Source != ReadingSource.WindowsProperty) continue;
                if (reason == RefreshReason.ListOpened && route.Source == ReadingSource.GattUncached
                    && _devices[route.DeviceId].Components.Any(c => BatteryRules.IsCurrentValue(c,
                        _devices[route.DeviceId].Binding, _mode, _time, _period))) continue;
                if (reason == RefreshReason.Periodic && _mode == BatteryMode.Mock) continue;
                if (_lanes.TryGetValue(route.Source, out var ticket) && ticket.Route == route && ticket.Phase == QueryPhase.Running
                    && BatteryRules.MatchesBinding(ticket.Request.Stamp, _devices[route.DeviceId].Binding)) continue;
                long due = reason == RefreshReason.Property ? Add(TimeSpan.FromSeconds(1)) : Now;
                Queue(route, reason, due);
            }
        });
    public void SetRefreshMinutes(int minutes)
    {
        if (minutes is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(minutes));
        Apply(_ => { _period = TimeSpan.FromMinutes(minutes); });
    }
    public void Suspend() => Apply(effects =>
    {
        _sleeping = true;
        foreach (string key in _devices.Keys.ToArray())
        {
            BatteryDeviceState device = _devices[key];
            _devices[key] = device with { Binding = device.Binding with { SleepInvalidated = true,
                Ble = device.Binding.Ble is { } ble ? new(ConnectionState.Unknown, checked(ble.Epoch + 1)) : null,
                Classic = device.Binding.Classic is { } classic ? new(ConnectionState.Unknown, checked(classic.Epoch + 1)) : null } };
        }
        foreach (var route in _routes.Values) route.PendingAt = null;
        foreach (var ticket in _lanes.Values) Drain(ticket, ReadOutcome.Cancelled, effects);
    });
    // Discovery must supply new connection evidence after resume; old epochs are never restored here.
    public void Resume() => Apply(_ => _sleeping = false);
    public void RepublishSnapshot() => Apply(_ => { });

    private long Now => _time.GetTimestamp();
    private long Add(TimeSpan delay) => RefreshPolicy.Add(Now, delay, _time);
    private void Queue(Route route, RefreshReason reason, long due)
    {
        route.PendingAt = route.PendingAt.HasValue ? Math.Min(route.PendingAt.Value, due) : due;
        route.Reasons |= reason;
    }
    private bool Eligible(Route route)
    {
        if (!route.Enabled || _sleeping || !_devices.TryGetValue(route.DeviceId, out var device) || device.Binding.SleepInvalidated) return false;
        return route.Source != ReadingSource.GattUncached || device.Binding.Ble?.Connection == ConnectionState.Connected;
    }
    private string[] DuePaths(Route route, bool manual)
    {
        if (route.Paths.Count == 0) return [DiscoveryPath];
        return route.Paths.Where(p => manual || (!p.Value.Paused && (!p.Value.NextTimestamp.HasValue || p.Value.NextTimestamp <= Now)))
            .Select(p => p.Key).ToArray();
    }
    private long Earliest(Route route)
    {
        long due = route.PendingAt ?? long.MaxValue;
        if (route.Source == ReadingSource.GattUncached && _lastGattStart.HasValue)
            due = Math.Max(due, RefreshPolicy.Add(_lastGattStart.Value, TimeSpan.FromSeconds(10), _time));
        bool manual = BypassesRetry(route);
        if (!manual && route.Paths.Count > 0)
        {
            long retry = route.Paths.Values.Where(p => !p.Paused).Select(p => p.NextTimestamp ?? Now).DefaultIfEmpty(long.MaxValue).Min();
            due = Math.Max(due, retry);
        }
        return due;
    }
    private static bool BypassesRetry(Route route) => route.Reasons.HasFlag(RefreshReason.Manual)
        || (route.Source == ReadingSource.WindowsProperty && route.Reasons.HasFlag(RefreshReason.Property));
    private void Apply(Action<List<Action>> change)
    {
        var effects = new List<Action>();
        BatterySnapshot? snapshot;
        long version; long? next;
        lock (_gate)
        {
            if (_stopping) return;
            Expire(effects); change(effects); Pump(effects);
            snapshot = MakeSnapshot(true); version = ++_timerVersion; next = NextTimer();
        }
        foreach (Action effect in effects) effect();
        ScheduleTimer(version, next);
        // UI uses a mailbox and Version to reject out-of-order dispatch; subscriber failures cannot break I/O cleanup.
        lock (_gate) { if (_stopping) return; }
        try { SnapshotChanged?.Invoke(snapshot); } catch (Exception) { /* Host callback isolation. */ }
    }
    private BatterySnapshot MakeSnapshot(bool increment)
        => new(increment ? ++_snapshotVersion : _snapshotVersion, _time.GetUtcNow(), _mode, _devices.Values, _time, _period);
    private void Pump(List<Action> effects)
    {
        if (_stopping) return;
        foreach (ReadingSource source in _providers.Keys)
        {
            if (_lanes.ContainsKey(source)) continue;
            Route? route = _routes.Values.Where(r => r.Source == source && r.PendingAt.HasValue && Eligible(r) && Earliest(r) <= Now)
                .OrderBy(r => r.PendingAt).ThenBy(r => r.Order).FirstOrDefault();
            if (route is null) continue;
            string[] paths = DuePaths(route, BypassesRetry(route));
            if (paths.Length == 0) continue;
            BatteryDeviceState device = _devices[route.DeviceId];
            TransportKind transport = source == ReadingSource.Mock ? TransportKind.Mock
                : source == ReadingSource.WindowsProperty && device.Binding.Ble is null ? TransportKind.Classic : TransportKind.Ble;
            var request = new QueryRequest(device.Binding.Stamp(source, ++route.RequestId, transport), device.Binding.BindingKey,
                route.Reasons, Now, TimeSpan.FromSeconds(source == ReadingSource.WindowsProperty ? 2 : 10), paths);
            var ticket = new Ticket(route, request);
            _lanes[source] = ticket; route.PendingAt = null; route.Reasons = RefreshReason.None;
            Telemetry.Record(source, DiagnosticStep.QueryStarted);
            if (source == ReadingSource.GattUncached) _lastGattStart = Now;
            BeginSources(ticket);
            effects.Add(() => CreateAndStart(ticket));
        }
    }
    private void BeginSources(Ticket ticket)
    {
        BatteryDeviceState device = _devices[ticket.Route.DeviceId];
        if (ticket.Route.Source == ReadingSource.WindowsProperty)
            _devices[ticket.Route.DeviceId] = device.WithSystemCandidates(device.SystemCandidates.Select(s =>
                BatteryRules.BeginRequest(s, device.Binding, ticket.Request.Stamp, _time.GetUtcNow())));
        else _devices[ticket.Route.DeviceId] = device.WithComponents(device.Components.Select(c =>
            ticket.Request.DueServiceKeys.Contains(DiscoveryPath) || ticket.Request.DueServiceKeys.Contains(c.ServiceKey)
                ? c with { Source = BatteryRules.BeginRequest(c.Source, device.Binding, ticket.Request.Stamp, _time.GetUtcNow()) } : c));
    }
    private void CreateAndStart(Ticket ticket)
    {
        IBatteryReadOperation operation;
        try { operation = _providers[ticket.Route.Source].CreateRead(ticket.Request, _lifetime.Token); }
        catch (Exception) { Complete(ticket, new(ReadOutcome.Error)); return; }
        bool cancelled;
        lock (_gate) { ticket.Operation = operation; cancelled = _stopping || ticket.Phase == QueryPhase.Draining; }
        _ = Observe(ticket, operation); // Must be installed before Start; supports synchronous providers.
        if (cancelled) Cancel(operation);
        try { operation.Start(update => Receive(ticket, update)); }
        catch (Exception) { Complete(ticket, new(ReadOutcome.Error)); } // Contract: no native work on synchronous throw.
    }
    private async Task Observe(Ticket ticket, IBatteryReadOperation operation)
    {
        ReadCompletion completion;
        try { completion = await operation.Completion.ConfigureAwait(false); }
        catch (OperationCanceledException) { completion = new(ReadOutcome.Cancelled); }
        catch (Exception) { completion = new(ReadOutcome.Error); }
        Complete(ticket, completion);
    }
    private void Receive(Ticket ticket, ProviderUpdate update) => Apply(effects =>
    {
        if (!_lanes.TryGetValue(ticket.Route.Source, out var active) || active != ticket || ticket.Phase != QueryPhase.Running
            || !_devices.TryGetValue(ticket.Route.DeviceId, out var device)
            || !BatteryRules.MatchesBinding(ticket.Request.Stamp, device.Binding) || update.Sequence <= ticket.Sequence)
        { RejectedUpdates++; return; }
        ticket.Sequence = update.Sequence;
        if (update is DirectoryChangedUpdate changed)
        {
            _devices[ticket.Route.DeviceId] = BatteryRules.InvalidateDatabase(device);
            Drain(ticket, ReadOutcome.Cancelled, effects);
            ticket.Route.Paths.Clear();
            if (!changed.ConfirmedMissing) Queue(ticket.Route, RefreshReason.Database, Now);
            else { var missing = new RefreshPolicy(); missing.Complete(ReadOutcome.Unsupported, Now, _period, _time); ticket.Route.Paths[DiscoveryPath] = missing; }
        }
        else if (update is DirectoryUpdate directory && ticket.Route.Source != ReadingSource.WindowsProperty)
        {
            if (!Enum.IsDefined(directory.Scope) || directory.Members.Count > 8
                || directory.Members.Any(m => string.IsNullOrEmpty(m.ServiceKey) || string.IsNullOrEmpty(m.ComponentKey))
                || directory.Members.Select(m => m.ComponentKey).Distinct().Count() != directory.Members.Count
                || (directory.Scope == DirectoryScope.ServiceCharacteristics && (string.IsNullOrEmpty(directory.ParentKey)
                    || directory.Members.Any(m => m.ServiceKey != directory.ParentKey)))) { RejectedUpdates++; return; }
            ticket.SawDirectory = true;
            var components = device.Components.ToList();
            if (directory.Scope == DirectoryScope.GattServices)
                foreach (var member in directory.Members)
                {
                    ticket.Services.Add(member.ServiceKey);
                    if (components.All(c => c.ServiceKey != member.ServiceKey || c.RemovedFromDirectory))
                        components.Add(new(member.ComponentKey, member.ServiceKey, member.DisplayOrder,
                            BatteryRules.BeginRequest(new SourceState { Source = ticket.Route.Source, CandidateKey = member.ComponentKey,
                                ComponentKey = member.ComponentKey }, device.Binding, ticket.Request.Stamp, _time.GetUtcNow())));
                }
            if (directory.Scope == DirectoryScope.ServiceCharacteristics)
                foreach (var member in directory.Members)
                {
                    ticket.Services.Add(member.ServiceKey);
                    ticket.ConfirmedComponents.Add(member.ComponentKey);
                    if (components.All(c => c.ComponentKey != member.ComponentKey)) components.Add(new(member.ComponentKey,
                        member.ServiceKey, member.DisplayOrder, BatteryRules.BeginRequest(new SourceState {
                            Source = ticket.Route.Source, CandidateKey = member.ComponentKey, ComponentKey = member.ComponentKey },
                            device.Binding, ticket.Request.Stamp, _time.GetUtcNow())));
                }
            _devices[ticket.Route.DeviceId] = device.WithComponents(BatteryRules.ReconcileDirectory(components,
                directory.Scope, directory.ParentKey, directory.Members.Select(m => directory.Scope == DirectoryScope.GattServices
                    ? m.ServiceKey : m.ComponentKey), directory.Complete));
            // Keep a bounded amount of retired history across database generations.
            var reconciled = _devices[ticket.Route.DeviceId];
            _devices[ticket.Route.DeviceId] = reconciled.WithComponents(reconciled.Components.Where(c => !c.RemovedFromDirectory)
                .Concat(reconciled.Components.Where(c => c.RemovedFromDirectory).TakeLast(8)));
            if (directory.Complete && directory.Scope == DirectoryScope.GattServices)
            {
                var services = directory.Members.Select(m => m.ServiceKey).ToHashSet(StringComparer.Ordinal);
                foreach (string path in ticket.Route.Paths.Keys.Where(p => p != DiscoveryPath && !services.Contains(p)).ToArray())
                    ticket.Route.Paths.Remove(path);
            }
        }
        else if (update is ServiceFailureUpdate service && ticket.Route.Source == ReadingSource.GattUncached)
        {
            if (!Enum.IsDefined(service.Outcome) || service.Outcome is ReadOutcome.Success or ReadOutcome.Unknown || string.IsNullOrEmpty(service.ServiceKey)
                || (service.ServiceKey != DiscoveryPath && !ticket.Services.Contains(service.ServiceKey)))
            { RejectedUpdates++; return; }
            var components = device.Components.ToList();
            if (!components.Any(c => c.ServiceKey == service.ServiceKey && !c.RemovedFromDirectory))
                components.Add(new(service.ServiceKey + "/pending", service.ServiceKey, components.Count,
                    new SourceState { Source = ticket.Route.Source, CandidateKey = service.ServiceKey + "/pending", ComponentKey = service.ServiceKey + "/pending" }));
            _devices[ticket.Route.DeviceId] = device.WithComponents(components.Select(c =>
            {
                if (c.ServiceKey != service.ServiceKey || c.RemovedFromDirectory) return c;
                var state = BatteryRules.BeginRequest(c.Source, device.Binding, ticket.Request.Stamp, _time.GetUtcNow());
                state = BatteryRules.ApplyResult(state, device.Binding, ticket.Request.Stamp, update.Sequence,
                    service.Outcome, null, _time.GetUtcNow(), Now);
                ticket.FinishedComponents.Add(c.ComponentKey);
                return c with { Source = state, InCurrentGattDirectory = false };
            }));
            RecordPath(ticket, service.ServiceKey, service.Outcome);
        }
        else if (update is ValueUpdate value)
        {
            if (!Enum.IsDefined(value.Outcome) || value.CapturedTimestamp > Now || string.IsNullOrEmpty(value.CandidateKey))
            { RejectedUpdates++; return; }
            if (ticket.Route.Source == ReadingSource.WindowsProperty)
            {
                SourceState state = device.SystemCandidates.FirstOrDefault(c => c.CandidateKey == value.CandidateKey)
                    ?? new SourceState { Source = ReadingSource.WindowsProperty, CandidateKey = value.CandidateKey };
                state = BatteryRules.BeginRequest(state, device.Binding, ticket.Request.Stamp, _time.GetUtcNow());
                state = BatteryRules.ApplyResult(state, device.Binding, ticket.Request.Stamp, update.Sequence,
                    value.Outcome, value.Value, value.ObservedAtUtc, value.CapturedTimestamp);
                _devices[ticket.Route.DeviceId] = device.WithSystemCandidates(device.SystemCandidates
                    .Where(c => c.CandidateKey != value.CandidateKey).Append(state));
                ticket.FinishedComponents.Add(value.CandidateKey);
                RecordPath(ticket, DiscoveryPath, state.LatestOutcome);
                if (state.LatestOutcome == ReadOutcome.Success) Telemetry.Record(ticket.Route.Source, DiagnosticStep.ValueAccepted);
            }
            else
            {
                ComponentState? component = device.Components.FirstOrDefault(c => c.ComponentKey == value.CandidateKey);
                if (component is null || !component.InCurrentGattDirectory || component.ServiceKey != value.ServiceKey
                    || !ticket.ConfirmedComponents.Contains(component.ComponentKey)
                    || (!ticket.Request.DueServiceKeys.Contains(DiscoveryPath) && !ticket.Request.DueServiceKeys.Contains(component.ServiceKey)))
                { RejectedUpdates++; return; }
                var state = BatteryRules.ApplyResult(component.Source, device.Binding, ticket.Request.Stamp, update.Sequence,
                    value.Outcome, value.Value, value.ObservedAtUtc, value.CapturedTimestamp);
                _devices[ticket.Route.DeviceId] = device.WithComponents(device.Components.Select(c => c == component ? c with { Source = state } : c));
                ticket.FinishedComponents.Add(component.ComponentKey); RecordPath(ticket, component.ServiceKey, state.LatestOutcome);
                if (state.LatestOutcome == ReadOutcome.Success) Telemetry.Record(ticket.Route.Source, DiagnosticStep.ValueAccepted);
            }
        }
    });
    private static void RecordPath(Ticket ticket, string path, ReadOutcome outcome)
    {
        // A path failure wins within the round; exactly one backoff update is applied at terminal/timeout.
        if (!ticket.PathResults.TryGetValue(path, out var old) || old == ReadOutcome.Success) ticket.PathResults[path] = outcome;
    }
    private void Expire(List<Action> effects)
    {
        foreach (var ticket in _lanes.Values)
            if (ticket.Phase == QueryPhase.Running && _time.GetElapsedTime(ticket.Request.StartedTimestamp, Now) >= ticket.Request.Budget)
                Drain(ticket, ReadOutcome.TimedOut, effects);
    }
    private void Drain(Ticket ticket, ReadOutcome outcome, List<Action> effects)
    {
        if (ticket.Phase == QueryPhase.Draining) return;
        ticket.Phase = QueryPhase.Draining;
        Telemetry.Record(ticket.Route.Source, outcome == ReadOutcome.TimedOut ? DiagnosticStep.LogicalTimeout : DiagnosticStep.CancelRequested, outcome);
        FinishSources(ticket, outcome); FinishPaths(ticket, outcome);
        if (ticket.Operation is { } operation) effects.Add(() => Cancel(operation));
    }
    private void FinishSources(Ticket ticket, ReadOutcome outcome)
    {
        if (!_devices.TryGetValue(ticket.Route.DeviceId, out var device)) return;
        SourceState Finish(SourceState s)
        {
            if (s.LatestRequestStamp != ticket.Request.Stamp || ticket.FinishedComponents.Contains(s.ComponentKey ?? s.CandidateKey)) return s;
            return BatteryRules.ApplyResult(s, device.Binding, ticket.Request.Stamp, ++ticket.Sequence,
                outcome == ReadOutcome.Success ? ReadOutcome.Error : outcome, null, _time.GetUtcNow(), Now) with { IsRefreshing = false };
        }
        if (ticket.Route.Source == ReadingSource.WindowsProperty) _devices[ticket.Route.DeviceId] = device.WithSystemCandidates(device.SystemCandidates.Select(Finish));
        else _devices[ticket.Route.DeviceId] = device.WithComponents(device.Components.Select(c => c with { Source = Finish(c.Source),
            InCurrentGattDirectory = c.InCurrentGattDirectory && (c.Source.LatestRequestStamp != ticket.Request.Stamp
                || ticket.ConfirmedComponents.Contains(c.ComponentKey)) }));
    }
    private void FinishPaths(Ticket ticket, ReadOutcome outcome)
    {
        Route route = ticket.Route;
        if (outcome == ReadOutcome.Cancelled) return;
        foreach (var component in _devices.GetValueOrDefault(route.DeviceId)?.Components ?? [])
            if (!component.RemovedFromDirectory && component.Source.LatestRequestStamp == ticket.Request.Stamp && !ticket.FinishedComponents.Contains(component.ComponentKey))
                RecordPath(ticket, component.ServiceKey, outcome == ReadOutcome.Success ? ReadOutcome.Error : outcome);
        if (ticket.PathResults.Count == 0) RecordPath(ticket, DiscoveryPath, outcome == ReadOutcome.Success ? ReadOutcome.Unsupported : outcome);
        else if (ticket.PathResults.Keys.Any(p => p != DiscoveryPath)) route.Paths.Remove(DiscoveryPath);
        foreach (var result in ticket.PathResults)
        {
            if (!route.Paths.TryGetValue(result.Key, out var policy)) route.Paths[result.Key] = policy = new();
            policy.Complete(result.Value, Now, _period, _time);
        }
    }
    private void Complete(Ticket ticket, ReadCompletion completion)
    {
        var effects = new List<Action>(); BatterySnapshot? snapshot = null; long version = 0; long? next = null;
        lock (_gate)
        {
            if (!_lanes.TryGetValue(ticket.Route.Source, out var active) || active != ticket) return;
            _cleanupErrors += Math.Max(0, completion.CleanupErrors);
            Telemetry.Record(ticket.Route.Source, DiagnosticStep.OperationTerminal, completion.Outcome, completion.CleanupErrors);
            if (!_stopping)
            {
                Expire(effects);
                if (ticket.Phase == QueryPhase.Running) { FinishSources(ticket, completion.Outcome); FinishPaths(ticket, completion.Outcome); }
            }
            _lanes.Remove(ticket.Route.Source); ticket.Terminal.TrySetResult();
            if (!_stopping)
            {
                if (_mode != BatteryMode.Mock && _routes.ContainsValue(ticket.Route) && ticket.Route.PendingAt is null)
                {
                    long? due = ticket.Route.Paths.Values.Where(p => !p.Paused).Select(p => p.NextTimestamp).Min();
                    if (due.HasValue) Queue(ticket.Route, RefreshReason.Periodic, due.Value);
                }
                Pump(effects); snapshot = MakeSnapshot(true); version = ++_timerVersion; next = NextTimer();
            }
        }
        foreach (var effect in effects) effect();
        if (snapshot is not null)
        {
            ScheduleTimer(version, next);
            lock (_gate) { if (_stopping) return; }
            try { SnapshotChanged?.Invoke(snapshot); } catch (Exception) { }
        }
    }
    private void Cancel(IBatteryReadOperation operation)
    {
        try { operation.RequestCancel(); } catch (Exception) { lock (_gate) _cleanupErrors++; }
    }
    private long? NextTimer()
    {
        if (_mode == BatteryMode.Mock) return null;
        var deadlines = new List<long>();
        foreach (var ticket in _lanes.Values.Where(t => t.Phase == QueryPhase.Running))
            deadlines.Add(RefreshPolicy.Add(ticket.Request.StartedTimestamp, ticket.Request.Budget, _time));
        foreach (var route in _routes.Values.Where(r => r.PendingAt.HasValue && !_lanes.ContainsKey(r.Source) && Eligible(r)))
        { long at = Earliest(route); if (at != long.MaxValue) deadlines.Add(at); }
        foreach (var device in _devices.Values)
            foreach (var component in device.Components.Where(c => BatteryRules.IsCurrentValue(c, device.Binding, _mode, _time, _period)))
                deadlines.Add(RefreshPolicy.Add(component.Source.LastSuccess!.CapturedTimestamp, _period + _period, _time));
        return deadlines.Count == 0 ? null : deadlines.Min();
    }
    private void ScheduleTimer(long version, long? deadline)
    {
        lock (_timerGate)
        {
            lock (_gate) { if (version != _timerVersion) return; }
            _timer?.Dispose(); _timer = null;
            if (!deadline.HasValue) return;
            TimeSpan delay = _time.GetElapsedTime(Now, Math.Max(Now, deadline.Value));
            _timer = _time.CreateTimer(_ =>
            {
                lock (_gate) { if (_stopping || version != _timerVersion) return; }
                Apply(_ => { });
            }, null, delay, Timeout.InfiniteTimeSpan);
        }
    }
    public Task<ShutdownResult> StopAsync()
    {
        Task[] pending; IBatteryReadOperation[] operations; long version;
        lock (_gate)
        {
            if (_stop is not null) return _stop.Task;
            _stop = new(TaskCreationOptions.RunContinuationsAsynchronously); _stopping = true; version = ++_timerVersion;
            SnapshotChanged = null;
            pending = _lanes.Values.Select(t => t.Terminal.Task).ToArray();
            operations = _lanes.Values.Select(t => t.Operation).OfType<IBatteryReadOperation>().ToArray();
        }
        ScheduleTimer(version, null);
        // Disposal may await original native work. Its task remains observed after the overall budget expires.
        _ = FinishStop(pending, operations);
        return _stop.Task;
    }
    private async Task FinishStop(Task[] pending, IBatteryReadOperation[] operations)
    {
        using var budgetCancellation = new CancellationTokenSource();
        Task budget = Task.Delay(TimeSpan.FromSeconds(2), _time, budgetCancellation.Token);
        try { _lifetime.Cancel(); } catch (Exception) { lock (_gate) _cleanupErrors++; }
        foreach (var operation in operations) Cancel(operation);
        Task[] disposals = _providers.Values.Select(provider => Task.Run(async () =>
        {
            try { await provider.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { lock (_gate) _cleanupErrors++; }
        })).ToArray();
        Task all = Task.WhenAll(pending.Concat(disposals));
        await Task.WhenAny(all, budget).ConfigureAwait(false);
        budgetCancellation.Cancel();
        lock (_gate)
        {
            int unresolved = pending.Count(t => !t.IsCompleted) + disposals.Count(t => !t.IsCompleted);
            _stop!.TrySetResult(new(unresolved == 0, unresolved, _cleanupErrors));
        }
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
