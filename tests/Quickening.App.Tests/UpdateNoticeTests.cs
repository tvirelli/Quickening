using Quickening.App.Updates;
using Xunit;

namespace Quickening.App.Tests;

public class UpdateNoticeTests
{
    [Fact]
    public void Evaluate_FreshInstall_NullLastSeen_ReturnsNull()
    {
        Assert.Null(UpdateNotice.Evaluate(lastSeenVersion: null, currentVersion: "1.0.0"));
    }

    [Fact]
    public void Evaluate_EmptyLastSeen_ReturnsNull()
    {
        Assert.Null(UpdateNotice.Evaluate(lastSeenVersion: "", currentVersion: "1.0.0"));
    }

    [Fact]
    public void Evaluate_SameVersion_ReturnsNull()
    {
        Assert.Null(UpdateNotice.Evaluate(lastSeenVersion: "1.0.0", currentVersion: "1.0.0"));
    }

    [Fact]
    public void Evaluate_ChangedVersion_ReturnsNote()
    {
        Assert.Equal(
            "Updated to v1.0.1",
            UpdateNotice.Evaluate(lastSeenVersion: "1.0.0", currentVersion: "1.0.1"));
    }
}
