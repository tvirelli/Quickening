namespace Quickening.Core.Logging;

public interface ILogger
{
    void LogError(string message, Exception? exception = null);
    void LogWarning(string message);
    void LogInfo(string message);
}
