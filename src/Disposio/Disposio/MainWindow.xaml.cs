using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Disposio.Views;
using Disposio.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.


namespace Disposio
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow() : this(false)
        {
        }

        public MainWindow(bool openDashboard)
        {
            this.InitializeComponent();
            // Ein Frame ist der Bereich, in dem WinUI zwischen Seiten navigiert.
            // Ein normaler Start beginnt immer beim Login. Nur das ausdrückliche Debug-
            // Startprofil darf eine als solche gekennzeichnete Entwicklungsansicht öffnen.
            var runtimeFrame = new Frame();
            BenutzerSitzung.Abmelden();
            bool openDevelopmentDashboard = false;
#if DEBUG
            if (openDashboard)
            {
                BenutzerSitzung.EntwicklungsansichtStarten();
                openDevelopmentDashboard = true;
            }
#endif
            runtimeFrame.Navigate(openDevelopmentDashboard ?  typeof(LoginPage) : typeof(DashboardAnsicht));
            this.Content = runtimeFrame;
        }
    }
}



