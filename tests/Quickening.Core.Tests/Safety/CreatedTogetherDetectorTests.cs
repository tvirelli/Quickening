using Quickening.Core.Safety;
using Xunit;

namespace Quickening.Core.Tests.Safety;

public class CreatedTogetherDetectorTests
{
    private static DateTime T(int h, int m, int s, int ms = 0) => new(2026, 1, 1, h, m, s, ms, DateTimeKind.Utc);

    [Fact]
    public void SameSecond_AcrossAllCopies_IsFlagged()
    {
        var times = new[] { T(10, 0, 5, 100), T(10, 0, 5, 900), T(10, 0, 5, 0) };
        Assert.True(CreatedTogetherDetector.IsLikelyCreatedTogether(times));
    }

    [Fact]
    public void DifferentSeconds_IsNotFlagged()
    {
        var times = new[] { T(10, 0, 5), T(10, 0, 6) };
        Assert.False(CreatedTogetherDetector.IsLikelyCreatedTogether(times));
    }

    [Fact]
    public void SingleFile_IsNotFlagged()
    {
        Assert.False(CreatedTogetherDetector.IsLikelyCreatedTogether(new[] { T(10, 0, 5) }));
    }

    [Fact]
    public void AnyDefaultTime_IsNotFlagged()
    {
        var times = new[] { T(10, 0, 5), default };
        Assert.False(CreatedTogetherDetector.IsLikelyCreatedTogether(times));
    }
}
