namespace Quickening.Tests.Shared;

// IProgress<T>.Report on the built-in System.Progress<T> marshals through
// a captured SynchronizationContext, which doesn't exist in a plain xUnit
// test - it would silently queue reports on the thread pool instead of
// invoking the callback inline, making assertions unreliable. This minimal
// IProgress<T> implementation invokes the callback synchronously/
// immediately instead, which is what a test needs to observe reports
// deterministically. Linked (not project-referenced) into both
// Quickening.Core.Tests and Quickening.App.Tests via <Compile Include>,
// since those two test projects don't reference each other and a whole
// extra shared project would be disproportionate for one small class.
internal sealed class SynchronousProgress<T> : IProgress<T>
{
    private readonly Action<T> _callback;

    public SynchronousProgress(Action<T> callback)
    {
        _callback = callback;
    }

    public void Report(T value) => _callback(value);
}
