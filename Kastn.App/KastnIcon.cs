using Avalonia.Controls;

namespace KASTN;

/// <summary>
/// Uses the same multi-resolution K icon for the window and the Windows executable.
/// </summary>
internal static class KastnIcon
{
    private static WindowIcon? cached;

    public static WindowIcon Create() => cached ??= Build();

    private static WindowIcon Build()
    {
        using var stream = typeof(KastnIcon).Assembly.GetManifestResourceStream("KASTN.kastn.ico")
            ?? throw new InvalidOperationException("Kastn icon resource is missing.");
        return new WindowIcon(stream);
    }
}
