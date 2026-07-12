using Quickening.App.Tray;
using Xunit;

namespace Quickening.App.Tests;

public class NotificationDispatcherTests
{
    [Fact]
    public void Dispatch_DeliversPayload_ToSubscribedHandler()
    {
        var dispatcher = new NotificationDispatcher<string>();
        string? received = null;
        using var _ = dispatcher.Subscribe(p => received = p);

        dispatcher.Dispatch("hello");

        Assert.Equal("hello", received);
    }

    [Fact]
    public void Dispatch_DeliversPayload_ToAllSubscribers()
    {
        var dispatcher = new NotificationDispatcher<string>();
        var received = new List<string>();
        using var a = dispatcher.Subscribe(p => received.Add("a:" + p));
        using var b = dispatcher.Subscribe(p => received.Add("b:" + p));

        dispatcher.Dispatch("x");

        Assert.Equal(new[] { "a:x", "b:x" }, received);
    }

    [Fact]
    public void Dispatch_SkipsDisposedSubscription()
    {
        var dispatcher = new NotificationDispatcher<string>();
        string? received = null;
        var subscription = dispatcher.Subscribe(p => received = p);
        subscription.Dispose();

        dispatcher.Dispatch("gone");

        Assert.Null(received);
    }

    [Fact]
    public void Dispatch_WithNoSubscribers_BuffersUntilFirstSubscribe()
    {
        // Cold-launch-from-toast: Windows can replay a pending toast
        // activation the moment Register() runs, before any service has
        // called Start(). The dispatcher must hold that activation and
        // hand it to the first subscriber instead of dropping it.
        var dispatcher = new NotificationDispatcher<string>();
        dispatcher.Dispatch("early");

        var received = new List<string>();
        using var _ = dispatcher.Subscribe(received.Add);

        Assert.Equal(new[] { "early" }, received);
    }

    [Fact]
    public void BufferedPayloads_AreReplayedOnlyOnce()
    {
        var dispatcher = new NotificationDispatcher<string>();
        dispatcher.Dispatch("early");

        var first = new List<string>();
        using var a = dispatcher.Subscribe(first.Add);
        var second = new List<string>();
        using var b = dispatcher.Subscribe(second.Add);

        Assert.Equal(new[] { "early" }, first);
        Assert.Empty(second);
    }

    [Fact]
    public void Dispatch_ContinuesToRemainingHandlers_WhenOneThrows()
    {
        var dispatcher = new NotificationDispatcher<string>();
        using var bad = dispatcher.Subscribe(_ => throw new InvalidOperationException("boom"));
        string? received = null;
        using var good = dispatcher.Subscribe(p => received = p);

        dispatcher.Dispatch("survives");

        Assert.Equal("survives", received);
    }

    [Fact]
    public void Dispatch_AfterFirstSubscriberDisposes_BuffersAgainForNextSubscriber()
    {
        // A user can toggle the watcher off (Stop -> dispose subscription)
        // and a scheduled-scan toast can land in that window; the next
        // Start() should still see it.
        var dispatcher = new NotificationDispatcher<string>();
        var subscription = dispatcher.Subscribe(_ => { });
        subscription.Dispose();

        dispatcher.Dispatch("between");

        var received = new List<string>();
        using var _ = dispatcher.Subscribe(received.Add);
        Assert.Equal(new[] { "between" }, received);
    }
}
