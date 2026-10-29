using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Disposio.Views
{
    // D07 „Tourdetails“. Startwert ist die Tour-Id aus D04 („Öffnen“). Lädt Tour und Stopps einmal beim Öffnen.
    // GPS-Daten gibt es erst mit der Fahrer-App; bis dahin steht dort ein Hinweis.
    public sealed partial class D07Tourdetails : UserControl
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        private readonly FirmenAnsichtKontext _kontext;
        private readonly TourRepository _repository = new();
        private readonly long? _tourId;
        private TourDetails? _details;
        private CancellationTokenSource? _abbruch;

        public D07Tourdetails(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            _tourId = kontext.Parameter as long?;
            Loaded += async (_, _) => await LadenAsync();
            Unloaded += (_, _) => _abbruch?.Cancel();
        }

        // ---- Laden ----

        private async Task LadenAsync()
        {
            if (_kontext.IstVorschau)
            {
                LadeFertig("In der Vorschau ohne Anmeldung werden keine Firmendaten geladen.");
                return;
            }
            if (_tourId is not long tourId)
            {
                LadeFertig("Keine Tour gewählt. Bitte in „Touren“ bei einer Tour auf „Öffnen“ klicken.");
                return;
            }

            var abbruch = new CancellationTokenSource();
            _abbruch = abbruch;
            try
            {
                TourDetails details = await _repository.DetailsLadenAsync(tourId, abbruch.Token);
                if (abbruch.IsCancellationRequested)
                    return;
                _details = details;
                Anzeigen(details);
                LadeFertig("");
            }
            catch (OperationCanceledException) when (abbruch.IsCancellationRequested)
            {
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (ArgumentException fehler)
            {
                LadeFertig(fehler.Message);
            }
            catch (Exception)
            {
                LadeFertig("Die Tour konnte nicht geladen werden. Bitte prüfen Sie die Verbindung und öffnen Sie sie erneut.");
            }
            finally
            {
                if (ReferenceEquals(_abbruch, abbruch))
                    _abbruch = null;
                abbruch.Dispose();
            }
        }

        private void LadeFertig(string text)
        {
            LadeAnzeige.IsActive = false;
            LadeText.Text = text;
            LadeBereich.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Anzeigen(TourDetails details)
        {
            TourDetailKopf t = details.Tour;
            bool offen = !IstAbgeschlossen(t.Status);

            KopfText.Text = string.Join(" · ", new[] { t.Tournummer, t.Bereich, t.Status }.Where(teil => !string.IsNullOrWhiteSpace(teil)));
            ZeitraumText.Text = $"{t.Zeitraum} · {t.Startort} → {t.Zielort}";

            FahrerText.Text = t.Fahrer ?? "Noch nicht zugeordnet";
            FahrzeugText.Text = t.Kennzeichen is null ? "Noch nicht zugeordnet" : $"{t.Kennzeichen} · {t.Fahrzeugtyp} {t.Aufbau}".Trim();
            GpsText.Text = "Noch keine Daten (Fahrer-App folgt)";
            LadungText.Text = t.Sendungen == 0
                ? "Keine Sendungen"
                : $"{(t.Plaetze == 1 ? "1 Platz" : $"{t.Plaetze} Plätze")} · {t.Lademeter.ToString("0.0", Deutsch)} Ldm · {t.GewichtKg.ToString("N0", Deutsch)} kg";

            // Nächster Stopp = erster ohne Ist-Ankunft.
            TourDetailStopp? naechster = details.Stopps.FirstOrDefault(s => s.IstAnkunft is null);
            if (naechster is null)
            {
                FensterText.Text = details.Stopps.Count == 0 ? "—" : "Alle Stopps erledigt";
                EtaText.Text = "—";
            }
            else
            {
                FensterText.Text = $"{naechster.Bezeichnung} · {naechster.FensterVon}–{naechster.FensterBis}";
                EtaText.Text = naechster.Eta is null
                    ? "Noch keine ETA"
                    : naechster.VerspaetungMinuten > 0
                        ? $"{naechster.Eta} · +{naechster.VerspaetungMinuten} Min."
                        : $"{naechster.Eta} · pünktlich";
            }

            bool unterwegs = t.Status.Contains("unterwegs", StringComparison.OrdinalIgnoreCase);
            var stopps = details.Stopps;
            StoppListe.ItemsSource = stopps
                .Select((s, i) => new TourDetailStoppZeile(
                    s,
                    istAktuell: offen && ReferenceEquals(s, naechster),
                    unterwegs: unterwegs,
                    istErster: i == 0,
                    istLetzter: i == stopps.Count - 1,
                    naechsterErreicht: i + 1 < stopps.Count && stopps[i + 1].IstAnkunft is not null))
                .ToList();
            StoppLeerText.Visibility = details.Stopps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            KarteButton.IsEnabled = details.Stopps.Any(s => s.Breitengrad is not null && s.Laengengrad is not null);
            FahrerKontaktButton.IsEnabled = t.Fahrer is not null;
            KundenButton.IsEnabled = DokumenteButton.IsEnabled = true;
            ZuweisenButton.IsEnabled = offen;
        }

        private static bool IstAbgeschlossen(string status) =>
            status.Contains("abgeschlossen", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("beendet", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("storniert", StringComparison.OrdinalIgnoreCase);

        // ---- Knöpfe ----

        // Öffnet die Live-Karte mit den Stopps dieser Tour (nur Stopps mit Koordinaten).
        private void AufKarte_Click(object sender, RoutedEventArgs e)
        {
            if (_details is not { } d)
                return;
            var stopps = d.Stopps
                .Where(s => s.Breitengrad is not null && s.Laengengrad is not null)
                .Select(s => new TourKartenStopp(s.Reihenfolge, s.Bezeichnung, $"{s.Datum} {s.FensterVon}–{s.FensterBis}",
                    s.IstAnkunft is not null, (double)s.Breitengrad!.Value, (double)s.Laengengrad!.Value))
                .ToList();
            _kontext.Oeffnen("D08", new TourKarte(d.Tour.Tournummer, stopps));
        }

        // Nachrichten, Kundenkommunikation und Frachtdokumente bekommen die Tour-Id als Startwert.
        private void FahrerKontaktieren_Click(object sender, RoutedEventArgs e) => TourOeffnen("D09");

        private void Kundeninformation_Click(object sender, RoutedEventArgs e) => TourOeffnen("D10");

        private void Frachtdokumente_Click(object sender, RoutedEventArgs e) => TourOeffnen("D11");

        private void Zuweisen_Click(object sender, RoutedEventArgs e) => TourOeffnen("D06");

        private void TourOeffnen(string ansicht)
        {
            if (_details is { } d)
                _kontext.Oeffnen(ansicht, d.Tour.Id);
        }
    }

    // Ein Stopp der Zeitleiste mit fertig formatierten Texten und Farben.
    // Die Farben kommen aus FirmenStil.xaml (passen also zu hell/dunkel und zur Akzentfarbe der Firma).
    public sealed class TourDetailStoppZeile
    {
        public TourDetailStoppZeile(TourDetailStopp s, bool istAktuell, bool unterwegs, bool istErster, bool istLetzter,
                                    bool naechsterErreicht)
        {
            // Wie im Wireframe „Celle · Markt Nord“: Bezeichnung und Kunde(n) der Sendungen.
            Titel = string.IsNullOrWhiteSpace(s.Kunden) ? s.Bezeichnung : $"{s.Bezeichnung} · {s.Kunden}";
            Unterzeile = $"{s.Art} · {s.Adresse}";
            ZeitfensterText = $"Fenster {s.FensterVon}–{s.FensterBis}";

            bool erreicht = s.IstAnkunft is not null;
            Status = s.IstAbfahrt is not null ? "Abgefahren"
                : erreicht ? "Angekommen"
                : istAktuell && unterwegs ? "Unterwegs"
                : s.Status;

            Zeit = s.IstAnkunft ?? (s.Eta is null ? "—" : $"ETA {s.Eta}");
            bool verspaetet = !erreicht && s.VerspaetungMinuten > 0;
            Verspaetung = verspaetet ? $"+{s.VerspaetungMinuten} Min. nach Fenster" : "";
            VerspaetungSichtbar = verspaetet ? Visibility.Visible : Visibility.Collapsed;

            Brush grau = R("MutedTextBrush"), rand = R("BorderBrush"), flaeche = R("SurfaceBrush"),
                  akzent = R("AccentBrush"), akzentText = R("AccentTextBrush"), fehler = R("ErrorBrush"),
                  text = R("TextBrush"), leer = new SolidColorBrush(Colors.Transparent);

            // Linien: grau, soweit die Tour schon gefahren ist, sonst in Rahmenfarbe.
            LinieObenSichtbar = istErster ? Visibility.Collapsed : Visibility.Visible;
            LinieUntenSichtbar = istLetzter ? Visibility.Collapsed : Visibility.Visible;
            LinieObenFarbe = erreicht ? grau : rand;
            LinieUntenFarbe = naechsterErreicht ? grau : rand;

            PunktText = erreicht ? "✓" : s.Reihenfolge.ToString(CultureInfo.InvariantCulture);
            KartenRandDicke = new Thickness(1);
            Deckkraft = 1;
            if (erreicht)
            {
                PunktFuellung = grau; PunktRand = grau; PunktSchrift = flaeche;
                PilleHintergrund = leer; PilleRand = rand; PilleSchrift = grau;
                KartenRand = rand;
                Deckkraft = 0.75;
            }
            else if (istAktuell && verspaetet)
            {
                PunktFuellung = fehler; PunktRand = fehler; PunktSchrift = flaeche;
                PilleHintergrund = leer; PilleRand = fehler; PilleSchrift = fehler;
                KartenRand = fehler;
                KartenRandDicke = new Thickness(2);
            }
            else if (istAktuell)
            {
                PunktFuellung = akzent; PunktRand = akzent; PunktSchrift = akzentText;
                PilleHintergrund = akzent; PilleRand = akzent; PilleSchrift = akzentText;
                KartenRand = akzent;
                KartenRandDicke = new Thickness(2);
            }
            else if (verspaetet)
            {
                PunktFuellung = flaeche; PunktRand = fehler; PunktSchrift = fehler;
                PilleHintergrund = leer; PilleRand = fehler; PilleSchrift = fehler;
                KartenRand = rand;
            }
            else
            {
                PunktFuellung = flaeche; PunktRand = rand; PunktSchrift = grau;
                PilleHintergrund = leer; PilleRand = rand; PilleSchrift = text;
                KartenRand = rand;
            }
        }

        private static Brush R(string schluessel) => (Brush)Application.Current.Resources[schluessel];

        public string Titel { get; }
        public string Unterzeile { get; }
        public string ZeitfensterText { get; }
        public string Status { get; }
        public string Zeit { get; }
        public string Verspaetung { get; }
        public Visibility VerspaetungSichtbar { get; }

        public Visibility LinieObenSichtbar { get; }
        public Visibility LinieUntenSichtbar { get; }
        public Brush LinieObenFarbe { get; }
        public Brush LinieUntenFarbe { get; }
        public string PunktText { get; }
        public Brush PunktFuellung { get; }
        public Brush PunktRand { get; }
        public Brush PunktSchrift { get; }
        public Brush PilleHintergrund { get; }
        public Brush PilleRand { get; }
        public Brush PilleSchrift { get; }
        public Brush KartenRand { get; }
        public Thickness KartenRandDicke { get; }
        public double Deckkraft { get; }
    }
}
