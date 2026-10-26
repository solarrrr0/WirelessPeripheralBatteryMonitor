namespace PeripheralBatteryTray.Core;

[Flags]
public enum RefreshReason { None = 0, Discovery = 1, Connection = 2, Manual = 4, Periodic = 8,
    ListOpened = 16, Database = 32, Property = 64 }
public enum QueryPhase { Running, Draining }

/// <summary>Local opaque keys only. CreateRead must not initiate I/O.</summary>
public sealed class QueryRequest
{
    public QueryStamp Stamp { get; }
    public string BindingKey { get; }
    public RefreshReason Reason { get; }
    public long StartedTimestamp { get; }
    public TimeSpan Budget { get; }
    public IReadOnlyList<string> DueServiceKeys { get; }
    public QueryRequest(QueryStamp stamp, string bindingKey, RefreshReason reason, long startedTimestamp,
        TimeSpan budget, IEnumerable<string> dueServiceKeys)
    {
        Stamp = stamp; BindingKey = bindingKey; Reason = reason; StartedTimestamp = startedTimestamp;
        Budget = budget; DueServiceKeys = Array.AsReadOnly(dueServiceKeys.ToArray());
    }
}
public sealed record DirectoryMember(string ComponentKey, string ServiceKey, int DisplayOrder);
public abstract record ProviderUpdate(long Sequence);
public sealed record ValueUpdate(long Sequence, string CandidateKey, string? ServiceKey, ReadOutcome Outcome,
    BatteryValue? Value, DateTimeOffset ObservedAtUtc, long CapturedTimestamp) : ProviderUpdate(Sequence);
public sealed record DirectoryUpdate : ProviderUpdate
{
    public DirectoryScope Scope { get; }
    public string? ParentKey { get; }
    public IReadOnlyList<DirectoryMember> Members { get; }
    public bool Complete { get; }
    public DirectoryUpdate(long sequence, DirectoryScope scope, string? parentKey,
        IEnumerable<DirectoryMember> members, bool complete) : base(sequence)
    { Scope = scope; ParentKey = parentKey; Members = Array.AsReadOnly(members.ToArray()); Complete = complete; }
}
public sealed record DirectoryChangedUpdate(long Sequence, bool ConfirmedMissing = false) : ProviderUpdate(Sequence);
public sealed record ServiceFailureUpdate(long Sequence, string ServiceKey, ReadOutcome Outcome) : ProviderUpdate(Sequence);
public sealed record ReadCompletion(ReadOutcome Outcome, int CleanupErrors = 0);
public sealed record ShutdownResult(bool FullyStopped, int UnresolvedOperations, int CleanupErrors);
public sealed record ActiveRead(QueryStamp Stamp, QueryPhase Phase);

public interface IBatteryProvider : IAsyncDisposable
{
    ReadingSource Source { get; }
    IBatteryReadOperation CreateRead(QueryRequest request, CancellationToken lifetime);
}
public interface IBatteryReadOperation
{
    // Completion denotes original work terminal AND attempted cleanup, never a timeout wrapper.
    Task<ReadCompletion> Completion { get; }
    void Start(Action<ProviderUpdate> publish);
    void RequestCancel();
}
