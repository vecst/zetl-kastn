using System;
using System.IO;
using ZETL;

namespace KASTN;

// Kastn-owned runtime state, kept in its own file so writing it never races with
// Zetl's writes to the shared settings.json. Currently just the last project opened
// in the workbench, used by the "reopen last project" startup preference.
internal sealed class KastnState
{
    public string LastProjectId { get; set; } = "";
    public List<string> PinnedProjectIds { get; set; } = [];
}

internal sealed class KastnStateStore
{
    private readonly string path;
    private readonly Action<string>? log;

    public KastnStateStore(string? path = null, Action<string>? log = null)
    {
        this.path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Zetl",
            "kastn-state.json");
        this.log = log;
        State = JsonFile.ReadOrQuarantine<KastnState>(this.path, log) ?? new KastnState();
        State.PinnedProjectIds ??= [];
    }

    public KastnState State { get; }

    public string LastProjectId
    {
        get => State.LastProjectId;
        set
        {
            var next = value ?? "";
            if (string.Equals(State.LastProjectId, next, StringComparison.Ordinal))
            {
                return;
            }

            State.LastProjectId = next;
            try
            {
                JsonFile.WriteAtomic(path, State);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"Could not save Kastn state: {ex.Message}");
            }
        }
    }

    public IReadOnlyList<string> PinnedProjectIds => State.PinnedProjectIds;

    public bool IsPinned(string projectId) =>
        State.PinnedProjectIds.Contains(projectId, StringComparer.Ordinal);

    public void SetPinned(string projectId, bool pinned)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        State.PinnedProjectIds ??= [];
        var changed = pinned
            ? AddPinned(projectId)
            : State.PinnedProjectIds.RemoveAll(item => string.Equals(item, projectId, StringComparison.Ordinal)) > 0;
        if (changed)
        {
            SaveState();
        }
    }

    private bool AddPinned(string projectId)
    {
        if (IsPinned(projectId))
        {
            return false;
        }

        State.PinnedProjectIds.Add(projectId);
        return true;
    }

    private void SaveState()
    {
        try
        {
            JsonFile.WriteAtomic(path, State);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Could not save Kastn state: {ex.Message}");
        }
    }
}
