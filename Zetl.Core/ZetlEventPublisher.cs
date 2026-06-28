namespace ZETL;

internal static class ZetlEventPublisher
{
    public static void Publish(
        EventHandler? handlers,
        object sender,
        EventArgs args,
        Action<Exception>? onSubscriberError = null)
    {
        if (handlers is null)
        {
            return;
        }

        var subscribers = handlers.GetInvocationList();
        foreach (EventHandler handler in subscribers)
        {
            try
            {
                handler(sender, args);
            }
            catch (Exception ex)
            {
                onSubscriberError?.Invoke(ex);
            }
        }
    }

    public static void Publish<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        object sender,
        TEventArgs args,
        Action<Exception>? onSubscriberError = null)
    {
        if (handlers is null)
        {
            return;
        }

        var subscribers = handlers.GetInvocationList();
        foreach (EventHandler<TEventArgs> handler in subscribers)
        {
            Invoke(handler, sender, args, onSubscriberError);
        }
    }

    private static void Invoke<TEventArgs>(
        EventHandler<TEventArgs> handler,
        object sender,
        TEventArgs args,
        Action<Exception>? onSubscriberError)
    {
        try
        {
            handler(sender, args);
        }
        catch (Exception ex)
        {
            onSubscriberError?.Invoke(ex);
        }
    }
}
