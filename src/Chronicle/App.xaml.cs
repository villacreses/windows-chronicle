using Chronicle.Data;
using Chronicle.Projection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Storage;

namespace Chronicle
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>The running app, for <see cref="Program"/> to route
        /// redirected activations into.</summary>
        public static App? Instance { get; private set; }

        private MainWindow? _window;

        public App()
        {
            Instance = this;
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // The app owns where the database lives; AppDatabase (in
            // Chronicle.Core) stays free of any Windows.Storage dependency.
            AppDatabase.Initialize(
                Path.Combine(
                    ApplicationData.Current.LocalFolder.Path,
                    "chronicle.db"));

            _window = new MainWindow();
            _window.Activate();

            // Cold launch: GetActivatedEventArgs() is valid on this (UI) thread,
            // so pull the toast argument out here before handing off. A normal
            // launch yields null and is a no-op; a cold toast-click deep-links.
            HandleActivation(
                ExtractToastArgument(AppInstance.GetCurrent().GetActivatedEventArgs()));
        }

        /// <summary>
        /// Invoked by <see cref="Program"/> (the primary instance) when a
        /// later launch redirects its activation here — e.g. a toast clicked
        /// while Chronicle is already open. Runs on a BACKGROUND thread.
        ///
        /// The WinRT activation object has thread affinity: marshaling it raw
        /// to the UI thread and then reading <c>args.Data</c> there throws a
        /// COMException (RPC_E_WRONG_THREAD). So decode the plain argument
        /// string HERE, where the object is valid, and marshal only that
        /// string onto the UI thread.
        /// </summary>
        public void OnRedirectedActivation(AppActivationArguments args)
        {
            string? toastArgument = ExtractToastArgument(args);
            _window?.DispatcherQueue.TryEnqueue(() => HandleActivation(toastArgument));
        }

        /// <summary>
        /// Pulls the reminder launch argument out of an activation, or null if
        /// it is not a toast activation. MUST be called on the thread that owns
        /// <paramref name="args"/> — the WinRT COM object cannot cross
        /// apartments (see <see cref="OnRedirectedActivation"/>).
        /// </summary>
        private static string? ExtractToastArgument(AppActivationArguments args)
        {
            if (args.Kind != ExtendedActivationKind.ToastNotification)
                return null;

            var toastArgs = args.Data
                as Windows.ApplicationModel.Activation.ToastNotificationActivatedEventArgs;
            return toastArgs?.Argument;
        }

        /// <summary>
        /// Translates an OS activation into Chronicle's navigation model. Thin
        /// by design: focus the window, and if a reminder payload is present,
        /// decode it and hand off to the deep-link. Runs on the UI thread. No
        /// scheduling, reminder, or recurrence knowledge lives here — this is
        /// orchestration only.
        /// </summary>
        private void HandleActivation(string? toastArgument)
        {
            FocusWindow();

            var decoded = ReminderActivationPayload.TryDecode(toastArgument);
            if (decoded is null || _window is null)
                return;

            _ = _window.DeepLinkToReminderAsync(decoded.Value.Ref);
        }

        private void FocusWindow()
        {
            if (_window is null)
                return;
            try
            {
                // A window owned by a background process won't come forward from
                // AppWindow.Show()/Activate() alone — Windows blocks that. This
                // path runs on the PRIMARY instance after a toast click was
                // redirected here, so restore it if minimized and explicitly
                // pull it to the foreground.
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                if (IsIconic(hwnd))
                    ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            catch
            {
                // Focus is best-effort; Activate() below still surfaces it.
            }
            _window.Activate();
        }

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);
    }
}
