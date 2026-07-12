using Quickening.Core.Logging;
using Xunit;

namespace Quickening.Core.Tests.Logging;

public class FileLoggerTests : IDisposable
{
    private readonly string _logDir;

    public FileLoggerTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "QuickeningLogTests_" + Guid.NewGuid());
    }

    public void Dispose()
    {
        if (Directory.Exists(_logDir))
        {
            Directory.Delete(_logDir, recursive: true);
        }
    }

    [Fact]
    public void LogError_WritesMessageAndExceptionToTodaysLogFile()
    {
        var logger = new FileLogger(_logDir);

        logger.LogError("Something broke", new InvalidOperationException("boom"));

        var expectedPath = Path.Combine(_logDir, $"quickening-{DateTime.UtcNow:yyyy-MM-dd}.log");
        Assert.True(File.Exists(expectedPath));
        var content = File.ReadAllText(expectedPath);
        Assert.Contains("[ERROR]", content);
        Assert.Contains("Something broke", content);
        Assert.Contains("boom", content);
    }

    [Fact]
    public void LogInfo_And_LogWarning_AppendSeparateLines()
    {
        var logger = new FileLogger(_logDir);

        logger.LogInfo("scan started");
        logger.LogWarning("skipped a locked file");

        var expectedPath = Path.Combine(_logDir, $"quickening-{DateTime.UtcNow:yyyy-MM-dd}.log");
        var lines = File.ReadAllLines(expectedPath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("[INFO]", lines[0]);
        Assert.Contains("[WARN]", lines[1]);
    }

    [Fact]
    public void Constructor_CreatesLogDirectory_IfMissing()
    {
        Assert.False(Directory.Exists(_logDir));

        _ = new FileLogger(_logDir);

        Assert.True(Directory.Exists(_logDir));
    }

    [Fact]
    public void LogError_DoesNotThrow_WhenTheUnderlyingWriteFails()
    {
        // A logger call is frequently made from inside error-handling code
        // (see App.Logger?.LogError(...) in HomePage's catch block) - if
        // the write itself fails, it must never throw and replace/mask the
        // real error being logged. Force a write failure by pre-creating a
        // directory at the exact path the log file would occupy, so
        // File.AppendAllText fails with "is a directory, not a file".
        Directory.CreateDirectory(_logDir);
        var collidingPath = Path.Combine(_logDir, $"quickening-{DateTime.UtcNow:yyyy-MM-dd}.log");
        Directory.CreateDirectory(collidingPath);

        var logger = new FileLogger(_logDir);

        var exception = Record.Exception(() => logger.LogError("this should not throw"));

        Assert.Null(exception);
    }
}
