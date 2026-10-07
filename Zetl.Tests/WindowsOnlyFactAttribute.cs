using Xunit;

namespace ZETL.Tests;

// Windows sharing modes make an open reader block replacing a file; Unix only
// takes advisory locks, so an atomic rename succeeds past one. Tests that inject
// failures through that lock cannot fail the write elsewhere.
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Relies on Windows file-sharing semantics.";
    }
}

public sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    public WindowsOnlyTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Relies on Windows file-sharing semantics.";
    }
}
