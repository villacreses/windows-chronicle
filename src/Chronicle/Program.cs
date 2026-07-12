using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chronicle;

/// <summary>
/// Custom entry point (the generated one is disabled via the
/// DISABLE_XAML_GENERATED_MAIN define) so Chronicle can single-instance: a
/// clicked reminder toast must FOCUS the existing window, not spawn a second
/// one.
///
/// Single-instance redirection lives HERE, in Main, before
/// <see cref="Application.Start"/> — never in <c>App.OnLaunched</c>.
/// Redirecting from OnLaunched deadlocks the XAML STA thread with a
/// COMException (learned in the Phase C activation spike). See
/// NOTIFICATIONS.md "Single-instancing."
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection())
            return; // handed our activation to the primary instance; exit.

        Application.Start((p) =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    /// <summary>
    /// Returns true when this launch was redirected to an already-running
    /// instance (so this process should exit). Returns false when we ARE the
    /// primary instance and should start the app.
    /// </summary>
    private static bool DecideRedirection()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var primary = AppInstance.FindOrRegisterForKey("chronicle-main");

        if (primary.IsCurrent)
        {
            // We are the primary. Route activations redirected here by later
            // launches (e.g. a toast click while we're open) into the app.
            primary.Activated += (_, redirectedArgs) =>
                App.Instance?.OnRedirectedActivation(redirectedArgs);
            return false;
        }

        RedirectActivationTo(primary, activation);
        return true;
    }

    /// <summary>
    /// Redirects on a worker thread and waits on a semaphore — the documented
    /// pattern that avoids deadlocking the launching STA thread. When it
    /// returns, Main returns and this process exits cleanly (no Kill needed).
    /// </summary>
    private static void RedirectActivationTo(
        AppInstance target, AppActivationArguments activation)
    {
        var done = new SemaphoreSlim(0, 1);
        Task.Run(async () =>
        {
            await target.RedirectActivationToAsync(activation);
            done.Release();
        });
        done.Wait();
    }
}
