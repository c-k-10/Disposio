using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Disposio.Views
{
    /// <summary>
    /// Hover- und Auswahl-Effekt für Tabellenzeilen in einem ItemsControl (z. B. D02 Sendungen, D04 Touren).
    /// In der Zeilenvorlage an eine Border hängen:
    /// <code>&lt;Border local:ListenZeile.Aktiv="True" local:ListenZeile.Markiert="{x:Bind IstMarkiert}" BorderThickness="4,0,0,0"&gt;</code>
    /// Maus darüber: leichte Akzentfarbe im Hintergrund. Markiert: kräftigerer Hintergrund und links ein
    /// Akzentbalken (der linke Rand der Border). Die Farbe kommt immer aus der aktuellen Akzentfarbe der Firma.
    /// </summary>
    public static class ListenZeile
    {
        public static readonly DependencyProperty AktivProperty = DependencyProperty.RegisterAttached(
            "Aktiv", typeof(bool), typeof(ListenZeile), new PropertyMetadata(false, AktivGeaendert));

        public static readonly DependencyProperty MarkiertProperty = DependencyProperty.RegisterAttached(
            "Markiert", typeof(bool), typeof(ListenZeile), new PropertyMetadata(false, (o, _) => Aktualisieren(o as Border)));

        // Merkt sich, ob die Maus gerade über der Zeile ist.
        private static readonly DependencyProperty UeberProperty = DependencyProperty.RegisterAttached(
            "Ueber", typeof(bool), typeof(ListenZeile), new PropertyMetadata(false));

        public static bool GetAktiv(DependencyObject element) => (bool)element.GetValue(AktivProperty);
        public static void SetAktiv(DependencyObject element, bool wert) => element.SetValue(AktivProperty, wert);
        public static bool GetMarkiert(DependencyObject element) => (bool)element.GetValue(MarkiertProperty);
        public static void SetMarkiert(DependencyObject element, bool wert) => element.SetValue(MarkiertProperty, wert);

        /// <summary>Sucht von einem Element in der Zeile (z. B. der CheckBox) die Zeilen-Border und setzt die Markierung.</summary>
        public static void MarkierenFuer(DependencyObject? elementInZeile, bool markiert)
        {
            if (ZeileVon(elementInZeile) is { } zeile)
                SetMarkiert(zeile, markiert);
        }

        public static Border? ZeileVon(DependencyObject? element)
        {
            while (element is not null)
            {
                if (element is Border border && GetAktiv(border))
                    return border;
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        private static void AktivGeaendert(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (element is not Border zeile || e.NewValue is not true)
                return;
            zeile.PointerEntered += (_, _) => Ueber(zeile, true);
            zeile.PointerExited += (_, _) => Ueber(zeile, false);
            zeile.PointerCanceled += (_, _) => Ueber(zeile, false);
            zeile.PointerCaptureLost += (_, _) => Ueber(zeile, false);
            Aktualisieren(zeile);
        }

        private static void Ueber(Border zeile, bool ueber)
        {
            zeile.SetValue(UeberProperty, ueber);
            Aktualisieren(zeile);
        }

        private static void Aktualisieren(Border? zeile)
        {
            if (zeile is null || !GetAktiv(zeile))
                return;
            bool markiert = GetMarkiert(zeile);
            bool ueber = (bool)zeile.GetValue(UeberProperty);
            Color akzent = Application.Current.Resources["AccentBrush"] is SolidColorBrush brush ? brush.Color : Colors.SteelBlue;

            double deckkraft = markiert ? (ueber ? 0.24 : 0.16) : (ueber ? 0.09 : 0);
            // Transparent statt null, damit die Zeile auch auf leeren Flächen die Maus bemerkt.
            zeile.Background = new SolidColorBrush(ColorHelper.FromArgb((byte)(deckkraft * 255), akzent.R, akzent.G, akzent.B));
            zeile.BorderBrush = new SolidColorBrush(markiert ? akzent : Colors.Transparent);
        }
    }
}
