using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Text;

namespace Disposio.Views
{
    // G02 zeigt die Kennzahlen eines Monats und bietet jeden Monat als PDF an. Die Rechteprüfung steckt im
    // StatistikRepository: Inhaber/Geschäftsführer oder Kundenadmin der eigenen Firma, auch bei gesperrter Firma.
    public sealed partial class G02Statistik : UserControl
    {
        private readonly FirmenAnsichtKontext _kontext;
        private readonly IStatistikRepository _repository = new StatistikRepository();
        // Der zuletzt angezeigte Monat. Ein PDF dieses Monats braucht keine zweite Abfrage.
        private MonatsStatistik? _angezeigt;
        // Jede Monatsabfrage bekommt eine Nummer. Kommt eine ältere Antwort zu spät, wird sie verworfen.
        private int _anfrage;
        private bool _laedt;
        private bool _speichert;

        public G02Statistik(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            Loaded += async (_, _) => await LadenAsync();
        }

        private async void NeuLaden_Click(object sender, RoutedEventArgs e) => await LadenAsync();

        // Lädt die Liste der Monate. Der neueste Monat wird ausgewählt und damit auch angezeigt.
        private async Task LadenAsync()
        {
            if (_kontext.IstVorschau)
            {
                StatusText.Text = "In der Vorschau ohne Anmeldung werden keine Statistiken geladen.";
                NeuLadenButton.IsEnabled = false;
                return;
            }

            if (_laedt)
                return;

            _laedt = true;
            NeuLadenButton.IsEnabled = false;
            Laden(true, "Monate werden geladen …");
            try
            {
                IReadOnlyList<DateOnly> monate = await _repository.MonateLadenAsync();
                DownloadListe.ItemsSource = monate.Select(monat => new DownloadZeile(monat)).ToList();

                MonatBox.Items.Clear();
                foreach (DateOnly monat in monate)
                    MonatBox.Items.Add(new ComboBoxItem { Content = StatistikTexte.Lang(monat), Tag = monat });
                Laden(false, "");
                // Löst Monat_SelectionChanged aus und zeigt den neuesten Monat.
                MonatBox.SelectedIndex = monate.Count > 0 ? 0 : -1;
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                Laden(false, "Die Statistik konnte nicht geladen werden. Bitte prüfen Sie die Verbindung und laden Sie erneut.");
            }
            finally
            {
                _laedt = false;
                NeuLadenButton.IsEnabled = true;
            }
        }

        private async void Monat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if ((MonatBox.SelectedItem as ComboBoxItem)?.Tag is DateOnly monat)
                await MonatAnzeigenAsync(monat);
        }

        private async Task MonatAnzeigenAsync(DateOnly monat)
        {
            int anfrage = ++_anfrage;
            _angezeigt = null;
            MonatPdfButton.IsEnabled = false;
            MonatTitel.Text = StatistikTexte.Lang(monat);
            Laden(true, $"Statistik für {StatistikTexte.Lang(monat)} wird geladen …");
            try
            {
                MonatsStatistik statistik = await _repository.MonatLadenAsync(monat);
                if (anfrage != _anfrage)
                    return;

                _angezeigt = statistik;
                ZeilenListe.ItemsSource = StatistikTexte.Zeilen(statistik).Select(zeile => new StatistikAnzeigeZeile(zeile)).ToList();
                MonatPdfButton.IsEnabled = true;
                Laden(false, "");
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                // Eine veraltete Anfrage darf die Anzeige des neueren Monats nicht überschreiben.
                if (anfrage != _anfrage)
                    return;

                ZeilenListe.ItemsSource = null;
                Laden(false, "Die Statistik für diesen Monat konnte nicht geladen werden. Bitte versuchen Sie es erneut.");
            }
        }

        private async void MonatPdf_Click(object sender, RoutedEventArgs e)
        {
            if (_angezeigt is MonatsStatistik statistik)
                await HerunterladenAsync(statistik.Monat);
        }

        // „Anzeigen“ in der Download-Liste wählt den Monat oben aus.
        private void Anzeigen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: DownloadZeile zeile })
                return;

            foreach (ComboBoxItem eintrag in MonatBox.Items.OfType<ComboBoxItem>())
            {
                if (eintrag.Tag is DateOnly monat && monat == zeile.Monat)
                {
                    MonatBox.SelectedItem = eintrag;
                    return;
                }
            }
        }

        private async void Herunterladen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: DownloadZeile zeile })
                await HerunterladenAsync(zeile.Monat);
        }

        // Lädt die Zahlen (falls dieser Monat nicht gerade angezeigt wird), erstellt das PDF und speichert es.
        private async Task HerunterladenAsync(DateOnly monat)
        {
            // Ein Doppelklick erzeugt keine zweite Datei.
            if (_speichert)
                return;

            _speichert = true;
            Laden(true, $"PDF für {StatistikTexte.Lang(monat)} wird erstellt …");
            try
            {
                MonatsStatistik statistik = _angezeigt is { } angezeigt && angezeigt.Monat == monat
                    ? angezeigt
                    : await _repository.MonatLadenAsync(monat);
                string pfad = StatistikPdf.InDownloadsSpeichern(statistik, DateTime.Now);
                Laden(false, $"Gespeichert in „Downloads“: {Path.GetFileName(pfad)}");
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (IOException)
            {
                Laden(false, "Die PDF-Datei konnte nicht im Ordner „Downloads“ gespeichert werden. Bitte prüfen Sie, ob der Ordner beschreibbar ist.");
            }
            catch (Exception)
            {
                Laden(false, "Das PDF konnte nicht erstellt werden. Bitte prüfen Sie die Verbindung und versuchen Sie es erneut.");
            }
            finally
            {
                _speichert = false;
            }
        }

        private void Laden(bool aktiv, string text)
        {
            LadeAnzeige.IsActive = aktiv;
            LadeAnzeige.Visibility = aktiv ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = text;
        }
    }

    // Eine Zeile der Kennzahlen. Die hervorgehobene Zeile („Kosten gesamt“) erscheint fett.
    public sealed class StatistikAnzeigeZeile
    {
        public StatistikAnzeigeZeile(StatistikZeile zeile)
        {
            Bezeichnung = zeile.Bezeichnung;
            Wert = zeile.Wert;
            Schrift = zeile.Hervorgehoben ? FontWeights.SemiBold : FontWeights.Normal;
        }

        public string Bezeichnung { get; }
        public string Wert { get; }
        public FontWeight Schrift { get; }
    }

    // Ein Monat in der Download-Liste, zum Beispiel „Okt 26“.
    public sealed class DownloadZeile
    {
        public DownloadZeile(DateOnly monat)
        {
            Monat = monat;
            Kurz = StatistikTexte.Kurz(monat);
            Lang = StatistikTexte.Lang(monat);
            // Für Bildschirmleser: Welchen Monat ein Knopf betrifft.
            AnzeigenBeschreibung = $"{Lang} anzeigen";
            HerunterladenBeschreibung = $"{Lang} als PDF herunterladen";
        }

        public DateOnly Monat { get; }
        public string Kurz { get; }
        public string Lang { get; }
        public string AnzeigenBeschreibung { get; }
        public string HerunterladenBeschreibung { get; }
    }
}
