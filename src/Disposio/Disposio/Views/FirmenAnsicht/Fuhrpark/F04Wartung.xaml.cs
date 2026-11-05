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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Disposio.Views
{
    // F04 zeigt offene Wartungstermine (mit Ampel) und die Historie. Startwert: eine Erfolgsmeldung aus F05
    // (string) oder eine Fahrzeug-Id (long), auf die die Historie gleich gefiltert wird.
    public sealed partial class F04Wartung : UserControl
    {
        private readonly FirmenAnsichtKontext _kontext;
        private readonly IWartungRepository _repository = new WartungRepository();
        private CancellationTokenSource? _laden;
        private bool _laedt;
        private bool _speichert;
        private string? _meldung;
        private long? _filterFahrzeugId;
        private WartungDaten? _daten;

        public F04Wartung(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            _meldung = kontext.Parameter as string;
            _filterFahrzeugId = kontext.Parameter is long fahrzeugId ? fahrzeugId : null;

            foreach (WartungsAmpel ampel in new[] { WartungsAmpel.Ueberfaellig, WartungsAmpel.BaldFaellig, WartungsAmpel.Geplant })
                Legende.Children.Add(WartungsAmpelFarben.Legendeneintrag(ampel));

            Loaded += async (_, _) => await LadenAsync();
            Unloaded += (_, _) => _laden?.Cancel();
        }

        private async void NeuLaden_Click(object sender, RoutedEventArgs e) => await LadenAsync();

        private void Anlegen_Click(object sender, RoutedEventArgs e) =>
            _kontext.Oeffnen("F05", new WartungFormularStart(WartungFormularModus.TerminPlanen, null, AktuellerFilter()));

        private void Nachtragen_Click(object sender, RoutedEventArgs e) =>
            _kontext.Oeffnen("F05", new WartungFormularStart(WartungFormularModus.Nachtragen, null, AktuellerFilter()));

        private void Erledigen_Click(object sender, RoutedEventArgs e)
        {
            if (!_speichert && sender is Button { DataContext: WartungsterminZeile zeile })
                _kontext.Oeffnen("F05", new WartungFormularStart(WartungFormularModus.Erledigen, zeile.Id, AktuellerFilter()));
        }

        // Nur für falsch angelegte Termine. Durchgeführte Wartungen lassen sich nicht entfernen.
        private async void Entfernen_Click(object sender, RoutedEventArgs e)
        {
            if (_laedt || _speichert || XamlRoot is null || sender is not Button { DataContext: WartungsterminZeile zeile })
                return;

            _speichert = true;
            try
            {
                var bestaetigen = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = RequestedTheme,
                    Title = $"Termin „{zeile.Wartungsart}“ für {zeile.Kennzeichen} entfernen?",
                    Content = "Nur für falsch angelegte Termine. Ist die Wartung schon gemacht, wählen Sie stattdessen " +
                        "„Erledigt eintragen“, damit sie in der Historie erscheint.",
                    PrimaryButtonText = "Entfernen",
                    CloseButtonText = "Abbrechen",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await bestaetigen.ShowAsync() != ContentDialogResult.Primary)
                    return;

                TerminListe.IsEnabled = false;
                _meldung = await _repository.TerminEntfernenAsync(zeile.Id)
                    ? $"Der Termin „{zeile.Wartungsart}“ für {zeile.Kennzeichen} wurde entfernt."
                    : "Der Termin war schon erledigt oder entfernt. Die Liste zeigt jetzt den aktuellen Stand.";
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
                return;
            }
            catch (Exception)
            {
                StatusText.Text = "Das Entfernen konnte nicht bestätigt werden. Bitte laden Sie die Liste neu und prüfen Sie den Stand.";
                return;
            }
            finally
            {
                _speichert = false;
                TerminListe.IsEnabled = true;
            }

            await LadenAsync();
        }

        private async Task LadenAsync()
        {
            if (_kontext.IstVorschau)
            {
                StatusText.Text = "In der Vorschau ohne Anmeldung werden keine Wartungen geladen.";
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
            StatusText.Text = "Wartungen werden geladen …";
            try
            {
                WartungDaten daten = await _repository.LadenAsync(abbruch.Token);
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
                StatusText.Text = "Die Wartungen konnten nicht geladen werden. Bitte prüfen Sie die Verbindung und laden Sie erneut.";
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

        private void Anzeigen(WartungDaten daten)
        {
            // Dringendes zuerst: überfällig, bald fällig, geplant; innerhalb davon nach Termin.
            DateOnly heute = WartungsRegeln.Heute();
            var termine = daten.OffeneTermine
                .Select(termin => new WartungsterminZeile(termin, heute))
                .OrderBy(zeile => zeile.Ampel)
                .ThenBy(zeile => zeile.Termin)
                .ThenBy(zeile => zeile.Kennzeichen, StringComparer.CurrentCulture)
                .ToList();
            TerminListe.ItemsSource = termine;
            TermineLeerText.Visibility = termine.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Filter: alle Fahrzeuge der Firma. Ausgewählt bleibt, was vorher gewählt (oder als Startwert übergeben) war.
            long? filterVorher = AktuellerFilter() ?? _filterFahrzeugId;
            _filterFahrzeugId = null;
            HistorieFilterBox.SelectionChanged -= HistorieFilter_SelectionChanged;
            HistorieFilterBox.Items.Clear();
            HistorieFilterBox.Items.Add(new ComboBoxItem { Content = "Alle Fahrzeuge", Tag = null });
            HistorieFilterBox.SelectedIndex = 0;
            foreach (WartungFahrzeug fahrzeug in daten.Fahrzeuge)
            {
                HistorieFilterBox.Items.Add(new ComboBoxItem
                {
                    Content = fahrzeug.Aktiv ? fahrzeug.Kennzeichen : $"{fahrzeug.Kennzeichen} (inaktiv)",
                    Tag = fahrzeug.Id
                });
                if (fahrzeug.Id == filterVorher)
                    HistorieFilterBox.SelectedIndex = HistorieFilterBox.Items.Count - 1;
            }
            HistorieFilterBox.SelectionChanged += HistorieFilter_SelectionChanged;
            HistorieAnzeigen();
        }

        private void HistorieFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => HistorieAnzeigen();

        private long? AktuellerFilter() => (HistorieFilterBox.SelectedItem as ComboBoxItem)?.Tag is long id ? id : null;

        private void HistorieAnzeigen()
        {
            if (_daten is null)
                return;

            long? fahrzeugId = AktuellerFilter();
            var zeilen = _daten.Historie
                .Where(wartung => fahrzeugId is null || wartung.FahrzeugId == fahrzeugId)
                .Select(wartung => new WartungHistorieZeile(wartung))
                .ToList();
            HistorieListe.ItemsSource = zeilen;
            HistorieLeerText.Text = fahrzeugId is null
                ? "Noch keine durchgeführten Wartungen eingetragen."
                : "Für dieses Fahrzeug sind noch keine durchgeführten Wartungen eingetragen.";
            HistorieLeerText.Visibility = zeilen.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Zum Beispiel „1 überfällig, 2 bald fällig, 3 geplant · 12 Wartungen in der Historie.“
        private static string Zusammenfassung(WartungDaten daten)
        {
            DateOnly heute = WartungsRegeln.Heute();
            int ueberfaellig = daten.OffeneTermine.Count(t => WartungsRegeln.Bewerten(t, heute) == WartungsAmpel.Ueberfaellig);
            int bald = daten.OffeneTermine.Count(t => WartungsRegeln.Bewerten(t, heute) == WartungsAmpel.BaldFaellig);
            int geplant = daten.OffeneTermine.Count - ueberfaellig - bald;
            string historie = daten.Historie.Count == 1 ? "1 Wartung" : $"{daten.Historie.Count} Wartungen";
            return $"{ueberfaellig} überfällig, {bald} bald fällig, {geplant} geplant · {historie} in der Historie.";
        }
    }

    /// <summary>Eine Zeile „Offene Wartungstermine“ mit Ampelfarbe (#90).</summary>
    public sealed class WartungsterminZeile
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public WartungsterminZeile(Wartungstermin termin, DateOnly heute)
        {
            Id = termin.Id;
            FahrzeugId = termin.FahrzeugId;
            Termin = termin.Termin;
            Kennzeichen = termin.Kennzeichen;
            Wartungsart = termin.Wartungsart;
            Ampel = WartungsRegeln.Bewerten(termin, heute);
            AmpelText = WartungsRegeln.Bezeichnung(Ampel);
            AmpelFarbe = WartungsAmpelFarben.Pinsel(Ampel);
            // Überfällig und bald fällig bekommen zusätzlich einen getönten Hintergrund.
            Hintergrund = Ampel is WartungsAmpel.Ueberfaellig or WartungsAmpel.BaldFaellig
                ? WartungsAmpelFarben.Pinsel(Ampel, 0.10)
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            int tage = termin.Termin.DayNumber - heute.DayNumber;
            Faellig = $"am {termin.Termin.ToString("dd.MM.yyyy", Deutsch)}";
            string zusatz = tage switch
            {
                < -1 => $"seit {-tage} Tagen",
                -1 => "seit gestern",
                0 => "heute",
                1 => "morgen",
                _ => $"in {tage} Tagen"
            };
            if (termin.FaelligBeiKm is int km)
            {
                int rest = km - termin.AktuellerKm;
                zusatz += rest > 0
                    ? $" · oder bei {km.ToString("N0", Deutsch)} km (noch {rest.ToString("N0", Deutsch)} km)"
                    : $" · {km.ToString("N0", Deutsch)} km erreicht";
            }
            FaelligZusatz = zusatz;
            Bemerkung = termin.Bemerkung;
            ErledigenBeschreibung = $"{termin.Wartungsart} für Fahrzeug {termin.Kennzeichen} als erledigt eintragen";
            EntfernenBeschreibung = $"Termin {termin.Wartungsart} für Fahrzeug {termin.Kennzeichen} entfernen";
        }

        public long Id { get; }
        public long FahrzeugId { get; }
        public DateOnly Termin { get; }
        public string Kennzeichen { get; }
        public string Wartungsart { get; }
        public WartungsAmpel Ampel { get; }
        public string AmpelText { get; }
        public SolidColorBrush AmpelFarbe { get; }
        public SolidColorBrush Hintergrund { get; }
        public string Faellig { get; }
        public string FaelligZusatz { get; }
        public string Bemerkung { get; }
        public string ErledigenBeschreibung { get; }
        public string EntfernenBeschreibung { get; }
    }

    /// <summary>Eine Zeile „Wartungshistorie“ (#91).</summary>
    public sealed class WartungHistorieZeile
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public WartungHistorieZeile(DurchgefuehrteWartung wartung)
        {
            Datum = wartung.Datum.ToString("dd.MM.yyyy", Deutsch);
            Kennzeichen = wartung.Kennzeichen;
            Wartungsart = wartung.Wartungsart;
            Kilometerstand = $"{wartung.Kilometerstand.ToString("N0", Deutsch)} km";
            Kosten = wartung.Kosten.ToString("C", Deutsch);
            Bemerkung = wartung.Folgetermin is DateOnly folge
                ? string.Join(" · ", new[] { wartung.Bemerkung, $"Folgetermin {folge.ToString("dd.MM.yyyy", Deutsch)}" }
                    .Where(text => text.Length > 0))
                : wartung.Bemerkung;
        }

        public string Datum { get; }
        public string Kennzeichen { get; }
        public string Wartungsart { get; }
        public string Kilometerstand { get; }
        public string Kosten { get; }
        public string Bemerkung { get; }
    }

    /// <summary>
    /// Die Ampelfarben an einer Stelle, für F01 und F04. Die Farben sind auf hellem und dunklem Hintergrund
    /// lesbar; die Bedeutung steht zusätzlich immer als Text daneben (nicht nur über die Farbe).
    /// </summary>
    public static class WartungsAmpelFarben
    {
        public static Color Farbe(WartungsAmpel ampel) => ampel switch
        {
            WartungsAmpel.Ueberfaellig => Color.FromArgb(0xFF, 0xE5, 0x48, 0x4D),
            WartungsAmpel.BaldFaellig => Color.FromArgb(0xFF, 0xF5, 0xA5, 0x24),
            WartungsAmpel.Geplant => Color.FromArgb(0xFF, 0x30, 0xA4, 0x6C),
            _ => Color.FromArgb(0xFF, 0x8B, 0x8D, 0x98)
        };

        public static SolidColorBrush Pinsel(WartungsAmpel ampel, double deckkraft = 1)
        {
            Color farbe = Farbe(ampel);
            return new SolidColorBrush(Color.FromArgb((byte)(deckkraft * 255), farbe.R, farbe.G, farbe.B));
        }

        // Punkt plus Text, zum Beispiel für die Legende über der Terminliste.
        public static UIElement Legendeneintrag(WartungsAmpel ampel)
        {
            string erklaerung = ampel switch
            {
                WartungsAmpel.Ueberfaellig => "Überfällig",
                WartungsAmpel.BaldFaellig => $"Bald fällig (≤ {WartungsRegeln.BaldFaelligTage} Tage oder ≤ " +
                    $"{WartungsRegeln.BaldFaelligKm.ToString("N0", CultureInfo.GetCultureInfo("de-DE"))} km)",
                _ => WartungsRegeln.Bezeichnung(ampel)
            };
            var eintrag = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            eintrag.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Pinsel(ampel), VerticalAlignment = VerticalAlignment.Center });
            eintrag.Children.Add(new TextBlock
            {
                Text = erklaerung,
                FontSize = 13,
                Foreground = Application.Current.Resources["MutedTextBrush"] as Brush
            });
            return eintrag;
        }
    }
}
