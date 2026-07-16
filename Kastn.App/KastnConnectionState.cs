namespace KASTN;

internal enum KastnConnectionState
{
    Connecting,
    Online,
    Offline
}

internal sealed record KastnSessionSnapshot(
    KastnConnectionState ConnectionState,
    string Status,
    IReadOnlyList<ZETL.Contracts.ZetlProjectSummary> Projects,
    ZETL.Contracts.ZetlProjectSnapshot? Project)
{
    public string? ServerInstanceId { get; init; }
}
