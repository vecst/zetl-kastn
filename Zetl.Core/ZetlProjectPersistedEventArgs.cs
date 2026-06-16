namespace ZETL;

internal sealed class ZetlProjectPersistedEventArgs : EventArgs
{
    public ZetlProjectPersistedEventArgs(string projectId, long changeSequence)
    {
        ProjectId = projectId;
        ChangeSequence = changeSequence;
    }

    public string ProjectId { get; }
    public long ChangeSequence { get; }
}
