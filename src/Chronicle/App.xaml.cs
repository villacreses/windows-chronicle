using Chronicle.Data;
using Chronicle.Projection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.IO;
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

            // Handle the activation that launched THIS instance — a normal
            // launch is a no-op; a cold toast-click launch deep-links.
            HandleActivation(AppInstance.GetCurrent().GetActivatedEventArgs());
        }

        /// <summary>
        /// Invoked by <see cref="Program"/> (the primary instance) when a
        /// later launch redirects its activation here — e.g. a toast clicked
        /// while Chronicle is already open. Runs on a background thread, so
        /// marshal onto the UI thread.
        /// </summary>
        public void OnRedirectedActivation(AppActivationArguments args)
        {
            _window?.DispatcherQueue.TryEnqueue(() => HandleActivation(args));
        }

        /// <summary>
        /// Translates an OS activation into Chronicle's navigation model. Thin
        /// by design: focus the window, and if it is a reminder toast, decode
        /// the payload and hand off to the deep-link. No scheduling, reminder,
        /// or recurrence knowledge lives here — this is orchestration only.
        /// </summary>
        private void HandleActivation(AppActivationArguments args)
        {
            FocusWindow();

            if (args.Kind != ExtendedActivationKind.ToastNotification)
                return;

            var toastArgs = args.Data
                as Windows.ApplicationModel.Activation.ToastNotificationActivatedEventArgs;
            var decoded = ReminderActivationPayload.TryDecode(toastArgs?.Argument);
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
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id)?.Show();
            }
            catch
            {
                // Focus is best-effort; Activate() below still surfaces it.
            }
            _window.Activate();
        }
    }
}
