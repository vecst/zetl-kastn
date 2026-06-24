namespace ZETL;

internal static class ZetlRuntimeSettings
{
    public static void ApplyTo(ZetlStateStore store, ZetlAppSettings settings)
    {
        var projectBuckets = ZetlBucketDefaults.ResolveProjectBuckets(settings.DefaultProjectBuckets).ToList();
        store.Defaults = new ZetlBucketDefaults(
            projectBuckets,
            settings.DefaultCompileMode,
            settings.DefaultTsvRowLength)
        {
            DayStartHour = settings.DayStartHour,
            JournalAutoReturnHours = settings.JournalAutoReturnHours
        };
    }
}

internal sealed record ZetlUndoAction(bool Shifted, string Message, Action Undo);

internal sealed class ZetlUndoStack
{
    private readonly int capacity;
    private readonly List<ZetlUndoAction> actions = new();

    public ZetlUndoStack(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.capacity = capacity;
    }

    public void Push(bool shifted, string message, Action undo)
    {
        actions.Add(new ZetlUndoAction(shifted, message, undo));
        if (actions.Count > capacity)
        {
            actions.RemoveRange(0, actions.Count - capacity);
        }
    }

    public bool TryPop(bool shifted, out ZetlUndoAction? action)
    {
        var index = actions.FindLastIndex(item => item.Shifted == shifted);
        if (index < 0)
        {
            action = null;
            return false;
        }

        action = actions[index];
        actions.RemoveAt(index);
        return true;
    }
}

internal sealed class ZetlActivityLogBuffer
{
    private readonly object gate = new();
    private readonly Func<DateTime> getNow;
    private readonly List<string> pending = new();

    public ZetlActivityLogBuffer(Func<DateTime>? getNow = null)
    {
        this.getNow = getNow ?? (() => DateTime.Now);
    }

    public void Enqueue(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = $"[{getNow():HH:mm:ss}] {message}";
        lock (gate)
        {
            pending.Add(line);
        }
    }

    public IReadOnlyList<string> Drain()
    {
        lock (gate)
        {
            if (pending.Count == 0)
            {
                return [];
            }

            var batch = pending.ToList();
            pending.Clear();
            return batch;
        }
    }
}

internal static class ZetlAsync
{
    // Fire-and-forget a task while still surfacing failures: await it and log any
    // exception under the given label, so a faulted background task (replay, pop,
    // clipboard restore, paste) can't vanish without a diagnostic. This is the
    // fire-and-forget root, so it deliberately catches everything rather than
    // letting an exception reach the synchronization context unobserved.
    public static async void RunLogged(Func<Task> operation, string operationName, Action<string> log)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            try
            {
                log($"{operationName} failed: {ex}");
            }
            catch
            {
                // Last-chance diagnostic path: never let a logging failure escape
                // the async-void root and surface as an unhandled exception.
            }
        }
    }
}

internal static class ZetlRuntimeLabels
{
    public static string Destination(ZetlProject? project, ZetlBucket bucket)
    {
        return project is null ? bucket.Name : $"{bucket.Name} in {project.Name}";
    }
}
