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
using System.Threading.Tasks;
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
        // Das offene Fenster, damit die Seiten beim Wechsel Hell/Dunkel auch die Titelleiste umfärben können.
        private static MainWindow? _aktuell;

        /// <summary>
        /// Färbt die Windows-Titelleiste (mit Minimieren/Maximieren/Schließen) passend zur App:
        /// hell weiß, dunkel wie die obere Leiste der App. So gibt es im Dark Mode keinen weißen Balken mehr.
        /// </summary>
        public static void TitelleisteFaerben(bool hell)
        {
            if (_aktuell?.AppWindow?.TitleBar is not { } leiste)
                return;
            try
            {
                Windows.UI.Color hintergrund = Farbe(hell ? 0xFFFFFF : 0x1B2129);
                Windows.UI.Color text = Farbe(hell ? 0x1F2933 : 0xE6EAF0);
                Windows.UI.Color leise = Farbe(hell ? 0x6B7280 : 0x9AA3B0);
                Windows.UI.Color hover = Farbe(hell ? 0xF0F1F3 : 0x232A33);
                Windows.UI.Color gedrueckt = Farbe(hell ? 0xE3E6EB : 0x2E3641);

                leiste.BackgroundColor = hintergrund;
                leiste.ForegroundColor = text;
                leiste.InactiveBackgroundColor = hintergrund;
                leiste.InactiveForegroundColor = leise;
                leiste.ButtonBackgroundColor = hintergrund;
                leiste.ButtonForegroundColor = text;
                leiste.ButtonInactiveBackgroundColor = hintergrund;
                leiste.ButtonInactiveForegroundColor = leise;
                leiste.ButtonHoverBackgroundColor = hover;
                leiste.ButtonHoverForegroundColor = text;
                leiste.ButtonPressedBackgroundColor = gedrueckt;
                leiste.ButtonPressedForegroundColor = text;
            }
            catch (Exception)
            {
                // Ältere Windows-Versionen erlauben keine eigenen Farben. Dann bleibt die Standard-Titelleiste.
            }
        }

        private static Windows.UI.Color Farbe(int rgb) => Microsoft.UI.ColorHelper.FromArgb(
            0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        public MainWindow() : this(StartAnsicht.Anmeldung)
        {
        }

        public MainWindow(StartAnsicht start)
        {
            this.InitializeComponent();
            _aktuell = this;
            TitelleisteFaerben(DarstellungSpeicher.IstHell);
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

            // Fenster schließen zählt auch als Abmeldung (für D01 „Seit Ihrer letzten Schicht“).
            // Höchstens 3 Sekunden warten, damit das Schließen nie hängt. Task.Run: kein Warten auf dem UI-Thread-Kontext.
            Closed += (_, _) =>
            {
                if (BenutzerSitzung.AktuellerBenutzer is { } benutzer)
                    Task.Run(() => TagesuebersichtRepository.AbmeldungSpeichernAsync(benutzer.Id)).Wait(TimeSpan.FromSeconds(3));
            };
        }
    }
}



