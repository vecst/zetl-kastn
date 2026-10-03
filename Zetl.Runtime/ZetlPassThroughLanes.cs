namespace ZETL;

// Whether pass-through is on, per lane: the setting, unless a held Ctrl+P has
// flipped it for now. A flip lasts while the lane keeps being used. Once the
// lane has gone QuietWindow without a copy or a paste, or its project has
// changed, the setting applies again, so a break to chat can turn it on and
// the work afterwards finds things as they were.
internal sealed class ZetlPassThroughLanes(Func<DateTimeOffset> clock)
{
    public static readonly TimeSpan QuietWindow = TimeSpan.FromMinutes(10);

    private readonly object gate = new();
    private readonly Flip?[] flips = new Flip?[2];

    private sealed class Flip(string? projectId, DateTimeOffset at)
    {
        public string? ProjectId { get; } = projectId;
        public DateTimeOffset LastActivity { get; set; } = at;
    }

    // Flips the lane and returns whether pass-through is now on. Flipping a
    // flipped lane goes back to the setting.
    public bool Toggle(bool setting, bool shifted, string? projectId)
    {
        lock (gate)
        {
            var index = ZetlLanes.Index(shifted);
            flips[index] = IsFlippedLocked(index, projectId)
                ? null
                : new Flip(projectId, clock());
            return flips[index] is null ? setting : !setting;
        }
    }

    public bool IsOn(bool setting, bool shifted, string? projectId)
    {
        lock (gate)
        {
            return IsFlippedLocked(ZetlLanes.Index(shifted), projectId) ? !setting : setting;
        }
    }

    public bool IsFlipped(bool shifted, string? projectId)
    {
        lock (gate)
        {
            return IsFlippedLocked(ZetlLanes.Index(shifted), projectId);
        }
    }

    // A copy or paste on the lane keeps a live flip alive; one that has
    // already gone quiet stays ended.
    public void NoteActivity(bool shifted)
    {
        lock (gate)
        {
            if (flips[ZetlLanes.Index(shifted)] is { } flip
                && clock() - flip.LastActivity < QuietWindow)
            {
                flip.LastActivity = clock();
            }
        }
    }

    // Ends flips that have gone quiet or whose project changed; returns whether
    // any did, so the caller can refresh what shows the flip.
    public bool Expire(Func<bool, string?> projectIdForLane)
    {
        lock (gate)
        {
            var ended = false;
            foreach (var shifted in new[] { false, true })
            {
                var index = ZetlLanes.Index(shifted);
                if (flips[index] is not null && !IsFlippedLocked(index, projectIdForLane(shifted)))
                {
                    flips[index] = null;
                    ended = true;
                }
            }

            return ended;
        }
    }

    // A flip that has gone quiet or outlived its project no longer counts,
    // even before Expire clears it.
    private bool IsFlippedLocked(int index, string? projectId) =>
        flips[index] is { } flip
        && string.Equals(flip.ProjectId, projectId, StringComparison.Ordinal)
        && clock() - flip.LastActivity < QuietWindow;
}
