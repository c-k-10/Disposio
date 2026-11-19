using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Disposio.Views
{
    // Seitenleiste „Winston“: Gespräch mit dem KI-Co-Disponenten (WinstonKi) und Hinweise auf neue Verspätungen.
    // Die Firmenansicht legt sie beim ersten Öffnen an und behält sie, damit das Gespräch beim Wechsel der
    // Ansicht nicht verloren geht.
    public sealed partial class WinstonLeiste : UserControl
    {
        // Eine Antwort darf höchstens so lange dauern (mehrere Werkzeug-Runden).
        private static readonly TimeSpan Zeitlimit = TimeSpan.FromMinutes(3);

        private static readonly string[] Beispielfragen =
        {
            "Welche Touren sind gerade verspätet?",
            "Gibt es Sperrungen oder Staus auf den laufenden Touren?",
            "Was steht heute an?",
            "Welche Sendungen sind noch nicht verplant?"
        };

        private readonly WinstonKi _winston = new();
        private readonly WinstonWerkzeuge _werkzeuge = new();
        private readonly Action<string, object?> _oeffnen;
        private readonly Action _zugriffBeenden;
        private readonly Action _schliessen;
        private readonly StackPanel _beispiele = new() { Spacing = 6 };

        private CancellationTokenSource? _laeuft;
        // Zählt bei „Neues Gespräch“ hoch: Eine Antwort, die noch zum alten Gespräch gehört, wird nicht mehr angezeigt.
        private int _gespraech;

        /// <param name="oeffnen">Öffnet eine Ansicht, z. B. <c>oeffnen("D10", tourId)</c>.</param>
        /// <param name="zugriffBeenden">Die Sitzung ist beendet (Rolle entzogen): zurück zur Anmeldung.</param>
        /// <param name="schliessen">Blendet die Leiste aus.</param>
        public WinstonLeiste(Action<string, object?> oeffnen, Action zugriffBeenden, Action schliessen)
        {
            InitializeComponent();
            _oeffnen = oeffnen;
            _zugriffBeenden = zugriffBeenden;
            _schliessen = schliessen;
            Begruessen();
        }

        /// <summary>Beim Einblenden: prüfen, ob Winston eingerichtet ist, und den Cursor ins Eingabefeld setzen.</summary>
        public void Anzeigen()
        {
            string? hinweis = BenutzerSitzung.IstFirmenVorschau
                ? "In der Vorschau ohne Anmeldung gibt es keine Firmendaten. Winston kann hier nichts nachschlagen."
                : WinstonKi.IstEingerichtet ? null : WinstonKi.OhneSchluessel;
            EinrichtungText.Text = hinweis ?? "";
            EinrichtungHinweis.Visibility = hinweis is null ? Visibility.Collapsed : Visibility.Visible;
            Eingabe.Focus(FocusState.Programmatic);
        }

        /// <summary>
        /// Neue Verspätungsvorschläge (aus dem Hintergrund-Check der Firmenansicht): je Vorschlag ein Hinweis mit
        /// der Begründung von Winston und einem Knopf zur Kundenkommunikation, wo freigegeben oder verworfen wird.
        /// </summary>
        public void VerspaetungenMelden(IReadOnlyList<Verspaetungsvorschlag> neue)
        {
            foreach (Verspaetungsvorschlag v in neue.Take(5))
            {
                var inhalt = new StackPanel { Spacing = 8 };
                inhalt.Children.Add(new TextBlock
                {
                    Text = $"Verspätung · Tour {v.Tournummer} · {v.Stopp}",
                    Style = Stil("StatusZeileText")
                });
                inhalt.Children.Add(TextFeld(v.Begruendung));
                var knopf = new Button
                {
                    Content = "Vorschlag ansehen",
                    Style = Stil("DashboardButton"),
                    MinHeight = 36,
                    Padding = new Thickness(14, 4, 14, 4),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                long tourId = v.TourId;
                knopf.Click += (_, _) => _oeffnen("D10", tourId);
                inhalt.Children.Add(knopf);
                Hinzufuegen(new Border { Style = Stil("WarnZeile"), Child = inhalt });
            }

            if (neue.Count > 5)
                Hinzufuegen(Blase($"… und {neue.Count - 5} weitere. Alle stehen in der Kundenkommunikation.", vomDisponenten: false));
        }

        /// <summary>Stand der Hintergrundprüfung oben in der Leiste, z. B. „Zuletzt geprüft 09:32 · 1 offener Vorschlag“.</summary>
        public void PruefungMelden(int offen) => PruefStand.Text =
            $"Zuletzt nach Verspätungen gesucht: {DateTime.Now:HH:mm} · " +
            (offen == 1 ? "1 offener Vorschlag" : $"{offen} offene Vorschläge");

        /// <summary>Ein Problem der Hintergrundprüfung als Hinweis im Verlauf anzeigen.</summary>
        public void FehlerMelden(string text) => Hinzufuegen(Fehler(text));

        // ── Gespräch ─────────────────────────────────────────────────────────────────────

        private void Begruessen()
        {
            Nachrichten.Children.Clear();
            Nachrichten.Children.Add(Blase(
                "Hallo! Ich bin Winston, dein Co-Disponent. Frag mich nach Touren, Verspätungen, Sperrungen, Sendungen, " +
                "Fahrzeugen oder Meldungen. Verspätungen melde ich dir von selbst – freigeben oder verwerfen tust du.",
                vomDisponenten: false));

            _beispiele.Children.Clear();
            foreach (string frage in Beispielfragen)
            {
                var knopf = new Button
                {
                    Content = new TextBlock { Text = frage, FontSize = 13, TextWrapping = TextWrapping.Wrap },
                    Style = Stil("DashboardButton"),
                    MinHeight = 36,
                    Padding = new Thickness(12, 6, 12, 6),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left
                };
                knopf.Click += async (_, _) => await FragenAsync(frage);
                _beispiele.Children.Add(knopf);
            }
            Nachrichten.Children.Add(_beispiele);
        }

        private void NeuesGespraech_Click(object sender, RoutedEventArgs e)
        {
            _gespraech++;
            _laeuft?.Cancel();
            _winston.NeuesGespraech();
            Begruessen();
        }

        private void Schliessen_Click(object sender, RoutedEventArgs e) => _schliessen();

        private async void Fragen_Click(object sender, RoutedEventArgs e) => await FragenAsync(Eingabe.Text);

        private async void Eingabe_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter)
                return;
            e.Handled = true;
            await FragenAsync(Eingabe.Text);
        }

        private async Task FragenAsync(string frage)
        {
            frage = (frage ?? "").Trim();
            if (frage.Length == 0 || _laeuft is not null)
                return;

            int gespraech = _gespraech;
            Eingabe.Text = "";
            Nachrichten.Children.Remove(_beispiele);
            Hinzufuegen(Blase(frage, vomDisponenten: true));
            ArbeitetAnzeigen(true);
            var laeuft = _laeuft = new CancellationTokenSource(Zeitlimit);
            try
            {
                string antwort = await _winston.FragenAsync(frage, _werkzeuge, laeuft.Token);
                if (gespraech == _gespraech)
                    Hinzufuegen(Blase(antwort, vomDisponenten: false));
            }
            catch (WinstonException fehler)
            {
                if (gespraech == _gespraech)
                    Hinzufuegen(Fehler(fehler.Message));
            }
            catch (UnauthorizedAccessException)
            {
                _zugriffBeenden();
            }
            catch (Exception)
            {
                // Nichts Unerwartetes darf die App beenden.
                if (gespraech == _gespraech)
                    Hinzufuegen(Fehler("Winston hatte ein technisches Problem. Bitte noch einmal versuchen."));
            }
            finally
            {
                laeuft.Dispose();
                _laeuft = null;
                ArbeitetAnzeigen(false);
            }
        }

        private void ArbeitetAnzeigen(bool arbeitet)
        {
            Arbeitet.Visibility = arbeitet ? Visibility.Visible : Visibility.Collapsed;
            FragenKnopf.IsEnabled = !arbeitet;
            if (!arbeitet)
                Eingabe.Focus(FocusState.Programmatic);
        }

        // ── Bausteine ────────────────────────────────────────────────────────────────────

        private void Hinzufuegen(UIElement element)
        {
            Nachrichten.Children.Add(element);
            Verlauf.UpdateLayout();
            Verlauf.ChangeView(null, Verlauf.ScrollableHeight, null);
        }

        // Frage des Disponenten: rechts in der Akzentfarbe. Antwort von Winston: links in Grau.
        private static Border Blase(string text, bool vomDisponenten)
        {
            TextBlock inhalt = TextFeld(text);
            if (vomDisponenten)
                inhalt.Foreground = Pinsel("AccentTextBrush");
            return new Border
            {
                Background = Pinsel(vomDisponenten ? "AccentBrush" : "StatusBoxBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 10, 12, 10),
                MaxWidth = 310,
                HorizontalAlignment = vomDisponenten ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Child = inhalt
            };
        }

        private static Border Fehler(string text) => new()
        {
            Style = Stil("WarnZeile"),
            Child = new TextBlock { Text = text, Style = Stil("FehlerText"), IsTextSelectionEnabled = true }
        };

        private static TextBlock TextFeld(string text) => new()
        {
            Text = text,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = Pinsel("TextBrush")
        };

        private static SolidColorBrush Pinsel(string key) => (SolidColorBrush)Application.Current.Resources[key];

        private static Style Stil(string key) => (Style)Application.Current.Resources[key];
    }
}
