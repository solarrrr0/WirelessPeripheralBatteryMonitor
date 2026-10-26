using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.Windows;

// Deliberately distinct types: AEP containers cannot be compared as PnP containers.
public readonly record struct AepId(string Value);
public readonly record struct AepContainerId(Guid Value);
public readonly record struct DevnodeId(string Value);
public readonly record struct PnpContainerId(Guid Value);
public sealed record Endpoint(AepId Id, TransportKind Transport, string Name, bool Paired,
    ConnectionState Connection, AepContainerId? Container, byte? Battery = null, bool Present = true);
public sealed record PnpNode(DevnodeId Id, AepId? Aep, PnpContainerId? Container,
    DevnodeId? Parent, bool? InLocalMachineContainer, Guid? Service = null);
public sealed class ResolvedBinding
{
    public BatteryDeviceState Device { get; }
    public IReadOnlyList<Endpoint> Endpoints { get; }
    public IReadOnlyList<PnpNode> Nodes { get; }
    public bool GattCandidate { get; }
    public ResolvedBinding(BatteryDeviceState device, IEnumerable<Endpoint> endpoints, IEnumerable<PnpNode> nodes)
    {
        Device = device; Endpoints = Array.AsReadOnly(endpoints.ToArray()); Nodes = Array.AsReadOnly(nodes.ToArray());
        GattCandidate = Nodes.Any(n => n.Service == new Guid("0000180f-0000-1000-8000-00805f9b34fb"))
            || Endpoints.Any(e => e.Transport == TransportKind.Ble && e.Battery is <= 100);
    }
}

