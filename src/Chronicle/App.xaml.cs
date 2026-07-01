using Chronicle.Data;
using Microsoft.UI.Xaml;
using System.IO;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Chronicle
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>

        protected override async void OnLaunched(
            LaunchActivatedEventArgs args)
        {
            // The app owns where the database lives; AppDatabase (in
            // Chronicle.Core) stays free of any Windows.Storage dependency.
            AppDatabase.Initialize(
                Path.Combine(
                    ApplicationData.Current.LocalFolder.Path,
                    "chronicle.db"));

            _window = new MainWindow();
    
            _window.Activate();
        }
    }
}
