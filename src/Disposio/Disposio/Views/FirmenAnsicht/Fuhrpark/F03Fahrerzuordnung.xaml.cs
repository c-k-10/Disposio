using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Disposio.Views
{
    // F03 lädt Zuordnungen, Fahrer und freie Fahrzeuge beim Öffnen. Die Rechteprüfung steckt im
    // FahrerzuordnungRepository: Es liefert und ändert nur Daten der Firma, für die das Konto berechtigt ist.
    public sealed partial class F03Fahrerzuordnung : UserControl
    {
        private readonly FirmenAnsichtKontext _kontext;
        private readonly IFahrerzuordnungRepository _repository = new FahrerzuordnungRepository();
        private CancellationTokenSource? _laden;
        private bool _laedt;
        // Läuft gerade Zuordnen, Wechseln oder Beenden? Dann zählt kein weiterer Klick.
        private bool _speichert;
        // Erfolgsmeldung, die nach dem nächsten Laden einmal im Status erscheint.
        private string? _meldung;
        private FahrerzuordnungDaten? _daten;

        public F03Fahrerzuordnung(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            Loaded += async (_, _) => await LadenAsync();
            Unloaded += (_, _) => _laden?.Cancel();
        }

        private async void NeuLaden_Click(object sender, RoutedEventArgs e) => await LadenAsync();

        private async void Zuordnen_Click(object sender, RoutedEventArgs e)
        {
            if (_laedt || _speichert)
                return;

            FehlerAnzeige.Visibility = Visibility.Collapsed;
            if ((FahrzeugBox.SelectedItem as ComboBoxItem)?.Tag is not ZuordnungFahrzeug fahrzeug)
            {
                Fehler("Bitte ein Fahrzeug auswählen.", FahrzeugBox);
                return;
            }
            if ((FahrerBox.SelectedItem as ComboBoxItem)?.Tag is not ZuordnungFahrer fahrer)
            {
                Fehler("Bitte einen Fahrer auswählen.", FahrerBox);
                return;
            }

            await SpeichernAsync(
                () => _repository.ZuordnenAsync(fahrzeug.Id, fahrer.FahrerId, BemerkungBox.Text),
                $"{fahrer.Name} fährt jetzt Fahrzeug {fahrzeug.Kennzeichen}.",
                fehlerImFormular: true);
        }

        // #86 Ändern: neuen Fahrer wählen. Die alte Zuordnung endet jetzt und bleibt in der Historie.
        private async void Wechseln_Click(object sender, RoutedEventArgs e)
        {
            if (_laedt || _speichert || XamlRoot is null || _daten is null ||
                sender is not Button { DataContext: ZuordnungZeile zeile })
                return;

            _speichert = true;
            ZuordnungFahrer? neuerFahrer;
            string bemerkung;
            try
            {
                var auswahl = new ComboBox
                {
                    PlaceholderText = "Bitte wählen",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MinHeight = 44
                };
                AutomationProperties.SetName(auswahl, "Neuer Fahrer");
                foreach (ZuordnungFahrer fahrer in _daten.Fahrer.Where(f => f.FahrerId != zeile.FahrerId))
                    auswahl.Items.Add(new ComboBoxItem { Content = fahrer.Anzeige, Tag = fahrer });

                var bemerkungBox = new TextBox
                {
                    PlaceholderText = "Bemerkung (optional)",
                    MaxLength = FahrerzuordnungRepository.BemerkungMaximal,
                    MinHeight = 44
                };
                AutomationProperties.SetName(bemerkungBox, "Bemerkung");
                var hinweis = new TextBlock
                {
                    Text = $"Die Zuordnung von {zeile.Fahrer} endet jetzt und bleibt in der Historie sichtbar.",
                    TextWrapping = TextWrapping.Wrap
                };

                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = RequestedTheme,
                    Title = $"Fahrer für {zeile.Kennzeichen} wechseln",
                    Content = new StackPanel { Spacing = 12, MinWidth = 380, Children = { hinweis, auswahl, bemerkungBox } },
                    PrimaryButtonText = "Fahrer wechseln",
                    CloseButtonText = "Abbrechen",
                    DefaultButton = ContentDialogButton.Primary,
                    IsPrimaryButtonEnabled = false
                };
                auswahl.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = auswahl.SelectedItem is not null;

                if (auswahl.Items.Count == 0)
                {
                    StatusText.Text = "Es gibt keinen weiteren Fahrer mit aktivem Konto, der übernehmen könnte.";
                    return;
                }
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;

                neuerFahrer = (auswahl.SelectedItem as ComboBoxItem)?.Tag as ZuordnungFahrer;
                bemerkung = bemerkungBox.Text;
            }
            finally
            {
                _speichert = false;
            }

            if (neuerFahrer is null)
                return;

            await SpeichernAsync(
                () => _repository.WechselnAsync(zeile.Id, neuerFahrer.FahrerId, bemerkung),
                $"Fahrzeug {zeile.Kennzeichen}: {neuerFahrer.Name} hat von {zeile.Fahrer} übernommen.",
                fehlerImFormular: false);
        }

        private async void Beenden_Click(object sender, RoutedEventArgs e)
        {
            if (_laedt || _speichert || XamlRoot is null || sender is not Button { DataContext: ZuordnungZeile zeile })
                return;

            _speichert = true;
            try
            {
                var bestaetigen = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = RequestedTheme,
                    Title = $"Zuordnung {zeile.Fahrer} – {zeile.Kennzeichen} beenden?",
                    Content = "Die Zuordnung endet jetzt. Sie wird nicht gelöscht, sondern bleibt mit ihrem Zeitraum in der " +
                        "Historie. Danach ist das Fahrzeug wieder frei für einen anderen Fahrer.",
                    PrimaryButtonText = "Beenden",
                    CloseButtonText = "Abbrechen",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await bestaetigen.ShowAsync() != ContentDialogResult.Primary)
                    return;
            }
            finally
            {
                _speichert = false;
            }

            await SpeichernAsync(async () =>
                {
                    if (!await _repository.BeendenAsync(zeile.Id))
                        throw new ArgumentException("Die Zuordnung war schon beendet. Die Ansicht zeigt jetzt den aktuellen Stand.");
                },
                $"Die Zuordnung {zeile.Fahrer} – {zeile.Kennzeichen} ist beendet.",
                fehlerImFormular: false);
        }

        // Gemeinsamer Ablauf für alle Änderungen: sperren, speichern, Meldung zeigen, neu laden.
        private async Task SpeichernAsync(Func<Task> aktion, string erfolg, bool fehlerImFormular)
        {
            _speichert = true;
            ZuordnenButton.IsEnabled = false;
            AktuelleListe.IsEnabled = false;
            bool neuLaden = true;
            try
            {
                await aktion();
                _meldung = erfolg;
                if (fehlerImFormular)
                {
                    // Erfolgreich zugeordnet: Das Formular ist wieder leer für die nächste Zuordnung.
                    FahrzeugBox.SelectedIndex = FahrerBox.SelectedIndex = -1;
                    BemerkungBox.Text = "";
                }
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
                neuLaden = false;
            }
            catch (FahrzeugDoppeltBelegtException exception)
            {
                Melden(exception.Message, fehlerImFormular);
            }
            catch (ArgumentException exception)
            {
                // Meldungen aus unserer eigenen Prüfung im Repository.
                Melden(exception.Message, fehlerImFormular);
            }
            catch (Exception)
            {
                // Vielleicht ist die Änderung trotzdem angekommen. Deshalb neu laden und den Stand zeigen.
                Melden("Die Änderung konnte nicht bestätigt werden. Bitte prüfen Sie den angezeigten Stand.", fehlerImFormular);
            }
            finally
            {
                _speichert = false;
                ZuordnenButton.IsEnabled = true;
                AktuelleListe.IsEnabled = true;
            }

            if (neuLaden)
                await LadenAsync();
        }

        private void Melden(string text, bool imFormular)
        {
            if (imFormular)
                Fehler(text, null);
            else
                _meldung = text;
        }

        private async Task LadenAsync()
        {
            if (_kontext.IstVorschau)
            {
                StatusText.Text = "In der Vorschau ohne Anmeldung werden keine Zuordnungen geladen.";
                NeuLadenButton.IsEnabled = ZuordnenButton.IsEnabled = false;
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
            StatusText.Text = "Zuordnungen werden geladen …";
            try
            {
                FahrerzuordnungDaten daten = await _repository.LadenAsync(abbruch.Token);
                if (abbruch.IsCancellationRequested)
                    return;

                _daten = daten;
                Anzeigen(daten);
                StatusText.Text = _meldung ?? Zusammenfassung(daten);
                _meldung = null;
            }
            catch (OperationCanceledException) when (abbruch.IsCancellationRequested)
            {
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                StatusText.Text = _meldung is null
                    ? "Die Zuordnungen konnten nicht geladen werden. Bitte prüfen Sie die Verbindung und laden Sie erneut."
                    : _meldung + " Die Liste konnte danach nicht neu geladen werden.";
                _meldung = null;
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

        private void Anzeigen(FahrerzuordnungDaten daten)
        {
            // Auswahl merken, damit ein Neuladen nach einem Fehler die Eingaben nicht verwirft.
            long? fahrzeugVorher = ((FahrzeugBox.SelectedItem as ComboBoxItem)?.Tag as ZuordnungFahrzeug)?.Id;
            long? fahrerVorher = ((FahrerBox.SelectedItem as ComboBoxItem)?.Tag as ZuordnungFahrer)?.FahrerId;

            FahrzeugBox.Items.Clear();
            foreach (ZuordnungFahrzeug fahrzeug in daten.FreieFahrzeuge)
            {
                FahrzeugBox.Items.Add(new ComboBoxItem { Content = fahrzeug.Anzeige, Tag = fahrzeug });
                if (fahrzeug.Id == fahrzeugVorher)
                    FahrzeugBox.SelectedIndex = FahrzeugBox.Items.Count - 1;
            }
            FahrzeugBox.PlaceholderText = daten.FreieFahrzeuge.Count == 0 ? "Kein Fahrzeug verfügbar" : "Bitte wählen";

            FahrerBox.Items.Clear();
            foreach (ZuordnungFahrer fahrer in daten.Fahrer)
            {
                FahrerBox.Items.Add(new ComboBoxItem { Content = fahrer.Anzeige, Tag = fahrer });
                if (fahrer.FahrerId == fahrerVorher)
                    FahrerBox.SelectedIndex = FahrerBox.Items.Count - 1;
            }
            FahrerBox.PlaceholderText = daten.Fahrer.Count == 0 ? "Keine Fahrer angelegt" : "Bitte wählen";

            AktuelleListe.ItemsSource = daten.Aktuelle.Select(z => new ZuordnungZeile(z)).ToList();
            AktuelleLeerText.Visibility = daten.Aktuelle.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Filter der Historie: alle Fahrzeuge, die dort vorkommen. Die bisherige Wahl bleibt erhalten.
            string? filterVorher = (HistorieFilterBox.SelectedItem as ComboBoxItem)?.Tag as string;
            HistorieFilterBox.SelectionChanged -= HistorieFilter_SelectionChanged;
            HistorieFilterBox.Items.Clear();
            HistorieFilterBox.Items.Add(new ComboBoxItem { Content = "Alle Fahrzeuge", Tag = null });
            HistorieFilterBox.SelectedIndex = 0;
            foreach (string kennzeichen in daten.Historie.Select(z => z.Kennzeichen).Distinct().OrderBy(k => k, StringComparer.CurrentCulture))
            {
                HistorieFilterBox.Items.Add(new ComboBoxItem { Content = kennzeichen, Tag = kennzeichen });
                if (kennzeichen == filterVorher)
                    HistorieFilterBox.SelectedIndex = HistorieFilterBox.Items.Count - 1;
            }
            HistorieFilterBox.SelectionChanged += HistorieFilter_SelectionChanged;
            HistorieAnzeigen();
        }

        private void HistorieFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => HistorieAnzeigen();

        private void HistorieAnzeigen()
        {
            if (_daten is null)
                return;

            string? kennzeichen = (HistorieFilterBox.SelectedItem as ComboBoxItem)?.Tag as string;
            var zeilen = _daten.Historie
                .Where(z => kennzeichen is null || z.Kennzeichen == kennzeichen)
                .Select(z => new ZuordnungZeile(z))
                .ToList();
            HistorieListe.ItemsSource = zeilen;
            HistorieLeerText.Text = _daten.Historie.Count == 0
                ? "Noch keine beendeten Zuordnungen. Beendete oder gewechselte Zuordnungen erscheinen hier mit ihrem Zeitraum."
                : "Für dieses Fahrzeug gibt es keine beendeten Zuordnungen.";
            HistorieLeerText.Visibility = zeilen.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string Zusammenfassung(FahrerzuordnungDaten daten)
        {
            string aktuelle = daten.Aktuelle.Count == 1 ? "1 laufende Zuordnung" : $"{daten.Aktuelle.Count} laufende Zuordnungen";
            string frei = daten.FreieFahrzeuge.Count == 1 ? "1 Fahrzeug frei" : $"{daten.FreieFahrzeuge.Count} Fahrzeuge frei";
            return $"{aktuelle}, {frei}.";
        }

        private void Fehler(string text, Control? feld)
        {
            FehlerAnzeige.Text = text;
            FehlerAnzeige.Visibility = Visibility.Visible;
            feld?.Focus(FocusState.Programmatic);
        }
    }

    // Eine Zeile in „Aktuelle Zuordnungen“ oder „Historie“ mit fertig formatierten Texten.
    public sealed class ZuordnungZeile
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public ZuordnungZeile(Fahrerzuordnung zuordnung)
        {
            Id = zuordnung.Id;
            FahrerId = zuordnung.FahrerId;
            Kennzeichen = zuordnung.Kennzeichen;
            Fahrer = zuordnung.Fahrername;
            Seit = Zeit(zuordnung.Von);
            Zeitraum = zuordnung.Bis is DateTime bis ? $"{Zeit(zuordnung.Von)} – {Zeit(bis)}" : $"seit {Zeit(zuordnung.Von)}";
            Bemerkung = zuordnung.Bemerkung ?? "";
            WechselnBeschreibung = $"Fahrer für Fahrzeug {zuordnung.Kennzeichen} wechseln";
            BeendenBeschreibung = $"Zuordnung von {zuordnung.Fahrername} zu Fahrzeug {zuordnung.Kennzeichen} beenden";
        }

        public long Id { get; }
        public long FahrerId { get; }
        public string Kennzeichen { get; }
        public string Fahrer { get; }
        public string Seit { get; }
        public string Zeitraum { get; }
        public string Bemerkung { get; }
        public string WechselnBeschreibung { get; }
        public string BeendenBeschreibung { get; }

        private static string Zeit(DateTime zeit) => zeit.ToString("dd.MM.yyyy HH:mm", Deutsch);
    }
}
