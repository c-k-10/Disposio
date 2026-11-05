using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Disposio.Views
{
    /// <summary>Was F05 tun soll. F04 übergibt das als Startwert.</summary>
    public enum WartungFormularModus
    {
        /// <summary>#89 Neuen Wartungstermin anlegen.</summary>
        TerminPlanen,
        /// <summary>Einen offenen Termin als durchgeführt eintragen.</summary>
        Erledigen,
        /// <summary>Eine durchgeführte Wartung ohne vorherigen Termin eintragen.</summary>
        Nachtragen
    }

    /// <summary>Startwert für F05: Modus, beim Erledigen die Termin-Id, sonst optional ein vorgewähltes Fahrzeug.</summary>
    public sealed record WartungFormularStart(WartungFormularModus Modus, long? WartungId, long? FahrzeugId);

    // F05 prüft, ob alles ausgefüllt und als Zahl lesbar ist; Wertebereiche prüft danach das WartungRepository.
    public sealed partial class F05WartungFormular : UserControl
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        private readonly FirmenAnsichtKontext _kontext;
        private readonly IWartungRepository _repository = new WartungRepository();
        private readonly WartungFormularStart _start;
        // Beim Erledigen: der Termin, der gerade bearbeitet wird.
        private Wartungstermin? _termin;
        private bool _speichert;

        public F05WartungFormular(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            _start = kontext.Parameter as WartungFormularStart
                ?? new WartungFormularStart(WartungFormularModus.TerminPlanen, null, null);

            foreach (string art in WartungsRegeln.Wartungsarten)
                ArtBox.Items.Add(new ComboBoxItem { Content = art, Tag = art });

            bool durchfuehrung = _start.Modus != WartungFormularModus.TerminPlanen;
            TerminBereich.Visibility = durchfuehrung ? Visibility.Collapsed : Visibility.Visible;
            DurchfuehrungBereich.Visibility = durchfuehrung ? Visibility.Visible : Visibility.Collapsed;
            DatumPicker.Date = new DateTimeOffset(WartungsRegeln.Heute().ToDateTime(TimeOnly.MinValue));
            DatumPicker.MaxDate = DatumPicker.Date.Value;

            switch (_start.Modus)
            {
                case WartungFormularModus.TerminPlanen:
                    FormularTitel.Text = "Wartungstermin anlegen";
                    SpeichernButton.Content = "Termin speichern";
                    HinweisAnzeige.Text = "Pflichtfelder sind mit * markiert. Ist zusätzlich ein Kilometerstand angegeben, " +
                        "gilt die Wartung als fällig, sobald das Datum ODER der Kilometerstand erreicht ist.";
                    break;
                case WartungFormularModus.Erledigen:
                    FormularTitel.Text = "Wartung als erledigt eintragen";
                    SpeichernButton.Content = "Wartung eintragen";
                    AuswahlBereich.Visibility = Visibility.Collapsed;
                    // Erst speichern, wenn der Termin geladen ist.
                    SpeichernButton.IsEnabled = false;
                    HinweisAnzeige.Text = DurchfuehrungHinweis;
                    break;
                default:
                    FormularTitel.Text = "Durchgeführte Wartung eintragen";
                    SpeichernButton.Content = "Wartung eintragen";
                    HinweisAnzeige.Text = DurchfuehrungHinweis;
                    break;
            }

            Loaded += async (_, _) => await LadenAsync();
        }

        private const string DurchfuehrungHinweis =
            "Pflichtfelder sind mit * markiert. Ist der Kilometerstand höher als der gespeicherte, wird er beim Fahrzeug " +
            "übernommen. Steht das Fahrzeug auf „Wartung“, setzen Sie es danach in der Fuhrparkübersicht wieder auf „Verfügbar“.";

        private async Task LadenAsync()
        {
            if (_kontext.IstVorschau)
                return;

            try
            {
                if (_start.Modus == WartungFormularModus.Erledigen)
                {
                    if (_start.WartungId is not long wartungId || await _repository.TerminLadenAsync(wartungId) is not Wartungstermin termin)
                    {
                        Fehler("Der Termin ist nicht mehr offen. Bitte gehen Sie zurück und laden Sie die Wartungen neu.", null);
                        return;
                    }

                    _termin = termin;
                    TerminInfo.Text = $"{termin.Wartungsart} · Fahrzeug {termin.Kennzeichen} · geplant für " +
                        termin.Termin.ToString("dd.MM.yyyy", Deutsch);
                    TerminInfo.Visibility = Visibility.Visible;
                    FormularTitel.Text = $"{termin.Wartungsart} für {termin.Kennzeichen} als erledigt eintragen";
                    KilometerBox.Text = termin.AktuellerKm.ToString("N0", Deutsch);
                    BemerkungBox.Text = termin.Bemerkung;
                    SpeichernButton.IsEnabled = true;
                    return;
                }

                // Neue Termine und Nachträge nur für aktive Fahrzeuge.
                foreach (WartungFahrzeug fahrzeug in (await _repository.FahrzeugeLadenAsync()).Where(f => f.Aktiv))
                {
                    FahrzeugBox.Items.Add(new ComboBoxItem { Content = fahrzeug.Kennzeichen, Tag = fahrzeug });
                    if (fahrzeug.Id == _start.FahrzeugId)
                        FahrzeugBox.SelectedIndex = FahrzeugBox.Items.Count - 1;
                }
                if (FahrzeugBox.Items.Count == 0)
                    Fehler("Es gibt kein aktives Fahrzeug. Legen Sie zuerst in der Fuhrparkübersicht ein Fahrzeug an.", null);
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                Fehler("Die Daten konnten nicht geladen werden. Bitte prüfen Sie die Verbindung und öffnen Sie das Formular erneut.", null);
            }
        }

        // Beim Nachtragen den gespeicherten Kilometerstand als Vorschlag eintragen, solange das Feld leer ist.
        private void Fahrzeug_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_start.Modus == WartungFormularModus.Nachtragen && string.IsNullOrWhiteSpace(KilometerBox.Text) &&
                (FahrzeugBox.SelectedItem as ComboBoxItem)?.Tag is WartungFahrzeug fahrzeug)
                KilometerBox.Text = fahrzeug.Kilometerstand.ToString("N0", Deutsch);
        }

        private void Abbrechen_Click(object sender, RoutedEventArgs e)
        {
            if (!_speichert)
                _kontext.Oeffnen("F04", _start.FahrzeugId);
        }

        private async void Speichern_Click(object sender, RoutedEventArgs e)
        {
            if (_speichert)
                return;

            FehlerAnzeige.Visibility = Visibility.Collapsed;
            _speichert = true;
            SpeichernButton.IsEnabled = AbbrechenButton.IsEnabled = false;
            object? inhalt = SpeichernButton.Content;
            SpeichernButton.Content = "Speichert …";
            try
            {
                string? meldung = _start.Modus == WartungFormularModus.TerminPlanen
                    ? await TerminSpeichernAsync()
                    : await DurchfuehrungSpeichernAsync();
                // Erst nach der Bestätigung aus Neon zurück zur Liste, mit Erfolgsmeldung.
                if (meldung is not null)
                    _kontext.Oeffnen("F04", meldung);
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (ArgumentException exception)
            {
                // Diese Meldungen stammen aus unserer eigenen Prüfung im WartungRepository.
                Fehler(exception.Message, null);
            }
            catch (InvalidOperationException) when (_kontext.IstVorschau)
            {
                Fehler("Die Eingaben sind gültig. In der Vorschau ohne Anmeldung wird aber nichts gespeichert.", null);
            }
            catch (Exception)
            {
                Fehler("Speichern konnte nicht bestätigt werden. Ihre Eingaben bleiben erhalten. Bitte prüfen Sie die " +
                    "Verbindung und laden Sie die Wartungen neu, bevor Sie es erneut versuchen.", null);
            }
            finally
            {
                _speichert = false;
                SpeichernButton.IsEnabled = AbbrechenButton.IsEnabled = true;
                SpeichernButton.Content = inhalt;
            }
        }

        // Null heißt: Eine Eingabe fehlt, die Meldung steht schon im Formular.
        private async Task<string?> TerminSpeichernAsync()
        {
            if (Fahrzeug() is not WartungFahrzeug fahrzeug)
                return Fehler("Bitte ein Fahrzeug auswählen.", FahrzeugBox);
            if (Art() is not string art)
                return Fehler("Bitte eine Wartungsart auswählen.", ArtBox);
            if (TerminPicker.Date is not DateTimeOffset termin)
                return Fehler("Bitte den Termin wählen.", TerminPicker);
            int? faelligKm = null;
            if (!string.IsNullOrWhiteSpace(FaelligKmBox.Text))
            {
                if (!GanzzahlLesen(FaelligKmBox.Text, out int km))
                    return Fehler("Bitte den Kilometerstand als ganze Zahl eingeben, zum Beispiel 250.000.", FaelligKmBox);
                faelligKm = km;
            }

            DateOnly tag = DateOnly.FromDateTime(termin.Date);
            await _repository.TerminAnlegenAsync(new WartungsterminEntwurf(fahrzeug.Id, art, tag, faelligKm, BemerkungBox.Text));
            return $"Termin „{art}“ für {fahrzeug.Kennzeichen} am {tag.ToString("dd.MM.yyyy", Deutsch)} wurde angelegt.";
        }

        private async Task<string?> DurchfuehrungSpeichernAsync()
        {
            long fahrzeugId;
            string kennzeichen;
            string art;
            if (_start.Modus == WartungFormularModus.Erledigen)
            {
                if (_termin is null)
                    return Fehler("Der Termin ist nicht geladen. Bitte gehen Sie zurück und öffnen Sie ihn erneut.", null);
                (fahrzeugId, kennzeichen, art) = (_termin.FahrzeugId, _termin.Kennzeichen, _termin.Wartungsart);
            }
            else
            {
                if (Fahrzeug() is not WartungFahrzeug fahrzeug)
                    return Fehler("Bitte ein Fahrzeug auswählen.", FahrzeugBox);
                if (Art() is not string gewaehlt)
                    return Fehler("Bitte eine Wartungsart auswählen.", ArtBox);
                (fahrzeugId, kennzeichen, art) = (fahrzeug.Id, fahrzeug.Kennzeichen, gewaehlt);
            }

            if (DatumPicker.Date is not DateTimeOffset datum)
                return Fehler("Bitte den Tag der Durchführung wählen.", DatumPicker);
            if (!GanzzahlLesen(KilometerBox.Text, out int kilometer))
                return Fehler("Bitte den Kilometerstand als ganze Zahl eingeben, zum Beispiel 184.250.", KilometerBox);
            decimal kosten = 0;
            if (!string.IsNullOrWhiteSpace(KostenBox.Text) && !KommazahlLesen(KostenBox.Text, out kosten))
                return Fehler("Bitte die Kosten in Euro eingeben, zum Beispiel 450,50.", KostenBox);
            int? folgeKm = null;
            if (!string.IsNullOrWhiteSpace(FolgeKmBox.Text))
            {
                if (!GanzzahlLesen(FolgeKmBox.Text, out int km))
                    return Fehler("Bitte den Kilometerstand des Folgetermins als ganze Zahl eingeben.", FolgeKmBox);
                folgeKm = km;
            }
            DateOnly? folge = FolgePicker.Date is DateTimeOffset folgeDatum ? DateOnly.FromDateTime(folgeDatum.Date) : null;

            await _repository.DurchfuehrungEintragenAsync(new WartungDurchfuehrung(
                _start.Modus == WartungFormularModus.Erledigen ? _termin!.Id : null,
                fahrzeugId, art, DateOnly.FromDateTime(datum.Date), kilometer, kosten, BemerkungBox.Text, folge, folgeKm));

            string text = $"„{art}“ für {kennzeichen} wurde in die Historie eingetragen.";
            if (folge is DateOnly tag)
                text += $" Folgetermin am {tag.ToString("dd.MM.yyyy", Deutsch)} ist angelegt.";
            return text;
        }

        private WartungFahrzeug? Fahrzeug() => (FahrzeugBox.SelectedItem as ComboBoxItem)?.Tag as WartungFahrzeug;

        private string? Art() => (ArtBox.SelectedItem as ComboBoxItem)?.Tag as string;

        private string? Fehler(string text, Control? feld)
        {
            FehlerAnzeige.Text = text;
            FehlerAnzeige.Visibility = Visibility.Visible;
            feld?.Focus(FocusState.Programmatic);
            return null;
        }

        // Ganze Zahl: Tausenderpunkte und Leerzeichen sind erlaubt, zum Beispiel „184.250“ (wie in F02).
        private static bool GanzzahlLesen(string text, out int wert)
        {
            string bereinigt = text.Replace(".", "").Replace(" ", "").Replace(" ", "").Trim();
            return int.TryParse(bereinigt, NumberStyles.None, CultureInfo.InvariantCulture, out wert);
        }

        // Betrag mit Komma oder Punkt („450,50“, „1.200,50“ oder „450.50“), höchstens zwei Nachkommastellen.
        private static bool KommazahlLesen(string text, out decimal wert)
        {
            string bereinigt = text.Replace("€", "").Replace(" ", "").Trim();
            // Mit Komma ist der Punkt ein Tausenderpunkt („1.200,50“).
            if (bereinigt.Contains(','))
                bereinigt = bereinigt.Replace(".", "").Replace(',', '.');
            return decimal.TryParse(bereinigt, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out wert)
                && decimal.Round(wert, 2) == wert;
        }
    }
}
