namespace ZETL;

// Quitting Zetl while Kastn is connected asks Kastn first, and Kastn may hold
// that question open behind a save dialog. A second Quit used to open a second
// request: Kastn's control channel was still busy with the first, the second
// connection timed out, a timeout reads as "no Kastn", and Zetl shut down under
// the pending dialog, after which Kastn started a new Zetl. Requests made while
// one is in flight now share its outcome instead.
internal sealed class ZetlQuitCoordinator(
    Func<bool> kastnConnected,
    Func<Task<KastnShutdownDecision>> askKastn,
    Action shutdown,
    Action<string> log)
{
    private Task? inFlight;

    // Runs on the UI thread, like the tray menu that calls it.
    public Task RequestAsync()
    {
        if (inFlight is { IsCompleted: false } pending)
        {
            log("Quit is already waiting on Kastn's decision.");
            return pending;
        }

        inFlight = RunAsync();
        return inFlight;
    }

    public bool IsWaitingOnKastn => inFlight is { IsCompleted: false };

    private async Task RunAsync()
    {
        if (!kastnConnected())
        {
            shutdown();
            return;
        }

        KastnShutdownDecision decision;
        try
        {
            decision = await askKastn();
        }
        catch (Exception ex)
        {
            // Don't trap the user in an unquittable Zetl if the signal fails.
            log($"Kastn shutdown request failed ({ex.GetType().Name}): {ex.Message}");
            decision = KastnShutdownDecision.NoKastn;
        }

        if (decision == KastnShutdownDecision.Cancel)
        {
            log("Quit cancelled at Kastn's confirmation.");
            return;
        }

        shutdown();
    }
}
