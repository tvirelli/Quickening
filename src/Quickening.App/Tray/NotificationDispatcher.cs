namespace Quickening.App.Tray;

/// <summary>
/// App-side fan-out for toast activations. AppNotificationManager requires
/// every NotificationInvoked subscription to exist BEFORE Register() is
/// called - subscribing afterwards throws 0x80070490 ("Must register event
/// handlers before calling Register()"). Services like the duplicate
/// watcher and scheduled scans need to attach/detach as the user toggles
/// them mid-session, so they can't subscribe to the WinRT event directly.
/// Instead App.xaml.cs makes the one permanent WinRT subscription before
/// Register() and forwards into this dispatcher, where subscriptions are
/// unrestricted.
///
/// Payloads dispatched while nobody is subscribed are buffered and replayed
/// to the first subscriber: on a cold launch from a toast click, Windows
/// delivers the pending activation as soon as Register() runs - before
/// OnLaunched has reached Watcher.Start()/ScheduledScan.Start().
/// </summary>
public sealed class NotificationDispatcher<T>
{
    private readonly object _lock = new();
    private readonly List<Subscription> _subscriptions = new();
    private readonly Queue<T> _pending = new();

    public IDisposable Subscribe(Action<T> handler)
    {
        Subscription subscription;
        T[] replay;
        lock (_lock)
        {
            subscription = new Subscription(this, handler);
            _subscriptions.Add(subscription);
            replay = _pending.ToArray();
            _pending.Clear();
        }

        foreach (var payload in replay)
        {
            Invoke(handler, payload);
        }

        return subscription;
    }

    public void Dispatch(T payload)
    {
        Action<T>[] handlers;
        lock (_lock)
        {
            if (_subscriptions.Count == 0)
            {
                _pending.Enqueue(payload);
                return;
            }
            handlers = _subscriptions.Select(s => s.Handler).ToArray();
        }

        foreach (var handler in handlers)
        {
            Invoke(handler, payload);
        }
    }

    private static void Invoke(Action<T> handler, T payload)
    {
        try
        {
            handler(payload);
        }
        catch (Exception ex)
        {
            // One service's bad activation handler must not eat the
            // activation for the other (both watcher and scheduled-scan
            // toasts flow through the same event).
            App.Logger?.LogError($"Notification handler threw: {ex}");
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_lock)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly NotificationDispatcher<T> _owner;
        public Action<T> Handler { get; }

        public Subscription(NotificationDispatcher<T> owner, Action<T> handler)
        {
            _owner = owner;
            Handler = handler;
        }

        public void Dispose() => _owner.Remove(this);
    }
}
