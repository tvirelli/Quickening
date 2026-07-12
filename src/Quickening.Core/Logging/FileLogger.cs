using System.Text;

namespace Quickening.Core.Logging;

/// <summary>
/// Minimal thread-safe append-only file logger. One log file per calendar
/// day at logDirectory\quickening-yyyy-MM-dd.log. Deliberately simple: no
/// buffering, no rotation/retention policy, no external logging package -
/// this app's error volume is low (user-facing failure paths, not a hot
/// loop), so durability (each line flushed immediately) wins over
/// throughput. Revisit if that assumption stops holding.
/// </summary>
public sealed class FileLogger : ILogger
{
    private readonly string _logDirectory;
    private readonly object _writeLock = new();

    public FileLogger(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public void LogError(string message, Exception? exception = null)
    {
        var full = exception is null ? message : $"{message} - {exception}";
        Write("ERROR", full);
    }

    public void LogWarning(string message) => Write("WARN", message);

    public void LogInfo(string message) => Write("INFO", message);

    private void Write(string level, string message)
    {
        var now = DateTime.UtcNow;
        var line = $"{now:O} [{level}] {message}{Environment.NewLine}";
        var path = Path.Combine(_logDirectory, $"quickening-{now:yyyy-MM-dd}.log");

        try
        {
            lock (_writeLock)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // A logging failure (disk full, file locked by another process,
            // etc.) must never propagate - this is frequently called from
            // inside error handlers, and letting a logging failure replace
            // or mask the real error being logged (or fault a background
            // scan thread) would be worse than losing one log line.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as above.
        }
    }
}
