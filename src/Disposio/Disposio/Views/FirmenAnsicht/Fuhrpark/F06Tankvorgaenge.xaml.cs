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
    // F06: Startwert ist optional eine Fahrzeug-Id (aus F01 „Tankvorgänge“). Ohne Startwert: alle Fahrzeuge.
    // Die Rechteprüfung steckt im TankvorgangRepository (Rolle Fuhrparkmanager, nur eigene Firma).
    public sealed partial class F06Tankvorgaenge : UserControl
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        // Zeitraum-Auswahl: Text und Anzahl Tage zurück (null = alles).
        private static readonly (string Text, int? Tage)[] Zeitraeume =
        {
            ("Letzte 30 Tage", 30),
            ("Letzte 90 Tage", 90),
            ("Letzte 12 Monate", 365),
            ("Alle", null)
        };

        private readonly FirmenAnsichtKontext _kontext;
        private readonly TankvorgangRepository _repository = new();
        private readonly long? _startFahrzeug;
        private CancellationTokenSource? _laden;
        private bool _auswahlIgnorieren = true;
        private bool _fahrzeugeGeladen;

        public F06Tankvorgaenge(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            _startFahrzeug = kontext.Parameter as long?;

            foreach (var (text, tage) in Zeitraeume)
                ZeitraumBox.Items.Add(new ComboBoxItem { Content = text, Tag = tage });
            ZeitraumBox.SelectedIndex = 2; // 12 Monate
            FahrzeugBox.Items.Add(new ComboBoxItem { Content = "Alle Fahrzeuge", Tag = null });
            FahrzeugBox.SelectedIndex = 0;
            _auswahlIgnorieren = false;

            Loaded += async (_, _) => await LadenAsync();
            Unloaded += (_, _) => _laden?.Cancel();
        }

        private long? GewaehltesFahrzeug => (FahrzeugBox.SelectedItem as ComboBoxItem)?.Tag as long?;

        private DateOnly? AbDatum => (ZeitraumBox.SelectedItem as ComboBoxItem)?.Tag is int tage
            ? WartungsRegeln.Heute().AddDays(-tage)
            : null;

        private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_auswahlIgnorieren && IsLoaded)
                await LadenAsync();
        }

        private async void NeuLaden_Click(object sender, RoutedEventArgs e) => await LadenAsync();

        private void Zurueck_Click(object sender, RoutedEventArgs e) => _kontext.Oeffnen("F01");

        private async Task LadenAsync()
        {
            if (_kontext.IstVorschau)
            {
                StatusText.Text = "In der Vorschau ohne Anmeldung werden keine Tankvorgänge geladen.";
                NeuLadenButton.IsEnabled = false;
                return;
            }

            _laden?.Cancel();
            var abbruch = new CancellationTokenSource();
            _laden = abbruch;
            LadeAnzeige.IsActive = true;
            LadeAnzeige.Visibility = Visibility.Visible;
            StatusText.Text = "Tankvorgänge werden geladen …";
            try
            {
                if (!_fahrzeugeGeladen)
                {
                    IReadOnlyList<TankFahrzeug> fahrzeuge = await _repository.FahrzeugeLadenAsync(abbruch.Token);
                    if (abbruch.IsCancellationRequested)
                        return;
                    _auswahlIgnorieren = true;
                    foreach (TankFahrzeug f in fahrzeuge)
                    {
                        var eintrag = new ComboBoxItem { Content = f.Anzeige, Tag = f.Id };
                        FahrzeugBox.Items.Add(eintrag);
                        if (f.Id == _startFahrzeug)
                            FahrzeugBox.SelectedItem = eintrag;
                    }
                    _auswahlIgnorieren = false;
                    _fahrzeugeGeladen = true;
                }

                IReadOnlyList<Tankvorgang> liste = await _repository.LadenAsync(GewaehltesFahrzeug, AbDatum, abbruch.Token);
                if (abbruch.IsCancellationRequested)
                    return;
                Anzeigen(liste);
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
                StatusText.Text = "Die Tankvorgänge konnten nicht geladen werden. Bitte prüfen Sie die Verbindung und laden Sie erneut.";
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

        private void Anzeigen(IReadOnlyList<Tankvorgang> liste)
        {
            Dictionary<long, decimal> verbrauch = VerbrauchJeTankvorgang(liste);
            TankListe.ItemsSource = liste
                .Select(t => new TankZeile(t, verbrauch.TryGetValue(t.Id, out decimal v) ? v : null))
                .ToList();

            string zeitraum = (ZeitraumBox.SelectedItem as ComboBoxItem)?.Content as string ?? "";
            string fahrzeug = (FahrzeugBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Alle Fahrzeuge";
            ListenTitel.Text = GewaehltesFahrzeug is null ? "Tankvorgänge aller Fahrzeuge" : $"Tankvorgänge {fahrzeug}";
            StatusText.Text = (liste.Count == 1 ? "1 Tankvorgang" : $"{liste.Count} Tankvorgänge") + $" · {zeitraum}" +
                              (liste.Count >= 1000 ? " (nur die neuesten 1.000 angezeigt)" : "");
            LeerText.Text = $"Keine Tankvorgänge für {fahrzeug} im Zeitraum „{zeitraum}“.";
            LeerText.Visibility = liste.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SummenBereich.Visibility = liste.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (liste.Count == 0)
                return;

            // Summen. Strom (kWh) zählt bei der Menge und beim Literpreis nicht mit, bei den Kosten schon.
            var kraftstoff = liste.Where(t => !t.IstStrom).ToList();
            decimal liter = kraftstoff.Sum(t => t.Menge);
            decimal kWh = liste.Where(t => t.IstStrom).Sum(t => t.Menge);
            AnzahlWert.Text = liste.Count.ToString("N0", Deutsch);
            MengeWert.Text = kWh > 0
                ? $"{liter.ToString("N0", Deutsch)} l · {kWh.ToString("N0", Deutsch)} kWh"
                : $"{liter.ToString("N0", Deutsch)} l";
            KostenWert.Text = Euro(liste.Sum(t => t.Kosten));
            decimal kraftstoffKosten = kraftstoff.Sum(t => t.Kosten);
            PreisWert.Text = liter > 0 ? $"Ø {PreisText(kraftstoffKosten / liter)} pro Liter" : "";

            // Ø Verbrauch nur für ein einzelnes Fahrzeug: Menge ab dem 2. Tankvorgang ÷ gefahrene Kilometer.
            if (GewaehltesFahrzeug is null)
            {
                VerbrauchWert.Text = "–";
                VerbrauchHinweis.Text = "Für den Verbrauch oben ein Fahrzeug wählen.";
            }
            else
            {
                var mitKm = kraftstoff.Where(t => t.Kilometerstand is not null)
                    .OrderBy(t => t.Kilometerstand).ThenBy(t => t.Datum).ThenBy(t => t.Id).ToList();
                int strecke = mitKm.Count >= 2 ? mitKm[^1].Kilometerstand!.Value - mitKm[0].Kilometerstand!.Value : 0;
                if (strecke > 0)
                {
                    decimal menge = mitKm.Skip(1).Sum(t => t.Menge);
                    VerbrauchWert.Text = $"{(menge / strecke * 100).ToString("0.0", Deutsch)} l/100 km";
                    VerbrauchHinweis.Text = $"über {strecke.ToString("N0", Deutsch)} km im Zeitraum";
                }
                else
                {
                    VerbrauchWert.Text = "–";
                    VerbrauchHinweis.Text = "Dafür braucht es mindestens zwei Tankvorgänge mit Kilometerstand.";
                }
            }
        }

        // Verbrauch je Tankvorgang (l bzw. kWh pro 100 km) seit dem vorigen Tanken desselben Fahrzeugs und
        // derselben Energieart: getankte Menge ÷ gefahrene Kilometer. Nur, wenn der Kilometerstand gestiegen ist.
        private static Dictionary<long, decimal> VerbrauchJeTankvorgang(IEnumerable<Tankvorgang> liste)
        {
            var ergebnis = new Dictionary<long, decimal>();
            foreach (var gruppe in liste.Where(t => t.Kilometerstand is not null).GroupBy(t => (t.FahrzeugId, t.IstStrom)))
            {
                Tankvorgang? vorher = null;
                foreach (Tankvorgang t in gruppe.OrderBy(t => t.Kilometerstand).ThenBy(t => t.Datum).ThenBy(t => t.Id))
                {
                    if (vorher is not null && t.Kilometerstand > vorher.Kilometerstand && t.Menge > 0)
                        ergebnis[t.Id] = t.Menge / (t.Kilometerstand!.Value - vorher.Kilometerstand!.Value) * 100;
                    vorher = t;
                }
            }
            return ergebnis;
        }

        internal static string Euro(decimal betrag) => $"{betrag.ToString("N2", Deutsch)} €";

        // Literpreise mit drei Nachkommastellen, wie an der Tankstelle (1,729 €).
        internal static string PreisText(decimal preis) => $"{preis.ToString("0.000", Deutsch)} €";
    }

    // Eine Tabellenzeile in F06 mit fertig formatierten Texten.
    public sealed class TankZeile
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public TankZeile(Tankvorgang t, decimal? verbrauch)
        {
            Datum = t.Datum.ToString("dd.MM.yyyy", Deutsch);
            Kennzeichen = t.Kennzeichen;
            Fahrer = t.Fahrer ?? "";
            Menge = $"{t.Menge.ToString("0.##", Deutsch)} {t.Einheit}";
            Kosten = F06Tankvorgaenge.Euro(t.Kosten);
            Preis = t.PreisProEinheit is decimal preis ? $"{F06Tankvorgaenge.PreisText(preis)}/{t.Einheit}" : "–";
            Kilometerstand = t.Kilometerstand is int km ? $"{km.ToString("N0", Deutsch)} km" : "–";
            Verbrauch = verbrauch is decimal v ? $"≈ {v.ToString("0.0", Deutsch)} {t.Einheit}/100 km" : "";
            Tankstelle = string.IsNullOrWhiteSpace(t.Tankstelle) ? "–" : t.Tankstelle;
            Kraftstoff = t.Kraftstoff;
            Status = string.IsNullOrWhiteSpace(t.Status) ? "–" : t.Status;
            Notiz = t.Notiz ?? "";
            NotizSichtbarkeit = t.Notiz is null ? Visibility.Collapsed : Visibility.Visible;

            BelegAdresse = t.BelegAdresse is not null && Uri.TryCreate(t.BelegAdresse, UriKind.Absolute, out Uri? uri) ? uri : null;
            BelegText = t.Belege > 1 ? $"Beleg (+{t.Belege - 1})" : "Beleg";
            BelegSichtbarkeit = BelegAdresse is null ? Visibility.Collapsed : Visibility.Visible;
            OhneBelegSichtbarkeit = BelegAdresse is null ? Visibility.Visible : Visibility.Collapsed;
            BelegBeschreibung = $"Beleg vom {Datum} für {Kennzeichen} öffnen";
        }

        public string Datum { get; }
        public string Kennzeichen { get; }
        public string Fahrer { get; }
        public string Menge { get; }
        public string Kosten { get; }
        public string Preis { get; }
        public string Kilometerstand { get; }
        public string Verbrauch { get; }
        public string Tankstelle { get; }
        public string Kraftstoff { get; }
        public string Status { get; }
        public string Notiz { get; }
        public Visibility NotizSichtbarkeit { get; }
        public Uri? BelegAdresse { get; }
        public string BelegText { get; }
        public Visibility BelegSichtbarkeit { get; }
        public Visibility OhneBelegSichtbarkeit { get; }
        public string BelegBeschreibung { get; }
    }
}
