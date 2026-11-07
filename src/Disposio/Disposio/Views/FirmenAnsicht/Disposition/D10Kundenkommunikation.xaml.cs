using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Disposio.Views
{
    // D10 lädt beim Öffnen und jede Minute: Winston erzeugt dabei neue Vorschläge (KundenkommunikationRepository).
    // Bearbeitete Felder werden beim automatischen Neuladen nicht überschrieben. Startwert: optional eine Tour-Id
    // (aus D07 „Kundeninformation“ oder D01), dann wird deren erster Vorschlag gewählt.
    public sealed partial class D10Kundenkommunikation : UserControl
    {
        private static readonly TimeSpan Abstand = TimeSpan.FromMinutes(1);
        private const string StandardBetreff = "Information zu Ihrer Lieferung";

        private static readonly (string Code, string Name)[] Sprachauswahl =
        {
            ("de", "Deutsch"), ("en", "Englisch"), ("pl", "Polnisch")
        };

        private readonly FirmenAnsichtKontext _kontext;
        private readonly KundenkommunikationRepository _repository = new();
        private readonly DispatcherTimer _timer = new() { Interval = Abstand };
        private readonly long? _startTourId;

        private IReadOnlyList<Verspaetungsvorschlag> _vorschlaege = Array.Empty<Verspaetungsvorschlag>();
        private Verspaetungsvorschlag? _aktuell;
        private int _textversion;
        private bool _geaendert;     // Der Disponent hat etwas bearbeitet → beim Neuladen nicht überschreiben.
        private bool _fuellt;        // Felder werden gerade aus dem Vorschlag gefüllt (keine „Änderung“).
        private bool _arbeitet;
        // Die deutsche Fassung, zu der die angezeigte Übersetzung gehört. Weicht DeutschBox davon ab,
        // muss vor dem Senden neu übersetzt werden (sonst bekäme der Fahrer einen anderen Inhalt).
        private string _uebersetzteFassung = "";

        public D10Kundenkommunikation(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            if (kontext.Parameter is long tourId)
                _startTourId = tourId;

            _fuellt = true;
            SpracheAuswahl.ItemsSource = Sprachauswahl.Select(s => s.Name).ToList();
            _fuellt = false;

            _timer.Tick += async (_, _) => await LadenAsync(still: true);
            Loaded += async (_, _) =>
            {
                await LadenAsync(still: false);
                if (!_kontext.IstVorschau)
                    _timer.Start();
            };
            Unloaded += (_, _) => _timer.Stop();
        }

        // ── Laden ────────────────────────────────────────────────────────────────────────

        private async Task LadenAsync(bool still)
        {
            if (_kontext.IstVorschau)
            {
                StatusText.Text = "In der Vorschau ohne Anmeldung werden keine Vorschläge geladen.";
                return;
            }
            if (_arbeitet)
                return;

            _arbeitet = true;
            if (!still)
            {
                LadeAnzeige.IsActive = true;
                LadeAnzeige.Visibility = Visibility.Visible;
                StatusText.Text = "Winston prüft die ETAs …";
            }
            try
            {
                _vorschlaege = await _repository.AktualisierenUndLadenAsync();
                ListeAnzeigen();
            }
            catch (UnauthorizedAccessException)
            {
                _timer.Stop();
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                StatusText.Text = "Die Vorschläge konnten nicht geladen werden. Neuer Versuch in einer Minute.";
            }
            finally
            {
                _arbeitet = false;
                LadeAnzeige.IsActive = false;
                LadeAnzeige.Visibility = Visibility.Collapsed;
            }
        }

        private void ListeAnzeigen()
        {
            int anzahl = _vorschlaege.Count;
            StatusText.Text = anzahl switch
            {
                0 => "Keine offenen Vorschläge",
                1 => "1 offener Vorschlag",
                _ => $"{anzahl} offene Vorschläge"
            };
            LeerText.Visibility = anzahl == 0 ? Visibility.Visible : Visibility.Collapsed;
            Formular.Visibility = anzahl == 0 ? Visibility.Collapsed : Visibility.Visible;

            // Welcher Vorschlag? Der bisherige, sonst der erste der Start-Tour, sonst der erste.
            Verspaetungsvorschlag? ziel =
                _vorschlaege.FirstOrDefault(v => v.Id == _aktuell?.Id) ??
                _vorschlaege.FirstOrDefault(v => v.TourId == _startTourId) ??
                _vorschlaege.FirstOrDefault();

            if (_aktuell is not null && ziel?.Id != _aktuell.Id)
                StatusText.Text += " · Der bisher gewählte Vorschlag ist nicht mehr offen (erledigt oder von jemand anderem entschieden).";

            _fuellt = true;
            VorschlagAuswahl.ItemsSource = _vorschlaege.Select(Beschriftung).ToList();
            VorschlagAuswahl.Visibility = anzahl > 1 ? Visibility.Visible : Visibility.Collapsed;
            VorschlagAuswahl.SelectedIndex = ziel is null ? -1 : _vorschlaege.ToList().IndexOf(ziel);
            _fuellt = false;

            // Bearbeitete Felder bleiben stehen; nur Kopfzeilen (ETA-Hinweis) werden aufgefrischt.
            if (ziel is not null && (ziel.Id != _aktuell?.Id || !_geaendert))
                Fuellen(ziel);
            else if (ziel is not null)
                KopfAnzeigen(ziel);
            else
                _aktuell = null;
        }

        private static string Beschriftung(Verspaetungsvorschlag v) =>
            $"{v.Tournummer} · {v.Stopp} · {v.Kunde} · ETA {v.EtaSnapshot}";

        private void Fuellen(Verspaetungsvorschlag v)
        {
            _aktuell = v;
            _textversion = v.Textversion;
            _fuellt = true;
            EmpfaengerBox.Text = v.Kundenempfaenger;
            BetreffBox.Text = StandardBetreff;
            KundentextBox.Text = v.Kundentext;
            FahrerKanalBox.Text = $"{v.FahrerName} · Fahrer-App";
            int sprache = Array.FindIndex(Sprachauswahl, s => s.Code == v.Fahrersprache);
            SpracheAuswahl.SelectedIndex = sprache < 0 ? 0 : sprache;
            DeutschBox.Text = v.FahrertextOriginal;
            _uebersetzteFassung = v.FahrertextOriginal;
            UebersetzungAnzeigen(v.FahrertextUebersetzt);
            GrundBox.Text = "";
            _fuellt = false;
            _geaendert = false;
            FehlerText.Text = "";
            KopfAnzeigen(v);
        }

        private void KopfAnzeigen(Verspaetungsvorschlag v)
        {
            BegruendungText.Text = v.Begruendung;
            KontextText.Text = $"Tour {v.Tournummer} · {v.Stopp} · Sendung {v.Auftragsnummer} · {v.Kunde} (nur intern sichtbar)";
            BerechnungText.Text = $"Letzte Berechnung {v.EtaBerechnetAm ?? "–"} · Empfänger und ETA vor Freigabe prüfen.";
            // Hat sich die ETA seit dem Vorschlag deutlich verändert, steht im Text eine falsche Uhrzeit.
            bool abweichung = Math.Abs(v.AbweichungMinuten) >= 5 && v.AktuelleEta is not null;
            EtaWarnung.Text = abweichung
                ? $"Achtung: Die aktuelle ETA ist {v.AktuelleEta} ({(v.AbweichungMinuten > 0 ? "+" : "")}{v.AbweichungMinuten} Min. gegenüber dem Vorschlag). Uhrzeit in beiden Texten anpassen."
                : "";
            EtaWarnung.Visibility = abweichung ? Visibility.Visible : Visibility.Collapsed;
        }

        // Fehlt die Übersetzung (DeepL gerade nicht erreichbar), bleibt das Feld leer: Beim Senden wird sie nachgeholt.
        private void UebersetzungAnzeigen(string? uebersetzung)
        {
            string sprache = AktuelleSprache();
            if (sprache == "de")
            {
                FahrertextBox.Text = DeutschBox.Text;
                FahrerHinweis.Text = "Der Fahrer spricht Deutsch – keine Übersetzung nötig.";
            }
            else
            {
                FahrertextBox.Text = uebersetzung ?? "";
                FahrerHinweis.Text = uebersetzung is null
                    ? "Übersetzung gerade nicht möglich. „Neu übersetzen“ versuchen – sonst wird beim Senden automatisch übersetzt."
                    : "Vorschau in Empfängersprache.";
            }
        }

        private string AktuelleSprache() =>
            SpracheAuswahl.SelectedIndex is int i && i >= 0 ? Sprachauswahl[i].Code : "de";

        // ── Bedienung ────────────────────────────────────────────────────────────────────

        private async void Aktualisieren_Click(object sender, RoutedEventArgs e) => await LadenAsync(still: false);

        private void VorschlagAuswahl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_fuellt || VorschlagAuswahl.SelectedIndex < 0 || VorschlagAuswahl.SelectedIndex >= _vorschlaege.Count)
                return;
            Fuellen(_vorschlaege[VorschlagAuswahl.SelectedIndex]); // Wechsel verwirft ungesendete Änderungen
        }

        private void Feld_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_fuellt)
                _geaendert = true;
        }

        private void Ausgangsfassung_Click(object sender, RoutedEventArgs e)
        {
            bool offen = AusgangsfassungBereich.Visibility != Visibility.Visible;
            AusgangsfassungBereich.Visibility = offen ? Visibility.Visible : Visibility.Collapsed;
            AusgangsfassungKnopf.Content = offen ? "▼ Deutsche Ausgangsfassung" : "▶ Deutsche Ausgangsfassung";
        }

        private void VerwerfenAufklappen_Click(object sender, RoutedEventArgs e)
        {
            bool offen = VerwerfenBereich.Visibility != Visibility.Visible;
            VerwerfenBereich.Visibility = offen ? Visibility.Visible : Visibility.Collapsed;
            VerwerfenKnopf.Content = offen ? "▼ Vorschlag verwerfen" : "▶ Vorschlag verwerfen";
        }

        private async void Sprache_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_fuellt)
                await NeuUebersetzenAsync();
        }

        private async void NeuUebersetzen_Click(object sender, RoutedEventArgs e) => await NeuUebersetzenAsync();

        // Deutsche Fassung + Sprache speichern und übersetzen lassen.
        private async Task NeuUebersetzenAsync()
        {
            if (_aktuell is not { } v || _arbeitet)
                return;
            _arbeitet = true;
            NeuUebersetzenKnopf.IsEnabled = false;
            FehlerText.Text = "";
            FahrerHinweis.Text = "Wird übersetzt …";
            try
            {
                var (version, uebersetzung) = await _repository.FahrertextNeuUebersetzenAsync(v.Id, DeutschBox.Text, AktuelleSprache());
                _textversion = version;
                _uebersetzteFassung = DeutschBox.Text;
                _fuellt = true;
                UebersetzungAnzeigen(uebersetzung);
                _fuellt = false;
                _geaendert = true;
            }
            catch (ArgumentException fehler)
            {
                FehlerText.Text = fehler.Message;
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                FehlerText.Text = "Die Übersetzung konnte nicht gespeichert werden. Bitte erneut versuchen.";
            }
            finally
            {
                _arbeitet = false;
                NeuUebersetzenKnopf.IsEnabled = true;
            }
        }

        private void Abbrechen_Click(object sender, RoutedEventArgs e)
        {
            if (_aktuell is { } v)
                Fuellen(v); // Änderungen verwerfen, Vorschlag bleibt offen
            StatusText.Text = "Änderungen verworfen. Der Vorschlag bleibt offen.";
        }

        private async void Freigeben_Click(object sender, RoutedEventArgs e)
        {
            if (_aktuell is not { } v || _arbeitet)
                return;
            string sprache = AktuelleSprache();
            if (sprache != "de" && DeutschBox.Text.Trim() != _uebersetzteFassung.Trim())
            {
                FehlerText.Text = "Die deutsche Ausgangsfassung wurde geändert. Bitte zuerst „Neu übersetzen“, damit der Fahrer denselben Inhalt bekommt.";
                return;
            }
            string empfaenger = EmpfaengerBox.Text.Trim();
            _arbeitet = true;
            FreigebenKnopf.IsEnabled = false;
            FehlerText.Text = "";
            try
            {
                await _repository.FreigebenAsync(new VorschlagFreigabe(
                    v.Id, _textversion, EmpfaengerBox.Text, BetreffBox.Text, KundentextBox.Text,
                    sprache == "de" ? FahrertextBox.Text : DeutschBox.Text,
                    sprache == "de" ? null : FahrertextBox.Text,
                    sprache), v.Tournummer);
                _aktuell = null;
                _geaendert = false;
                _arbeitet = false;
                await LadenAsync(still: true);
                StatusText.Text = $"Freigegeben: E-Mail an {empfaenger} (simuliert, im Aktionsprotokoll) und Nachricht an den Fahrer gesendet. · "
                                  + StatusText.Text;
            }
            catch (ArgumentException fehler)
            {
                FehlerText.Text = fehler.Message;
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                FehlerText.Text = "Die Freigabe konnte nicht gespeichert werden. Es wurde nichts gesendet – bitte erneut versuchen.";
            }
            finally
            {
                _arbeitet = false;
                FreigebenKnopf.IsEnabled = true;
            }
        }

        private async void Verwerfen_Click(object sender, RoutedEventArgs e)
        {
            if (_aktuell is not { } v || _arbeitet)
                return;
            _arbeitet = true;
            FehlerText.Text = "";
            try
            {
                await _repository.VerwerfenAsync(v.Id, _textversion, GrundBox.Text);
                _aktuell = null;
                _geaendert = false;
                VerwerfenBereich.Visibility = Visibility.Collapsed;
                VerwerfenKnopf.Content = "▶ Vorschlag verwerfen";
                _arbeitet = false;
                await LadenAsync(still: true);
                StatusText.Text = "Vorschlag verworfen, Grund gespeichert. Es wurde nichts gesendet. · " + StatusText.Text;
            }
            catch (ArgumentException fehler)
            {
                FehlerText.Text = fehler.Message;
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                FehlerText.Text = "Das Verwerfen konnte nicht gespeichert werden. Bitte erneut versuchen.";
            }
            finally
            {
                _arbeitet = false;
            }
        }
    }
}
