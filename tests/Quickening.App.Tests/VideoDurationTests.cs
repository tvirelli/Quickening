using System;
using Quickening.App.Controls;
using Xunit;

namespace Quickening.App.Tests;

public class VideoDurationTests
{
    [Fact]
    public void UsesObservedEnd_OnceKnown_CorrectingBogusNatural()
    {
        // 2:48:05 claimed vs the ~10.5s real end (the fragmented-MP4 bug).
        var best = VideoDuration.Best(TimeSpan.FromSeconds(10085), TimeSpan.FromSeconds(10.5));
        Assert.Equal(TimeSpan.FromSeconds(10.5), best);
    }

    [Fact]
    public void UsesObservedEnd_EvenWhenNaturalIsClose()
    {
        var best = VideoDuration.Best(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(29.98));
        Assert.Equal(TimeSpan.FromSeconds(29.98), best);
    }

    [Fact]
    public void UsesNatural_WhenNoObservedEndYet()
    {
        var best = VideoDuration.Best(TimeSpan.FromSeconds(30), TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(30), best);
    }
}
