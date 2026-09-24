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
}
