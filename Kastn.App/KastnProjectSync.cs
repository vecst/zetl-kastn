using System.Text.Json;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

/// <summary>
/// One project read: the full snapshot Kastn publishes, plus the delta cursor
/// that describes the same boundary (null when Zetl lacks delta reads).
/// </summary>
internal sealed record KastnProjectRead(
    ZetlProjectSnapshot Snapshot,
    ZetlProjectCursor? Cursor,
    string? ResetReason);

/// <summary>
/// Reads the open project from Zetl, by cursor-based delta when Zetl offers it.
/// Owns the applied snapshot/cursor pair for one project and rebuilds a complete
/// snapshot from each delta, so everything past the connection layer still sees
/// the same full snapshots as before. A read only builds a candidate; the
/// controller commits it once the read is still current, so an obsolete read
/// never moves the cursor.
/// </summary>
internal sealed class KastnProjectSync
{
    private readonly object gate = new();
    private Applied? applied;

    private sealed record Applied(ZetlProjectSnapshot Snapshot, ZetlProjectCursor Cursor);

    public Task<KastnProjectRead> ReadAsync(
        ZetlIpcClient client,
        string projectId,
        CancellationToken cancellationToken) =>
        ReadAsync(client.ExecuteAsync, client.Capabilities.Contains(ZetlIpcCapabilities.ProjectChanges),
            projectId, cancellationToken);

    /// <summary>Reads through <paramref name="send"/>; tests supply their own replies.</summary>
    internal async Task<KastnProjectRead> ReadAsync(
        Func<ZetlCommandEnvelope, CancellationToken, Task<ZetlResponseEnvelope>> send,
        bool deltasOffered,
        string projectId,
        CancellationToken cancellationToken)
    {
        if (!deltasOffered)
        {
            return new KastnProjectRead(
                await GetProjectAsync(send, projectId, cancellationToken).ConfigureAwait(false),
                null,
                null);
        }

        Applied? baseline;
        lock (gate)
        {
            baseline = applied?.Snapshot.Id == projectId ? applied : null;
        }

        // A cursor from a restarted Zetl is still sent: the server answers with
        // an explicit epoch_changed reset rather than Kastn guessing.
        var changes = await GetChangesAsync(send, projectId, baseline?.Cursor, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return new KastnProjectRead(
                ZetlProjectChangesReducer.Apply(baseline?.Snapshot, baseline?.Cursor, changes),
                changes.Cursor,
                changes.ResetReason);
        }
        catch (InvalidDataException)
        {
            // A batch that does not validate leaves the applied pair alone and
            // gets one fresh bootstrap. If that fails too, the refresh fails.
            var reset = await GetChangesAsync(send, projectId, null, cancellationToken)
                .ConfigureAwait(false);
            return new KastnProjectRead(
                ZetlProjectChangesReducer.Apply(null, null, reset),
                reset.Cursor,
                reset.ResetReason);
        }
    }

    /// <summary>
    /// Records what Kastn just published. Call it under the controller's state
    /// gate, after admission, with the read that produced the published project
    /// (null when no project was read). Keeps a baseline whose project still
    /// exists, so a refresh that reads nothing does not throw it away.
    /// </summary>
    public void Commit(KastnProjectRead? read, IReadOnlyList<ZetlProjectSummary> projects)
    {
        lock (gate)
        {
            if (read is not null)
            {
                applied = read.Cursor is null ? null : new Applied(read.Snapshot, read.Cursor);
            }
            else if (applied is not null && projects.All(project => project.Id != applied.Snapshot.Id))
            {
                applied = null;
            }
        }
    }

    private static async Task<ZetlProjectChanges> GetChangesAsync(
        Func<ZetlCommandEnvelope, CancellationToken, Task<ZetlResponseEnvelope>> send,
        string projectId,
        ZetlProjectCursor? cursor,
        CancellationToken cancellationToken)
    {
        var response = await send(
            ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.GetProjectChanges,
                new GetProjectChangesCommand { Cursor = cursor },
                projectId),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return response.Payload?.Deserialize<ZetlProjectChanges>(ZetlProtocolJson.Options)
            ?? throw new InvalidDataException("Zetl returned no project changes.");
    }

    private static async Task<ZetlProjectSnapshot> GetProjectAsync(
        Func<ZetlCommandEnvelope, CancellationToken, Task<ZetlResponseEnvelope>> send,
        string projectId,
        CancellationToken cancellationToken)
    {
        var response = await send(
            new ZetlCommandEnvelope
            {
                CommandId = Guid.NewGuid().ToString("N"),
                Kind = ZetlCommandKind.GetProject,
                ProjectId = projectId
            },
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return response.Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidDataException("Zetl returned no project.");
    }

    internal static void EnsureSuccess(ZetlResponseEnvelope response)
    {
        if (response.Status != ZetlResponseStatus.Success)
        {
            throw new InvalidOperationException(
                response.Error?.Message ?? $"Zetl returned {response.Status}.");
        }
    }
}
