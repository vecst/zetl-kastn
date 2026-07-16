namespace ZETL;

/// <summary>
/// The command write started, but the client did not receive a confirmed
/// response. The server may have durably applied the command. Callers may retry
/// the same command id against the same server instance, or refresh and
/// reconcile before deciding what to do next.
/// </summary>
public sealed class ZetlCommandOutcomeUnknownException : IOException
{
    public ZetlCommandOutcomeUnknownException(
        string commandId,
        string message,
        Exception innerException,
        bool serverInstanceChanged = false)
        : base(message, innerException)
    {
        CommandId = commandId;
        ServerInstanceChanged = serverInstanceChanged;
    }

    public string CommandId { get; }

    public bool ServerInstanceChanged { get; }
}
