using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Quickening.App;

/// <summary>
/// Custom process entry point (the WinUI-generated Main is disabled via the
/// DISABLE_XAML_GENERATED_MAIN constant in the csproj). VelopackApp.Run() MUST
/// be the first thing that executes: on an install/update/uninstall invocation
/// it performs its work and exits before any window is created; on a normal
/// launch it is a no-op and control falls through to the standard WinUI
/// bootstrap below (which reproduces what the generated Main did).
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Process-wide backstop against catastrophic regex backtracking (ReDoS).
        // ColorCode's CSS grammar (used by the code preview for .css/.less/.scss)
        // backtracks forever on some real stylesheet content — a tiny slick.less
        // hangs its tokenizer indefinitely — and FormatRichTextBlock runs that
        // regex synchronously on the UI thread, so the whole app went "not
        // responding". This caps EVERY .NET regex that doesn't set its own
        // timeout: a runaway match now throws RegexMatchTimeoutException (which
        // the code-preview builder catches and degrades to plain monospace)
        // instead of pinning a core forever. Must run before the first Regex is
        // constructed, so it is the very first statement in the process.
        AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromSeconds(1));

        VelopackApp.Build().Run();

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
