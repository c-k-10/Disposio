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
    // D04 lädt die Touren eines Tages. Das Datum lädt neu aus der Datenbank; Suche und Status
    // filtern nur die geladene Liste. „Filter anwenden“ (oder Enter im Suchfeld) wendet alles an.
    public sealed partial class D04Touren : UserControl
    {
        private const string AlleStatus = "Alle";
        private const string StatusVerspaetung = "Verspätung";
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        private readonly FirmenAnsichtKontext _kontext;
        private readonly TourRepository _repository = new();
        private IReadOnlyList<TourUebersicht> _touren = Array.Empty<TourUebersicht>();
        // Per Klick markierte Zeile. Die Id bleibt erhalten, wenn die Liste neu gefiltert wird.
        private Border? _markierteZeile;
        private long? _markierteTourId;
        private DateOnly? _geladenerTag;
        private CancellationTokenSource? _laden;
        // Nach dem Speichern in D05 kommt eine Erfolgsmeldung als Startwert. Sie erscheint nur einmal.
        private string? _meldung;

        public D04Touren(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;

            // Nach dem Anlegen einer Tour (D05) springt die Übersicht auf deren Tag.
            if (kontext.Parameter is TourGespeichert gespeichert)
            {
                DatumAuswahl.Date = gespeichert.Tag.ToDateTime(TimeOnly.MinValue);
                _meldung = gespeichert.Meldung;
            }
            else
            {
                DatumAuswahl.Date = DateTimeOffset.Now.Date;
            }
            StatusListeAufbauen();
            DatumTextSetzen(HeutigerTag);

            Loaded += async (_, _) => await AnwendenAsync();
            // Wer die Ansicht verlässt, braucht das Ladeergebnis nicht mehr.
            Unloaded += (_, _) => _laden?.Cancel();
        }

        private static DateOnly HeutigerTag => DateOnly.FromDateTime(DateTime.Now);

        private DateOnly GewaehlterTag =>
            DatumAuswahl.Date is DateTimeOffset datum ? DateOnly.FromDateTime(datum.Date) : HeutigerTag;

        // ---- Knöpfe ----

        // D05 bekommt den gerade gewählten Tag als Startwert.
        private void TourPlanen_Click(object sender, RoutedEventArgs e) => _kontext.Oeffnen("D05", GewaehlterTag);

        private async void FilterAnwenden_Click(object sender, RoutedEventArgs e) => await AnwendenAsync();

        private async void SuchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await AnwendenAsync();
            }
        }

        // „Zuweisen“ öffnet D06, „Öffnen“ die Tourdetails D07. Beide bekommen die Tour-Id als Startwert.
        private void TourAktion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: long id })
                TourOeffnen(id);
        }

        private void TourOeffnen(long id)
        {
            TourUebersicht? tour = _touren.FirstOrDefault(t => t.Id == id);
            if (tour is null)
                return;
            _kontext.Oeffnen(MussZugewiesenWerden(tour) ? "D06" : "D07", id);
        }

        // Klick auf eine Zeile markiert sie (Akzentbalken links), Doppelklick öffnet die Tour wie der Knopf.
        private void Zeile_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not Border { Tag: long id } zeile)
                return;
            if (_markierteZeile is { } vorher && !ReferenceEquals(vorher, zeile))
                ListenZeile.SetMarkiert(vorher, false);
            ListenZeile.SetMarkiert(zeile, true);
            _markierteZeile = zeile;
            _markierteTourId = id;
        }

        // Nach dem Neuaufbau der Liste: die noch markierte Zeile merken, damit ein Klick sie wieder abwählt.
        private void Zeile_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Border zeile && ListenZeile.GetMarkiert(zeile))
                _markierteZeile = zeile;
        }

        private void Zeile_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is Border { Tag: long id })
                TourOeffnen(id);
        }

        // ---- Laden und Filtern ----

        private async Task AnwendenAsync()
        {
            DateOnly tag = GewaehlterTag;
            DatumTextSetzen(tag);

            // Nur bei einem anderen Tag neu aus der Datenbank laden.
            if (_geladenerTag != tag)
                await LadenAsync(tag);
            else
                ListeFiltern();
        }

        private async Task LadenAsync(DateOnly tag)
        {
            if (_kontext.IstVorschau)
            {
                StatusText.Text = "In der Vorschau ohne Anmeldung werden keine Touren geladen.";
                LeerText.Visibility = Visibility.Collapsed;
                return;
            }

            _laden?.Cancel();
            var abbruch = new CancellationTokenSource();
            _laden = abbruch;
            LadeAnzeige.IsActive = true;
            LadeAnzeige.Visibility = Visibility.Visible;
            StatusText.Text = "Touren werden geladen …";
            try
            {
                IReadOnlyList<TourUebersicht> touren = await _repository.TagLadenAsync(tag, abbruch.Token);
                if (abbruch.IsCancellationRequested)
                    return;

                _touren = touren;
                _geladenerTag = tag;
                StatusListeAufbauen();
                ListeFiltern();
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
                StatusText.Text = "Die Touren konnten nicht geladen werden. Bitte prüfen Sie die Verbindung und versuchen Sie es erneut.";
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

        private void ListeFiltern()
        {
            string suche = SuchBox.Text.Trim();
            string status = StatusAuswahl.SelectedItem as string ?? AlleStatus;

            List<TourUebersicht> treffer = _touren
                .Where(tour => status == AlleStatus || AnzeigeStatus(tour) == status)
                .Where(tour => suche.Length == 0 || Suchtexte(tour).Any(text =>
                    text?.Contains(suche, StringComparison.CurrentCultureIgnoreCase) == true))
                .ToList();

            _markierteZeile = null;
            TourListe.ItemsSource = treffer
                .Select(tour => new TourZeile(tour, AnzeigeStatus(tour), MussZugewiesenWerden(tour))
                {
                    IstMarkiert = tour.Id == _markierteTourId
                })
                .ToList();

            string anzahl = treffer.Count == _touren.Count
                ? (_touren.Count == 1 ? "1 Tour" : $"{_touren.Count} Touren")
                : $"{treffer.Count} von {_touren.Count} Touren";
            StatusText.Text = _meldung is null ? anzahl : $"{_meldung} · {anzahl}";
            _meldung = null;

            LeerText.Text = _touren.Count == 0
                ? "Für diesen Tag sind keine Touren geplant. Mit „+ Tour planen“ legen Sie eine an."
                : "Keine Tour passt zum Filter.";
            LeerText.Visibility = treffer.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Die Status-Auswahl enthält „Alle“ und jeden Status, der an diesem Tag vorkommt.
        // So muss die Oberfläche die Statuswerte der Datenbank nicht fest kennen.
        private void StatusListeAufbauen()
        {
            string? vorher = StatusAuswahl.SelectedItem as string;
            var eintraege = new List<string> { AlleStatus };
            eintraege.AddRange(_touren.Select(AnzeigeStatus).Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(text => text, StringComparer.CurrentCultureIgnoreCase));

            StatusAuswahl.ItemsSource = eintraege;
            StatusAuswahl.SelectedItem = vorher is not null && eintraege.Contains(vorher) ? vorher : AlleStatus;
        }

        private void DatumTextSetzen(DateOnly tag)
        {
            // Wie im Wireframe: „Dienstag, 29. September 2026“
            string text = tag.ToString("dddd, d. MMMM yyyy", Deutsch);
            DatumText.Text = tag == HeutigerTag ? $"{text} · Heute" : text;
        }

        // ---- Regeln ----

        // Verspätung überschreibt den gespeicherten Status, solange die Tour nicht abgeschlossen ist.
        private static string AnzeigeStatus(TourUebersicht tour) =>
            tour.Verspaetet && !IstAbgeschlossen(tour.Status) ? StatusVerspaetung : StatusLesbar(tour.Status);

        private static bool IstAbgeschlossen(string status) =>
            status.Contains("abgeschlossen", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("beendet", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("storniert", StringComparison.OrdinalIgnoreCase);

        // Statuswerte aus der Datenbank lesbar machen, z. B. „UNTERWEGS“ oder „unterwegs“ → „Unterwegs“.
        private static string StatusLesbar(string status)
        {
            string text = status.Replace('_', ' ').Trim();
            return text.Length == 0 ? "Unbekannt" : char.ToUpper(text[0], Deutsch) + text[1..].ToLower(Deutsch);
        }

        // Ohne Fahrer oder Fahrzeug muss die Tour erst zugewiesen werden.
        private static bool MussZugewiesenWerden(TourUebersicht tour) => !tour.HatFahrer || !tour.HatFahrzeug;

        private static IEnumerable<string?> Suchtexte(TourUebersicht tour) => new[]
        {
            tour.Tournummer, tour.Bereich, tour.Fahrer, tour.Kennzeichen,
            tour.NaechsterStopp, tour.Startort, tour.Zielort
        };
    }

    // Eine Tabellenzeile mit fertig formatierten Texten. Die XAML-Vorlage bindet mit {x:Bind Name} daran.
    public sealed class TourZeile
    {
        public TourZeile(TourUebersicht tour, string status, bool mussZugewiesenWerden)
        {
            Id = tour.Id;
            TourUndBereich = tour.Bereich is null ? tour.Tournummer : $"{tour.Tournummer} · {tour.Bereich}";

            FahrerUndFahrzeug = (tour.HatFahrer, tour.HatFahrzeug) switch
            {
                (false, false) => "Noch nicht zugeordnet",
                (true, false) => $"{tour.Fahrer} / Fahrzeug fehlt",
                (false, true) => $"Fahrer fehlt / {tour.Kennzeichen}",
                _ => $"{tour.Fahrer} / {tour.Kennzeichen}"
            };

            NaechsterStopp = tour.NaechsterStopp ?? "Alle Stopps erledigt";
            string fenster = tour.FensterVon is null ? "—" : $"{tour.FensterVon}–{tour.FensterBis}";
            FensterUndEta = tour.NaechsterStopp is null ? "—" : $"{fenster} / {tour.Eta ?? "—"}";

            Status = status;
            bool verspaetet = status == "Verspätung";
            VerspaetetSichtbar = verspaetet ? Visibility.Visible : Visibility.Collapsed;
            NormalSichtbar = verspaetet ? Visibility.Collapsed : Visibility.Visible;
            AktionText = mussZugewiesenWerden ? "Zuweisen" : "Öffnen";
        }

        public long Id { get; }
        public string TourUndBereich { get; }
        public string FahrerUndFahrzeug { get; }
        public string NaechsterStopp { get; }
        public string FensterUndEta { get; }
        public string Status { get; }
        public Visibility NormalSichtbar { get; }
        public Visibility VerspaetetSichtbar { get; }
        public string AktionText { get; }
        // Beim Neuaufbau der Liste bleibt die zuletzt angeklickte Tour markiert.
        public bool IstMarkiert { get; init; }
    }
}
