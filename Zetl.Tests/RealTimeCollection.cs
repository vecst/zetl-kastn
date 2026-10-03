using Xunit;

namespace ZETL.Tests;

// Tests that measure real time (a hold firing after 200 ms, a burst of
// refreshes settling, an IPC reconnect) run in this collection, which xUnit
// never runs alongside another. Under parallel load their sleeps and timeouts
// stretch, and a hold turns into a tap.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealTimeCollection
{
    public const string Name = "Real time";
}
