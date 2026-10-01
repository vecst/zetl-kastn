using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ZETL;

// Keeps a window fully transparent until its first layout is done, then shows
// it all at once. Windows draws the frame immediately, but a large window like
// Settings needs a moment to lay out; without this the user sees an empty
// glass frame and then the controls popping in.
internal static class ZetlWindowReveal
{
    public static void WhenReady(Window window, Action<TimeSpan>? onRevealed = null)
    {
        var started = Stopwatch.GetTimestamp();
        var target = window.Opacity;
        window.Opacity = 0;

        void Reveal(object? sender, EventArgs e)
        {
            window.Opened -= Reveal;
            // Background priority runs after the dispatcher's layout and render
            // work, so the first full frame is ready when the window appears.
            Dispatcher.UIThread.Post(
                () =>
                {
                    window.Opacity = target;
                    onRevealed?.Invoke(Stopwatch.GetElapsedTime(started));
                },
                DispatcherPriority.Background);
        }

        window.Opened += Reveal;
    }
}
