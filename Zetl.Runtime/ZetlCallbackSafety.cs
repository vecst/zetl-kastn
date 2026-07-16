namespace ZETL;

internal static class ZetlCallbackSafety
{
    public static IntPtr FailOpen(
        Exception exception,
        Func<IntPtr> passThrough,
        Action<Exception>? reportFailure = null)
    {
        try
        {
            reportFailure?.Invoke(exception);
        }
        catch
        {
            // Diagnostics must never turn an input-handler failure into a
            // process-terminating exception at an unmanaged callback boundary.
        }

        try
        {
            return passThrough();
        }
        catch
        {
            // Returning zero from a low-level keyboard callback leaves the event
            // unsuppressed even when chaining to the next hook also fails.
            return IntPtr.Zero;
        }
    }
}
