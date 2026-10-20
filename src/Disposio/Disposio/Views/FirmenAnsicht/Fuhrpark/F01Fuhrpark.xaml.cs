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
        // Nach dem Speichern in F02 kommt eine Erfolgsmeldung als Startwert. Sie erscheint nur einmal.
        private string? _meldung;

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

                FahrzeugListe.ItemsSource = fahrzeuge.Select(fahrzeug => new FahrzeugZeile(fahrzeug)).ToList();
                LeerText.Visibility = fahrzeuge.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                StatusText.Text = _meldung ?? (fahrzeuge.Count == 1
                    ? "1 Fahrzeug geladen."
                    : $"{fahrzeuge.Count} Fahrzeuge geladen.");
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
    }

    // Eine Tabellenzeile mit fertig formatierten Texten. Die XAML-Vorlage bindet mit {x:Bind Name} daran.
    public sealed class FahrzeugZeile
    {
        // Die App ist deutschsprachig: „184.250 km“ und „13,6 Ldm“ unabhängig von der Windows-Sprache.
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public FahrzeugZeile(Fahrzeug fahrzeug)
        {
            Kennzeichen = fahrzeug.Kennzeichen;
            Abteilung = fahrzeug.Abteilung is null ? "" : $"Abteilung {fahrzeug.Abteilung}";
            AbteilungSichtbarkeit = fahrzeug.Abteilung is null ? Visibility.Collapsed : Visibility.Visible;
            KlasseUndAufbau = fahrzeug.SolltemperaturC is decimal temperatur
                ? $"{fahrzeug.Fahrzeugtyp} · {fahrzeug.Aufbau} {temperatur.ToString("0.#", Deutsch)} °C"
                : $"{fahrzeug.Fahrzeugtyp} · {fahrzeug.Aufbau}";
            HerstellerUndModell = $"{fahrzeug.Hersteller} {fahrzeug.Modell} · {fahrzeug.Baujahr}";
            Kilometerstand = $"{fahrzeug.Kilometerstand.ToString("N0", Deutsch)} km";
            Kapazitaet = $"{fahrzeug.NutzbareLademeter.ToString("0.##", Deutsch)} Ldm · {fahrzeug.EpStellplaetze} Plätze";
            Status = fahrzeug.Status;
        }

        public string Kennzeichen { get; }
        public string Abteilung { get; }
        public Visibility AbteilungSichtbarkeit { get; }
        public string KlasseUndAufbau { get; }
        public string HerstellerUndModell { get; }
        public string Kilometerstand { get; }
        public string Kapazitaet { get; }
        public string Status { get; }
    }
}
