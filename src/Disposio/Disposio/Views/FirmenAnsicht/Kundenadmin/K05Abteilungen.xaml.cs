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
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Disposio.Views
{
    // K05 lädt die Abteilungen der Firma beim Öffnen. Anlegen, Umbenennen und (De-)Aktivieren laden die Liste danach neu.
    // Die Rechteprüfung (Rolle Kundenadmin) steckt im AbteilungRepository.
    public sealed partial class K05Abteilungen : UserControl
    {
        private readonly FirmenAnsichtKontext _kontext;
        private readonly AbteilungRepository _repository = new();
        private IReadOnlyList<Abteilung> _abteilungen = Array.Empty<Abteilung>();
        private CancellationTokenSource? _laden;
        private bool _arbeitet;

        public K05Abteilungen(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            Loaded += async (_, _) => await LadenAsync(null);
            Unloaded += (_, _) => _laden?.Cancel();
        }

        // ---- Laden ----

        private async Task LadenAsync(string? meldung)
        {
            if (_kontext.IstVorschau)
            {
                StatusText.Text = "In der Vorschau ohne Anmeldung werden keine Abteilungen geladen.";
                AnlegenButton.IsEnabled = NeuBox.IsEnabled = false;
                return;
            }

            _laden?.Cancel();
            var abbruch = new CancellationTokenSource();
            _laden = abbruch;
            LadeAnzeige.IsActive = true;
            LadeAnzeige.Visibility = Visibility.Visible;
            StatusText.Text = "Abteilungen werden geladen …";
            try
            {
                _abteilungen = await _repository.LadenAsync(abbruch.Token);
                if (abbruch.IsCancellationRequested)
                    return;

                AbteilungListe.ItemsSource = _abteilungen.Select(a => new AbteilungZeile(a)).ToList();
                LeerText.Visibility = _abteilungen.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                int aktiv = _abteilungen.Count(a => a.Aktiv);
                string anzahl = $"{_abteilungen.Count} Abteilung{(_abteilungen.Count == 1 ? "" : "en")}, davon {aktiv} aktiv.";
                StatusText.Text = meldung is null ? anzahl : $"{meldung} · {anzahl}";
            }
            catch (OperationCanceledException) when (abbruch.IsCancellationRequested)
            {
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (MigrationFehltException fehler)
            {
                StatusText.Text = fehler.Message;
            }
            catch (Exception fehler)
            {
                // Keine Servertexte anzeigen, nur den PostgreSQL-Fehlercode. Der hilft bei der Fehlersuche.
                StatusText.Text = "Die Abteilungen konnten nicht geladen werden. Bitte prüfen Sie die Verbindung und laden Sie erneut." +
                                  Fehlercode(fehler);
            }
            finally
            {
                if (ReferenceEquals(_laden, abbruch))
                {
                    _laden = null;
                    LadeAnzeige.IsActive = false;
                    LadeAnzeige.Visibility = Visibility.Collapsed;
                }
                abbruch.Dispose();
            }
        }

        // ---- Anlegen ----

        private async void Anlegen_Click(object sender, RoutedEventArgs e) => await AnlegenAsync();

        private async void NeuBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await AnlegenAsync();
            }
        }

        private async Task AnlegenAsync()
        {
            string name = NeuBox.Text.Trim();
            bool ok = await AusfuehrenAsync(() => _repository.AnlegenAsync(name), $"Abteilung „{name}“ wurde angelegt.");
            if (ok)
                NeuBox.Text = "";
        }

        // ---- Umbenennen ----

        private async void Umbenennen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: long id } || _abteilungen.FirstOrDefault(a => a.Id == id) is not { } abteilung)
                return;

            var eingabe = new TextBox { Text = abteilung.Bezeichnung, MaxLength = 100, MinWidth = 320 };
            eingabe.SelectAll();
            var dialog = Dialog($"„{abteilung.Bezeichnung}“ umbenennen", eingabe, "Speichern");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            string name = eingabe.Text.Trim();
            if (name == abteilung.Bezeichnung)
                return;
            await AusfuehrenAsync(() => _repository.UmbenennenAsync(id, name),
                $"„{abteilung.Bezeichnung}“ heißt jetzt „{name}“.");
        }

        // ---- Aktivieren / Deaktivieren ----

        private async void Umschalten_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: long id } || _abteilungen.FirstOrDefault(a => a.Id == id) is not { } abteilung)
                return;

            bool aktivieren = !abteilung.Aktiv;
            if (!aktivieren)
            {
                // Vor dem Deaktivieren zeigen, was weiter zugeordnet bleibt.
                var text = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420,
                    Text = $"{Anzahl(abteilung.Benutzer, "Benutzer", "Benutzer")}, {Anzahl(abteilung.Fahrzeuge, "Fahrzeug", "Fahrzeuge")} und " +
                           $"{Anzahl(abteilung.OffeneTouren, "offene Tour", "offene Touren")} bleiben dieser Abteilung zugeordnet. " +
                           "Für neue Touren wird sie nicht mehr angeboten. Sie können sie später wieder aktivieren."
                };
                var dialog = Dialog($"„{abteilung.Bezeichnung}“ deaktivieren?", text, "Deaktivieren");
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;
            }

            await AusfuehrenAsync(() => _repository.AktivSetzenAsync(id, aktivieren),
                aktivieren ? $"„{abteilung.Bezeichnung}“ ist wieder aktiv." : $"„{abteilung.Bezeichnung}“ wurde deaktiviert.");
        }

        // ---- Hilfsmethoden ----

        // Führt eine Änderung aus, zeigt Fehler verständlich an und lädt danach die Liste neu.
        private async Task<bool> AusfuehrenAsync(Func<Task> aktion, string erfolg)
        {
            if (_arbeitet || _kontext.IstVorschau)
                return false;

            _arbeitet = true;
            AnlegenButton.IsEnabled = false;
            FehlerText.Visibility = Visibility.Collapsed;
            try
            {
                await aktion();
                await LadenAsync(erfolg);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (AbteilungVergebenException fehler)
            {
                FehlerAnzeigen(fehler.Message);
            }
            catch (MigrationFehltException fehler)
            {
                FehlerAnzeigen(fehler.Message);
            }
            catch (ArgumentException fehler)
            {
                FehlerAnzeigen(fehler.Message);
            }
            catch (Exception fehler)
            {
                FehlerAnzeigen("Die Änderung konnte nicht gespeichert werden. Bitte prüfen Sie die Verbindung und versuchen Sie es erneut." +
                               Fehlercode(fehler));
            }
            finally
            {
                _arbeitet = false;
                AnlegenButton.IsEnabled = true;
            }
            return false;
        }

        private static string Fehlercode(Exception fehler) =>
            fehler is NeonSqlException { Code: { } code } ? $" (Fehlercode {code})" : "";

        private void FehlerAnzeigen(string text)
        {
            FehlerText.Text = text;
            FehlerText.Visibility = Visibility.Visible;
        }

        private ContentDialog Dialog(string titel, UIElement inhalt, string bestaetigen) => new()
        {
            // Der Dialog braucht die XamlRoot der Seite und übernimmt deren Hell/Dunkel-Einstellung.
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = titel,
            Content = inhalt,
            PrimaryButtonText = bestaetigen,
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary
        };

        private static string Anzahl(int anzahl, string einzahl, string mehrzahl) =>
            $"{anzahl.ToString(CultureInfo.GetCultureInfo("de-DE"))} {(anzahl == 1 ? einzahl : mehrzahl)}";
    }

    // Eine Tabellenzeile mit fertig formatierten Texten. Die XAML-Vorlage bindet mit {x:Bind Name} daran.
    public sealed class AbteilungZeile
    {
        public AbteilungZeile(Abteilung abteilung)
        {
            Id = abteilung.Id;
            Bezeichnung = abteilung.Bezeichnung;
            Benutzer = abteilung.Benutzer.ToString(CultureInfo.InvariantCulture);
            Fahrzeuge = abteilung.Fahrzeuge.ToString(CultureInfo.InvariantCulture);
            OffeneTouren = abteilung.OffeneTouren.ToString(CultureInfo.InvariantCulture);
            StatusText = abteilung.Aktiv ? "Aktiv" : "Deaktiviert";
            UmschaltText = abteilung.Aktiv ? "Deaktivieren" : "Aktivieren";
            // Deaktivierte Abteilungen etwas blasser, damit die aktiven ins Auge fallen.
            Deckkraft = abteilung.Aktiv ? 1.0 : 0.6;
        }

        public long Id { get; }
        public string Bezeichnung { get; }
        public string Benutzer { get; }
        public string Fahrzeuge { get; }
        public string OffeneTouren { get; }
        public string StatusText { get; }
        public string UmschaltText { get; }
        public double Deckkraft { get; }
    }
}
