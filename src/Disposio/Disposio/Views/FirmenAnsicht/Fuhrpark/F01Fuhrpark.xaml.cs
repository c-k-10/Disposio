using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Disposio.Views
{
    // F01 lädt die Fahrzeuge beim Öffnen. Die Rechteprüfung steckt im FahrzeugRepository:
    // Es liefert nur Fahrzeuge der Firma, für die das Konto gerade berechtigt ist.
    public sealed partial class F01Fuhrpark : UserControl
    {
        private readonly FirmenAnsichtKontext _kontext;
        private readonly IFahrzeugRepository _repository = new FahrzeugRepository();
        private CancellationTokenSource? _laden;
        private bool _laedt;
        // Läuft gerade „Inaktiv setzen“ oder „Aktivieren“? Dann zählt kein weiterer Klick.
        private bool _schaltet;
        // Nach dem Speichern in F02 kommt eine Erfolgsmeldung als Startwert. Sie erscheint nur einmal.
        private string? _meldung;
        // Alle zuletzt geladenen Fahrzeuge. Angezeigt werden davon die aktiven, auf Wunsch auch die inaktiven.
        private IReadOnlyList<Fahrzeug> _fahrzeuge = Array.Empty<Fahrzeug>();
        private bool _inaktiveAnzeigen;

        public F01Fuhrpark(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            _meldung = kontext.Parameter as string;
            Loaded += async (_, _) => await LadenAsync();
            // Wer die Ansicht verlässt, braucht das Ladeergebnis nicht mehr.
            Unloaded += (_, _) => _laden?.Cancel();
        }

        private async void NeuLaden_Click(object sender, RoutedEventArgs e) => await LadenAsync();

        private void Anlegen_Click(object sender, RoutedEventArgs e) => _kontext.Oeffnen("F02");

        // F02 bekommt die Fahrzeug-Id als Startwert und öffnet sich dann zum Bearbeiten.
        private void Bearbeiten_Click(object sender, RoutedEventArgs e)
        {
            if (!_schaltet && sender is Button { DataContext: FahrzeugZeile zeile })
                _kontext.Oeffnen("F02", zeile.Id);
        }

        private void InaktiveUmschalten_Click(object sender, RoutedEventArgs e)
        {
            _inaktiveAnzeigen = !_inaktiveAnzeigen;
            Anzeigen();
            StatusText.Text = Zusammenfassung();
        }

        private async void AktivUmschalten_Click(object sender, RoutedEventArgs e)
        {
            if (_laedt || _schaltet || XamlRoot is null || sender is not Button { DataContext: FahrzeugZeile zeile })
                return;

            // Schon vor dem Dialog sperren: Ein Doppelklick darf keinen zweiten Dialog öffnen.
            _schaltet = true;
            bool aktivieren = !zeile.Aktiv;
            try
            {
                if (!aktivieren)
                {
                    var bestaetigen = new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        RequestedTheme = RequestedTheme,
                        Title = $"Fahrzeug {zeile.Kennzeichen} inaktiv setzen?",
                        Content = "Das Fahrzeug wird nicht gelöscht. Touren, Wartungen und Dokumente bleiben ihm zugeordnet, " +
                            "und Sie können es jederzeit wieder aktivieren. In der Liste ist es danach ausgeblendet.",
                        PrimaryButtonText = "Inaktiv setzen",
                        CloseButtonText = "Abbrechen",
                        DefaultButton = ContentDialogButton.Close
                    };
                    if (await bestaetigen.ShowAsync() != ContentDialogResult.Primary)
                        return;
                }

                FahrzeugListe.IsEnabled = false;
                bool geaendert = await _repository.AktivSetzenAsync(zeile.Id, aktivieren);
                _meldung = !geaendert
                    ? "Das Fahrzeug wurde inzwischen geändert. Die Liste zeigt jetzt den aktuellen Stand."
                    : aktivieren
                        ? $"Fahrzeug {zeile.Kennzeichen} ist wieder aktiv."
                        : $"Fahrzeug {zeile.Kennzeichen} ist jetzt inaktiv.";
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
                return;
            }
            catch (Exception)
            {
                // Vielleicht ist die Änderung trotzdem angekommen. Deshalb zum Neuladen auffordern.
                StatusText.Text = "Die Änderung konnte nicht bestätigt werden. Bitte laden Sie die Liste neu und prüfen Sie den Stand.";
                return;
            }
            finally
            {
                _schaltet = false;
                FahrzeugListe.IsEnabled = true;
            }

            await LadenAsync();
        }

        private async Task LadenAsync()
        {
            if (_kontext.IstVorschau)
            {
                // In der Vorschau gibt es keine Firma. „Fahrzeug anlegen“ zeigt trotzdem das Formular.
                StatusText.Text = "In der Vorschau ohne Anmeldung werden keine Fahrzeuge geladen.";
                NeuLadenButton.IsEnabled = false;
                return;
            }

            if (_laedt)
                return;

            _laedt = true;
            var abbruch = new CancellationTokenSource();
            _laden = abbruch;
            NeuLadenButton.IsEnabled = false;
            LadeAnzeige.IsActive = true;
            LadeAnzeige.Visibility = Visibility.Visible;
            StatusText.Text = "Fahrzeuge werden geladen …";
            try
            {
                IReadOnlyList<Fahrzeug> fahrzeuge = await _repository.LadenAsync(abbruch.Token);
                if (abbruch.IsCancellationRequested)
                    return;

                _fahrzeuge = fahrzeuge;
                Anzeigen();
                StatusText.Text = _meldung ?? Zusammenfassung();
                _meldung = null;
            }
            catch (OperationCanceledException) when (abbruch.IsCancellationRequested)
            {
            }
            catch (UnauthorizedAccessException)
            {
                // Rolle entzogen oder Konto deaktiviert: Sitzung beenden, Anmeldung zeigen.
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                // Keine technischen Details anzeigen. Die zuletzt geladene Liste bleibt sichtbar.
                StatusText.Text = "Die Fahrzeuge konnten nicht geladen werden. Bitte prüfen Sie die Verbindung und laden Sie erneut.";
            }
            finally
            {
                _laedt = false;
                if (ReferenceEquals(_laden, abbruch))
                    _laden = null;
                abbruch.Dispose();
                LadeAnzeige.IsActive = false;
                LadeAnzeige.Visibility = Visibility.Collapsed;
                NeuLadenButton.IsEnabled = true;
            }
        }

        // Zeigt die geladenen Fahrzeuge an. Inaktive nur, wenn „Inaktive anzeigen“ gewählt ist.
        private void Anzeigen()
        {
            int inaktive = _fahrzeuge.Count(fahrzeug => !fahrzeug.Aktiv);
            if (inaktive == 0)
                _inaktiveAnzeigen = false;

            var sichtbar = _fahrzeuge.Where(fahrzeug => fahrzeug.Aktiv || _inaktiveAnzeigen).ToList();
            FahrzeugListe.ItemsSource = sichtbar.Select(fahrzeug => new FahrzeugZeile(fahrzeug)).ToList();

            InaktiveButton.Visibility = inaktive > 0 ? Visibility.Visible : Visibility.Collapsed;
            InaktiveButton.Content = _inaktiveAnzeigen ? "Inaktive ausblenden" : $"Inaktive anzeigen ({inaktive})";

            LeerText.Text = _fahrzeuge.Count == 0
                ? "Noch keine Fahrzeuge angelegt. Legen Sie das erste Fahrzeug mit „+ Fahrzeug anlegen“ an."
                : "Alle Fahrzeuge sind inaktiv. Mit „Inaktive anzeigen“ sehen Sie sie wieder.";
            LeerText.Visibility = sichtbar.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Zum Beispiel „2 aktive Fahrzeuge, 1 inaktives geladen.“
        private string Zusammenfassung()
        {
            int aktive = _fahrzeuge.Count(fahrzeug => fahrzeug.Aktiv);
            int inaktive = _fahrzeuge.Count - aktive;
            string text = aktive == 1 ? "1 aktives Fahrzeug" : $"{aktive} aktive Fahrzeuge";
            if (inaktive > 0)
                text += inaktive == 1 ? ", 1 inaktives" : $", {inaktive} inaktive";
            return text + " geladen.";
        }
    }

    // Eine Tabellenzeile mit fertig formatierten Texten. Die XAML-Vorlage bindet mit {x:Bind Name} daran.
    public sealed class FahrzeugZeile
    {
        // Die App ist deutschsprachig: „184.250 km“ und „13,6 Ldm“ unabhängig von der Windows-Sprache.
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public FahrzeugZeile(Fahrzeug fahrzeug)
        {
            Id = fahrzeug.Id;
            Aktiv = fahrzeug.Aktiv;
            Kennzeichen = fahrzeug.Kennzeichen;
            Abteilung = fahrzeug.Abteilung is null ? "" : $"Abteilung {fahrzeug.Abteilung}";
            AbteilungSichtbarkeit = fahrzeug.Abteilung is null ? Visibility.Collapsed : Visibility.Visible;
            KlasseUndAufbau = fahrzeug.SolltemperaturC is decimal temperatur
                ? $"{fahrzeug.Fahrzeugtyp} · {fahrzeug.Aufbau} {temperatur.ToString("0.#", Deutsch)} °C"
                : $"{fahrzeug.Fahrzeugtyp} · {fahrzeug.Aufbau}";
            HerstellerUndModell = $"{fahrzeug.Hersteller} {fahrzeug.Modell} · {fahrzeug.Baujahr}";
            Kilometerstand = $"{fahrzeug.Kilometerstand.ToString("N0", Deutsch)} km";
            Kapazitaet = $"{fahrzeug.NutzbareLademeter.ToString("0.##", Deutsch)} Ldm · {fahrzeug.EpStellplaetze} Plätze";
            // Bei einem inaktiven Fahrzeug zählt sein letzter Status nicht mehr.
            Status = fahrzeug.Aktiv ? fahrzeug.Status : "Inaktiv";
            Deckkraft = fahrzeug.Aktiv ? 1 : 0.55;
            AktivUmschaltenText = fahrzeug.Aktiv ? "Inaktiv setzen" : "Aktivieren";
            BearbeitenBeschreibung = $"Fahrzeug {fahrzeug.Kennzeichen} bearbeiten";
            AktivUmschaltenBeschreibung = fahrzeug.Aktiv
                ? $"Fahrzeug {fahrzeug.Kennzeichen} inaktiv setzen"
                : $"Fahrzeug {fahrzeug.Kennzeichen} aktivieren";
        }

        public long Id { get; }
        public bool Aktiv { get; }
        public string Kennzeichen { get; }
        public string Abteilung { get; }
        public Visibility AbteilungSichtbarkeit { get; }
        public string KlasseUndAufbau { get; }
        public string HerstellerUndModell { get; }
        public string Kilometerstand { get; }
        public string Kapazitaet { get; }
        public string Status { get; }
        public double Deckkraft { get; }
        public string AktivUmschaltenText { get; }
        // Für Bildschirmleser: Welches Fahrzeug ein Knopf betrifft.
        public string BearbeitenBeschreibung { get; }
        public string AktivUmschaltenBeschreibung { get; }
    }
}
