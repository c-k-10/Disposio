using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Disposio
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
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // Die Startprofile für Dashboard und Firmenansicht sind Vorschauen für die Entwicklung.
            // Im fertigen Release beginnt auch ein Aufruf mit --dashboard oder --firmenansicht
            // immer bei der Anmeldung.
            StartAnsicht start = StartAnsicht.Anmeldung;
#if DEBUG
            string[] arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (arguments.Any(argument => string.Equals(argument, "--dashboard", StringComparison.OrdinalIgnoreCase)))
                start = StartAnsicht.DashboardVorschau;
            else if (arguments.Any(argument => string.Equals(argument, "--firmenansicht", StringComparison.OrdinalIgnoreCase)))
                start = StartAnsicht.FirmenVorschau;
#endif

            _window = new MainWindow(start);
            // Icon in der Titelleiste: der Kopf des Maskottchens (Assets/disposio.ico).
            _window.AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "disposio.ico"));
            _window.Activate();

            // Die App startet maximiert (wie ein Doppelklick auf die Titelleiste). Titelleiste und Taskleiste
            // bleiben sichtbar, das Fenster lässt sich weiter verkleinern.
            if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
        }
    }
}
