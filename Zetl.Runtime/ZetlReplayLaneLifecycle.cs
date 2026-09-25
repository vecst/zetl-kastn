namespace ZETL;

/// <summary>
/// Tracks the short interval after a lane consumes its final Replay item but
/// before its clipboard restoration and bucket transition have settled.
/// </summary>
internal sealed class ZetlReplayLaneLifecycle
{
    private readonly int[] phases = new int[2];

    public bool IsRestoring(bool shifted)
    {
        return Volatile.Read(ref phases[ZetlLanes.Index(shifted)])
            == (int)ZetlReplayLanePhase.Restoring;
    }

    public bool TryBeginRestoring(bool shifted)
    {
        return Interlocked.CompareExchange(
            ref phases[ZetlLanes.Index(shifted)],
            (int)ZetlReplayLanePhase.Restoring,
            (int)ZetlReplayLanePhase.Ready) == (int)ZetlReplayLanePhase.Ready;
    }

    public void CompleteRestoring(bool shifted)
    {
        Volatile.Write(
            ref phases[ZetlLanes.Index(shifted)],
            (int)ZetlReplayLanePhase.Ready);
    }
}

internal enum ZetlReplayLanePhase
{
    Ready,
    Restoring
}
