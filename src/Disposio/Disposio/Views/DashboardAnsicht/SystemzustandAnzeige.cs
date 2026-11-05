using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Disposio.Views
{
    /// <summary>
    /// „Systemzustand“ oben im Admin-Dashboard: eine Kachel je Prüfung (SystemzustandRepository) mit Ampel.
    /// Rot = kritisch, Gelb = Warnung, Grün = in Ordnung, Grau = nicht geprüft. Jede Kachel zeigt Symbol und Text,
    /// damit sie auch ohne Farbe verständlich ist. Klick auf eine Kachel zeigt Details und was zu tun ist.
    /// Prüft beim Öffnen und danach jede Minute. Wie AktionsprotokollAnsicht ganz im Code gebaut; die Farben
    /// kommen aus den Ressourcen des Dashboards, damit hell/dunkel und Akzentfarbe passen.
    /// </summary>
    public sealed class SystemzustandAnzeige : UserControl
    {
        private static readonly TimeSpan Abstand = TimeSpan.FromMinutes(1);
        private const string Symbolschrift = "Segoe Fluent Icons, Segoe MDL2 Assets";

        // Ampelfarben: auf hellem und dunklem Hintergrund gut erkennbar. Text bleibt in TextBrush (lesbar).
        private static readonly Color Rot = Color.FromArgb(0xFF, 0xE5, 0x48, 0x4D);
        private static readonly Color Gelb = Color.FromArgb(0xFF, 0xF5, 0xA5, 0x24);
        private static readonly Color Gruen = Color.FromArgb(0xFF, 0x30, 0xA4, 0x6C);
        private static readonly Color Grau = Color.FromArgb(0xFF, 0x8B, 0x93, 0xA1);

        private readonly Func<string, object> _ressource;
        private readonly Action _zugriffBeendet;
        private readonly SystemzustandRepository _repository = new();
        private readonly DispatcherTimer _timer = new() { Interval = Abstand };

        private readonly Border _rahmen = new();
        private readonly FontIcon _zusammenfassungSymbol = new() { FontFamily = new FontFamily(Symbolschrift), FontSize = 22 };
        private readonly TextBlock _zusammenfassung = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _stand = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
        private readonly ProgressRing _laden = new() { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed };
        private readonly Button _pruefenKnopf = new() { Content = "Jetzt prüfen" };
        private readonly Grid _kacheln = new() { ColumnSpacing = 14, RowSpacing = 14 };
        private readonly StackPanel _details = new() { Spacing = 8, Visibility = Visibility.Collapsed };

        private IReadOnlyList<Pruefergebnis> _ergebnisse = Array.Empty<Pruefergebnis>();
        private string? _offeneKachel;
        private bool _laeuft;

        /// <param name="ressource">Zugriff auf die Seitenressourcen des Dashboards (Brushes, Styles).</param>
        /// <param name="zugriffBeendet">Wird aufgerufen, wenn die Admin-Berechtigung fehlt.</param>
        public SystemzustandAnzeige(Func<string, object> ressource, Action zugriffBeendet)
        {
            _ressource = ressource;
            _zugriffBeendet = zugriffBeendet;
            Content = Aufbauen();

            _pruefenKnopf.Click += async (_, _) => await PruefenAsync();
            _timer.Tick += async (_, _) => await PruefenAsync();
            Loaded += async (_, _) =>
            {
                await PruefenAsync();
                _timer.Start();
            };
            Unloaded += (_, _) => _timer.Stop();
        }

        private Brush B(string schluessel) => (Brush)_ressource(schluessel);

        // ── Aufbau ───────────────────────────────────────────────────────────────────────

        private UIElement Aufbauen()
        {
            if (_ressource("DashboardButton") is Style knopfStil)
                _pruefenKnopf.Style = knopfStil;
            _zusammenfassung.Foreground = B("TextBrush");
            _stand.Foreground = B("MutedTextBrush");

            // Kopf: Symbol + Zusammenfassung links, Stand + „Jetzt prüfen“ rechts.
            var kopf = new Grid { ColumnSpacing = 14 };
            kopf.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            kopf.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            kopf.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _zusammenfassungSymbol.VerticalAlignment = VerticalAlignment.Center;
            kopf.Children.Add(_zusammenfassungSymbol);

            var titel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            titel.Children.Add(new TextBlock
            {
                Text = "SYSTEMZUSTAND", FontSize = 13, CharacterSpacing = 160, Foreground = B("MutedTextBrush")
            });
            titel.Children.Add(_zusammenfassung);
            Grid.SetColumn(titel, 1);
            kopf.Children.Add(titel);

            var rechts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
            _stand.VerticalAlignment = VerticalAlignment.Center;
            _laden.VerticalAlignment = VerticalAlignment.Center;
            rechts.Children.Add(_laden);
            rechts.Children.Add(_stand);
            rechts.Children.Add(_pruefenKnopf);
            Grid.SetColumn(rechts, 2);
            kopf.Children.Add(rechts);

            // Drei Kacheln pro Zeile.
            for (int i = 0; i < 3; i++)
                _kacheln.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var inhalt = new StackPanel { Spacing = 18 };
            inhalt.Children.Add(kopf);
            inhalt.Children.Add(_kacheln);
            inhalt.Children.Add(_details);

            _rahmen.Background = B("SurfaceBrush");
            _rahmen.BorderBrush = B("BorderBrush");
            _rahmen.BorderThickness = new Thickness(1);
            _rahmen.CornerRadius = new CornerRadius(18);
            _rahmen.Padding = new Thickness(26, 22, 26, 22);
            _rahmen.Child = inhalt;
            AutomationProperties.SetName(_rahmen, "Systemzustand");
            return _rahmen;
        }

        // ── Prüfen ───────────────────────────────────────────────────────────────────────

        private async Task PruefenAsync()
        {
            if (_laeuft || !IsLoaded)
                return;
            _laeuft = true;
            _pruefenKnopf.IsEnabled = false;
            _laden.IsActive = true;
            _laden.Visibility = Visibility.Visible;
            _stand.Text = "Wird geprüft …";
            try
            {
                using var abbruch = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                _ergebnisse = await _repository.AllePruefenAsync(abbruch.Token);
                Anzeigen();
                _stand.Text = $"Stand {DateTime.Now.ToString("HH:mm", CultureInfo.GetCultureInfo("de-DE"))} Uhr";
            }
            catch (UnauthorizedAccessException)
            {
                _timer.Stop();
                _zugriffBeendet();
            }
            catch (Exception)
            {
                _stand.Text = "Prüfung nicht möglich. Neuer Versuch in einer Minute.";
            }
            finally
            {
                _laeuft = false;
                _pruefenKnopf.IsEnabled = true;
                _laden.IsActive = false;
                _laden.Visibility = Visibility.Collapsed;
            }
        }

        private void Anzeigen()
        {
            int kritisch = _ergebnisse.Count(e => e.Stufe == Pruefstufe.Kritisch);
            int warnungen = _ergebnisse.Count(e => e.Stufe == Pruefstufe.Warnung);

            // Zusammenfassung; bei einem kritischen Problem wird der ganze Kasten rot umrandet.
            if (kritisch > 0)
            {
                _zusammenfassung.Text = (kritisch == 1 ? "1 kritisches Problem" : $"{kritisch} kritische Probleme")
                    + (warnungen > 0 ? $", {Warnungen(warnungen)}" : "");
                SetzeSymbol(_zusammenfassungSymbol, Pruefstufe.Kritisch);
                _rahmen.BorderBrush = new SolidColorBrush(Rot);
                _rahmen.BorderThickness = new Thickness(2);
                _rahmen.Background = Getoent(Rot, B("SurfaceBrush"));
            }
            else
            {
                _zusammenfassung.Text = warnungen > 0 ? Warnungen(warnungen) : "Alles in Ordnung";
                SetzeSymbol(_zusammenfassungSymbol, warnungen > 0 ? Pruefstufe.Warnung : Pruefstufe.Ok);
                _rahmen.BorderBrush = warnungen > 0 ? new SolidColorBrush(Gelb) : B("BorderBrush");
                _rahmen.BorderThickness = new Thickness(warnungen > 0 ? 2 : 1);
                _rahmen.Background = B("SurfaceBrush");
            }

            _kacheln.Children.Clear();
            _kacheln.RowDefinitions.Clear();
            for (int i = 0; i < _ergebnisse.Count; i++)
            {
                if (i % 3 == 0)
                    _kacheln.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var kachel = Kachel(_ergebnisse[i]);
                Grid.SetRow(kachel, i / 3);
                Grid.SetColumn(kachel, i % 3);
                _kacheln.Children.Add(kachel);
            }

            DetailsAnzeigen();
        }

        private static string Warnungen(int anzahl) => anzahl == 1 ? "1 Warnung" : $"{anzahl} Warnungen";

        // Eine Kachel: farbiger Balken links, Symbol, Name, Kurztext. Klick/Enter klappt die Details auf.
        private Border Kachel(Pruefergebnis e)
        {
            Color farbe = Farbe(e.Stufe);
            bool offen = _offeneKachel == e.Name;

            var symbol = new FontIcon { FontFamily = new FontFamily(Symbolschrift), FontSize = 18, VerticalAlignment = VerticalAlignment.Top };
            SetzeSymbol(symbol, e.Stufe);

            var texte = new StackPanel { Spacing = 3 };
            texte.Children.Add(new TextBlock
            {
                Text = e.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = B("TextBrush"), TextWrapping = TextWrapping.Wrap
            });
            texte.Children.Add(new TextBlock
            {
                Text = $"{StufeText(e.Stufe)} · {e.Kurztext}", FontSize = 14, Foreground = B("MutedTextBrush"), TextWrapping = TextWrapping.Wrap
            });

            var zeile = new Grid { ColumnSpacing = 12, Padding = new Thickness(14, 14, 16, 14) };
            zeile.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            zeile.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            zeile.Children.Add(symbol);
            Grid.SetColumn(texte, 1);
            zeile.Children.Add(texte);

            var balken = new Border { Width = 5, Background = new SolidColorBrush(farbe), CornerRadius = new CornerRadius(12, 0, 0, 12) };
            var innen = new Grid();
            innen.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            innen.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            innen.Children.Add(balken);
            Grid.SetColumn(zeile, 1);
            innen.Children.Add(zeile);

            var kachel = new Border
            {
                Child = innen,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(offen ? 2 : 1),
                BorderBrush = offen ? new SolidColorBrush(farbe) : B("BorderBrush"),
                Background = e.Stufe is Pruefstufe.Kritisch or Pruefstufe.Warnung ? Getoent(farbe, B("InputBrush")) : B("InputBrush"),
                IsTabStop = true,
                UseSystemFocusVisuals = true,
            };
            AutomationProperties.SetName(kachel, $"{e.Name}: {StufeText(e.Stufe)}, {e.Kurztext}. Für Details auswählen.");
            ToolTipService.SetToolTip(kachel, "Für Details klicken");

            void Umschalten()
            {
                _offeneKachel = offen ? null : e.Name;
                Anzeigen();
            }
            kachel.Tapped += (_, _) => Umschalten();
            kachel.KeyDown += (_, args) =>
            {
                if (args.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
                {
                    args.Handled = true;
                    Umschalten();
                }
            };
            return kachel;
        }

        // Details der aufgeklappten Kachel: Befunde und „Was tun?“.
        private void DetailsAnzeigen()
        {
            _details.Children.Clear();
            Pruefergebnis? e = _ergebnisse.FirstOrDefault(x => x.Name == _offeneKachel);
            if (e is null)
            {
                _details.Visibility = Visibility.Collapsed;
                return;
            }

            _details.Visibility = Visibility.Visible;
            _details.Children.Add(new TextBlock
            {
                Text = $"{e.Name} – {StufeText(e.Stufe)}", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = B("TextBrush")
            });
            var befunde = e.Details.Count > 0 ? e.Details : new[] { e.Kurztext };
            foreach (string befund in befunde)
            {
                _details.Children.Add(new TextBlock
                {
                    Text = $"•  {befund}", FontSize = 14, Foreground = B("TextBrush"), TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                });
            }
            if (e.Massnahme.Length > 0)
            {
                _details.Children.Add(new TextBlock
                {
                    Text = $"Was tun? {e.Massnahme}", FontSize = 14, Foreground = B("MutedTextBrush"),
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(0, 4, 0, 0)
                });
            }
        }

        // ── Farben und Symbole ───────────────────────────────────────────────────────────

        private static Color Farbe(Pruefstufe stufe) => stufe switch
        {
            Pruefstufe.Kritisch => Rot,
            Pruefstufe.Warnung => Gelb,
            Pruefstufe.Ok => Gruen,
            _ => Grau
        };

        private static string StufeText(Pruefstufe stufe) => stufe switch
        {
            Pruefstufe.Kritisch => "Kritisch",
            Pruefstufe.Warnung => "Warnung",
            Pruefstufe.Ok => "OK",
            _ => "Nicht geprüft"
        };

        // Segoe-Symbole: Häkchen (OK), Warndreieck (Warnung), Fehler-Kreis (Kritisch), Fragezeichen (nicht geprüft).
        private static void SetzeSymbol(FontIcon icon, Pruefstufe stufe)
        {
            icon.Glyph = stufe switch
            {
                Pruefstufe.Kritisch => "",
                Pruefstufe.Warnung => "",
                Pruefstufe.Ok => "",
                _ => ""
            };
            icon.Foreground = new SolidColorBrush(Farbe(stufe));
        }

        // Hintergrund leicht in der Ampelfarbe getönt (ca. 14 %), passend zu hell und dunkel.
        private static Brush Getoent(Color farbe, Brush grund)
        {
            Color basis = grund is SolidColorBrush s ? s.Color : Colors.Transparent;
            const double anteil = 0.14;
            byte Mischen(byte a, byte b) => (byte)Math.Round(a + (b - a) * anteil);
            return new SolidColorBrush(Color.FromArgb(0xFF, Mischen(basis.R, farbe.R), Mischen(basis.G, farbe.G), Mischen(basis.B, farbe.B)));
        }
    }
}
