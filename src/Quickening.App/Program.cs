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