/// <summary>Resolves only typed system evidence. No path parsing, name grouping or transitive union.</summary>
public sealed class DeviceIdentityResolver
{
    private readonly Dictionary<string, (string Key, long Epoch, TransportState? Classic, TransportState? Ble)> _history = [];
    private readonly Dictionary<string, string> _opaqueIds = new(StringComparer.Ordinal);
    private long _next;
    public void InvalidateConnections()
    {
        foreach (string id in _history.Keys.ToArray())
        {
            var old = _history[id];
            _history[id] = (old.Key, old.Epoch,
                old.Classic is { } c ? new(ConnectionState.Unknown, checked(c.Epoch + 1)) : null,
                old.Ble is { } b ? new(ConnectionState.Unknown, checked(b.Epoch + 1)) : null);
        }
    }
    public IReadOnlyList<ResolvedBinding> Resolve(IEnumerable<Endpoint> endpoints, IEnumerable<PnpNode> nodes)
    {
        Endpoint[] eps = endpoints.Where(e => e.Paired).DistinctBy(e => e.Id).ToArray();
        PnpNode[] pnp = nodes.DistinctBy(n => n.Id).ToArray();
        var evidence = eps.ToDictionary(e => e.Id, e => pnp.Where(n => n.Aep == e.Id
            && n.InLocalMachineContainer == false && Valid(n.Container)).ToArray());
        var groupKeys = new Dictionary<AepId, string>();
        foreach (Endpoint ep in eps)
        {
            PnpContainerId[] remote = evidence[ep.Id].Select(n => n.Container!.Value).Distinct().ToArray();
            bool aepValid = ep.Container is { Value: var guid } && guid != Guid.Empty;
            Endpoint[] aepPeers = aepValid ? eps.Where(e => e.Container == ep.Container).ToArray() : [ep];
            PnpContainerId[] peerRemote = aepPeers.SelectMany(e => evidence[e.Id]).Select(n => n.Container!.Value).Distinct().ToArray();
            // If one PnP container links incompatible AEP containers, do not merge either side.
            bool conflict = remote.Length > 1 || peerRemote.Length > 1 || (remote.Length == 1
                && eps.Where(e => evidence[e.Id].Any(n => n.Container == remote[0]))
                    .Where(e => e.Container is { Value: var value } && value != Guid.Empty)
                    .Select(e => e.Container).Distinct().Count() > 1);
            groupKeys[ep.Id] = conflict ? $"endpoint:{ep.Transport}:{ep.Id.Value}"
                : aepValid ? $"aep-container:{ep.Container!.Value.Value:D}"
                : remote.Length == 1 ? $"pnp-container:{remote[0].Value:D}" : $"endpoint:{ep.Transport}:{ep.Id.Value}";
        }
        var results = new List<ResolvedBinding>();
        foreach (var group in eps.GroupBy(e => groupKeys[e.Id], StringComparer.Ordinal))
        {
            Endpoint[] members = group.OrderBy(e => e.Id.Value, StringComparer.Ordinal).ToArray();
            var direct = members.SelectMany(e => evidence[e.Id]).DistinctBy(n => n.Id).ToArray();
            var remote = direct.Select(n => n.Container).Distinct().ToArray();
            PnpNode[] boundNodes = remote.Length == 1 ? pnp.Where(n => n.InLocalMachineContainer == false
                && n.Container == remote[0] && (n.Aep is null || members.Any(e => e.Id == n.Aep)))
                    .OrderBy(n => n.Id.Value, StringComparer.Ordinal).ToArray() : [];
            // Stable within this process only. Public diagnostics must alias these IDs again per export.
            if (!_opaqueIds.TryGetValue(group.Key, out string? id)) _opaqueIds[group.Key] = id = "device:" + ++_next;
            string signature = string.Join("|", members.Select(e => $"{e.Transport}:{e.Id.Value}").Order(StringComparer.Ordinal))
                + "#" + string.Join("|", boundNodes.Select(n => n.Id.Value).Order(StringComparer.Ordinal));
            _history.TryGetValue(id, out var old);
            long epoch = old.Epoch == 0 ? 1 : old.Key == signature ? old.Epoch : old.Epoch + 1;
            TransportState? Transport(TransportKind kind, TransportState? previous)
            {
                var transportMembers = members.Where(e => e.Transport == kind).ToArray();
                if (transportMembers.Length == 0) return null;
                var states = transportMembers.Select(e => e.Present ? e.Connection : ConnectionState.Unknown).Distinct().ToArray();
                ConnectionState state = states.Length == 1 ? states[0] : ConnectionState.Unknown;
                return new(state, previous is null ? 1 : previous.Connection == state && old.Key == signature ? previous.Epoch : previous.Epoch + 1);
            }
            var classic = Transport(TransportKind.Classic, old.Classic); var ble = Transport(TransportKind.Ble, old.Ble);
            _history[id] = (signature, epoch, classic, ble);
            var binding = new DeviceBinding { DeviceId = id, BindingKey = id + ":" + epoch,
                PreferenceKey = DisplaySettings.KeyFor("bluetooth:v1:" + group.Key),
                BindingEpoch = epoch, Classic = classic, Ble = ble, GattDatabaseEpoch = 1 };
            results.Add(new(new(binding, members.First().Name, classic is not null && ble is not null ? "蓝牙 Classic / BLE"
                : ble is not null ? "蓝牙 BLE" : "蓝牙 Classic"), members, boundNodes));
        }
        return results.AsReadOnly();
    }
    private static bool Valid(PnpContainerId? id) => id is { Value: var guid } && guid != Guid.Empty;

    public static IReadOnlyList<PnpNode> BoundedParents(PnpNode start, IEnumerable<PnpNode> nodes)
    {
        var index = nodes.ToDictionary(n => n.Id); var visited = new HashSet<DevnodeId> { start.Id }; var found = new List<PnpNode>();
        PnpNode current = start;
        for (int i = 0; i < 8 && current.Parent is { } parent && index.TryGetValue(parent, out var next); i++)
        {
            if (!visited.Add(next.Id) || next.InLocalMachineContainer != false || next.Container != start.Container) break;
            found.Add(next); current = next;
        }
        return found.AsReadOnly();
    }
}
