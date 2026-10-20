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
    // Womit das Fenster startet. Die beiden Vorschauen gibt es nur im Debug-Build.
    public enum StartAnsicht
    {
        Anmeldung,
        DashboardVorschau,
        FirmenVorschau
    }

    public sealed partial class MainWindow : Window
    {
        public MainWindow() : this(StartAnsicht.Anmeldung)
        {
        }

        public MainWindow(StartAnsicht start)
        {
            this.InitializeComponent();
            // Ein Frame ist der Bereich, in dem WinUI zwischen Seiten navigiert.
            // Ein normaler Start beginnt immer beim Login. Nur die ausdrücklichen Debug-
            // Startprofile dürfen eine als solche gekennzeichnete Entwicklungsansicht öffnen.
            var runtimeFrame = new Frame();
            BenutzerSitzung.Abmelden();
            Type startPage = typeof(LoginPage);
#if DEBUG
            if (start == StartAnsicht.DashboardVorschau)
            {
                BenutzerSitzung.EntwicklungsansichtStarten();
                startPage = typeof(DashboardAnsicht);
            }
            else if (start == StartAnsicht.FirmenVorschau)
            {
                BenutzerSitzung.FirmenVorschauStarten();
                startPage = typeof(FirmenAnsicht);
            }
#endif
            runtimeFrame.Navigate(startPage);
            this.Content = runtimeFrame;
        }
    }
}



