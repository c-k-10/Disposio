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
    // D02 lädt die Sendungen eines Tages. Das Datum lädt neu aus der Datenbank; Suche und Status filtern
    // nur die geladene Liste. Angehakte (unverplante) Sendungen gehen an „Planungsvorschlag prüfen“ (D13)
    // oder „Manuell verplanen“ (D05).
    public sealed partial class D02Sendungen : UserControl
    {
        private const string AlleStatus = "Alle";
        private const string NurUnverplant = "Nur unverplante";

        private readonly FirmenAnsichtKontext _kontext;
        private readonly SendungRepository _repository = new();
        private readonly HashSet<long> _ausgewaehlt = new();
        private IReadOnlyList<SendungUebersicht> _sendungen = Array.Empty<SendungUebersicht>();
        private DateOnly? _geladenerTag;
        private CancellationTokenSource? _laden;
        // Nach dem Speichern in D03 kommt eine Erfolgsmeldung als Startwert. Sie erscheint nur einmal.
        private string? _meldung;

        public D02Sendungen(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;

            // Nach dem Speichern einer Sendung (D03) springt die Liste auf deren Abholtag.
            if (kontext.Parameter is SendungGespeichert gespeichert)
            {
                DatumAuswahl.Date = gespeichert.Tag.ToDateTime(TimeOnly.MinValue);
                _meldung = gespeichert.Meldung;
            }
            else
            {
                DatumAuswahl.Date = DateTimeOffset.Now.Date;
            }
            StatusListeAufbauen();

            Loaded += async (_, _) => await AnwendenAsync();
            Unloaded += (_, _) => _laden?.Cancel();
        }

        private DateOnly GewaehlterTag =>
            DatumAuswahl.Date is DateTimeOffset datum ? DateOnly.FromDateTime(datum.Date) : DateOnly.FromDateTime(DateTime.Now);

        // ---- Knöpfe ----

        private void SendungErfassen_Click(object sender, RoutedEventArgs e) => _kontext.Oeffnen("D03");

        private void Details_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: long id })
                _kontext.Oeffnen("D03", id);
        }

        // Öffnet D16: freie Fahrer und Fahrzeuge für genau diese Sendung.
        private void Zuweisen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: long id })
                _kontext.Oeffnen("D16", id);
        }

        private async void FilterAnwenden_Click(object sender, RoutedEventArgs e) => await AnwendenAsync();

        private async void SuchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await AnwendenAsync();
            }
        }

        private void Auswahl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { Tag: long id } box)
                return;
            if (box.IsChecked == true)
                _ausgewaehlt.Add(id);
            else
                _ausgewaehlt.Remove(id);
            AuswahlAnzeigen();
        }

        // D13 ist noch ein Platzhalter. Die Ids werden trotzdem schon mitgegeben.
        private void Planungsvorschlag_Click(object sender, RoutedEventArgs e) =>
            _kontext.Oeffnen("D13", AuswahlIds());

        // D05 startet mit dem gewählten Tag und hakt die Sendungen dort schon an.
        private void ManuellVerplanen_Click(object sender, RoutedEventArgs e) =>
            _kontext.Oeffnen("D05", new TourPlanenStart(GewaehlterTag, AuswahlIds()));

        private IReadOnlyList<long> AuswahlIds() =>
            _sendungen.Where(s => _ausgewaehlt.Contains(s.Id)).Select(s => s.Id).ToArray();

        // ---- Laden und Filtern ----

        private async Task AnwendenAsync()
        {
            DateOnly tag = GewaehlterTag;
            if (_geladenerTag != tag)
                await LadenAsync(tag);
            else
                ListeFiltern();
        }

        private async Task LadenAsync(DateOnly tag)
        {
            if (_kontext.IstVorschau)
            {
                UnverplantText.Text = "Vorschau ohne Anmeldung";
                StatusText.Text = "In der Vorschau ohne Anmeldung werden keine Sendungen geladen.";
                return;
            }

            _laden?.Cancel();
            var abbruch = new CancellationTokenSource();
            _laden = abbruch;
            LadeAnzeige.IsActive = true;
            LadeAnzeige.Visibility = Visibility.Visible;
            StatusText.Text = "Sendungen werden geladen …";
            try
            {
                IReadOnlyList<SendungUebersicht> sendungen = await _repository.TagLadenAsync(tag, abbruch.Token);
                if (abbruch.IsCancellationRequested)
                    return;

                _sendungen = sendungen;
                _geladenerTag = tag;
                // Ein anderer Tag: alte Häkchen gelten nicht mehr.
                _ausgewaehlt.Clear();
                StatusListeAufbauen();
                ListeFiltern();
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
                // Keine technischen Details anzeigen. Die zuletzt geladene Liste bleibt sichtbar.
                StatusText.Text = "Die Sendungen konnten nicht geladen werden. Bitte prüfen Sie die Verbindung und versuchen Sie es erneut.";
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

            List<SendungUebersicht> treffer = _sendungen
                .Where(s => status == AlleStatus
                            || (status == NurUnverplant && !s.IstVerplant)
                            || string.Equals(s.Status, status, StringComparison.CurrentCultureIgnoreCase))
                .Where(s => suche.Length == 0 || Suchtexte(s).Any(text =>
                    text?.Contains(suche, StringComparison.CurrentCultureIgnoreCase) == true))
                .ToList();

            SendungListe.ItemsSource = treffer.Select(s => new SendungListenZeile(s, _ausgewaehlt.Contains(s.Id))).ToList();

            int unverplant = _sendungen.Count(s => !s.IstVerplant);
            UnverplantText.Text = unverplant == 1 ? "1 unverplante Sendung" : $"{unverplant} unverplante Sendungen";
            string anzahl = treffer.Count == _sendungen.Count
                ? (_sendungen.Count == 1 ? "1 Sendung" : $"{_sendungen.Count} Sendungen")
                : $"{treffer.Count} von {_sendungen.Count} Sendungen";
            StatusText.Text = _meldung is null ? anzahl : $"{_meldung} · {anzahl}";
            _meldung = null;

            LeerText.Text = _sendungen.Count == 0
                ? "Für diesen Tag gibt es keine Sendungen. Mit „+ Sendung erfassen“ legen Sie eine an."
                : "Keine Sendung passt zum Filter.";
            LeerText.Visibility = treffer.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AuswahlAnzeigen();
        }

        private void AuswahlAnzeigen()
        {
            int anzahl = _ausgewaehlt.Count;
            VorschlagButton.IsEnabled = ManuellButton.IsEnabled = anzahl > 0;
            AuswahlText.Text = anzahl switch
            {
                0 => "Haken Sie unverplante Sendungen an, um sie zu einer Tour zusammenzustellen.",
                1 => "1 Sendung ausgewählt.",
                _ => $"{anzahl} Sendungen ausgewählt."
            };
        }

        // „Alle“, „Nur unverplante“ und jeder Status, der an diesem Tag vorkommt.
        private void StatusListeAufbauen()
        {
            string? vorher = StatusAuswahl.SelectedItem as string;
            var eintraege = new List<string> { AlleStatus, NurUnverplant };
            eintraege.AddRange(_sendungen.Select(s => s.Status).Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(text => text, StringComparer.CurrentCultureIgnoreCase));

            StatusAuswahl.ItemsSource = eintraege;
            StatusAuswahl.SelectedItem = vorher is not null && eintraege.Contains(vorher) ? vorher : AlleStatus;
        }

        private static IEnumerable<string?> Suchtexte(SendungUebersicht s) => new[]
        {
            s.Kundenreferenz, s.Auftragsnummer, s.Kunde, s.Abholadresse, s.Lieferadresse, s.Gutart, s.Tournummer
        };
    }

    // Eine Tabellenzeile mit fertig formatierten Texten. Die XAML-Vorlage bindet mit {x:Bind Name} daran.
    public sealed class SendungListenZeile
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public SendungListenZeile(SendungUebersicht s, bool ausgewaehlt)
        {
            Id = s.Id;
            KannGeplantWerden = !s.IstVerplant;
            Ausgewaehlt = ausgewaehlt && KannGeplantWerden;
            ReferenzUndKunde = string.IsNullOrWhiteSpace(s.Kunde) ? s.Referenz : $"{s.Referenz} · {s.Kunde}";
            Strecke = $"{Adressen.Ort(s.Abholadresse)} → {Adressen.Ort(s.Lieferadresse)}";
            // Wie im Wireframe das Lieferfenster. Bei mehrtägigen Sendungen mit Lieferdatum.
            Fenster = s.Abholdatum == s.Lieferdatum
                ? $"{s.LieferfensterVon}–{s.LieferfensterBis}"
                : $"{s.Lieferdatum.ToString("dd.MM.", Deutsch)} {s.LieferfensterVon}–{s.LieferfensterBis}";
            string plaetze = s.Anzahl == 1 ? "1 Platz" : $"{s.Anzahl} Plätze";
            GutUndKapazitaet = $"{s.Gutart} · {plaetze} / {s.Lademeter.ToString("0.0", Deutsch)} Ldm";
            Status = s.Status;
            TourText = s.Tournummer is null ? "" : $"Tour {s.Tournummer}";
            TourSichtbar = s.Tournummer is null ? Visibility.Collapsed : Visibility.Visible;
            ZuweisenSichtbar = s.IstVerplant ? Visibility.Collapsed : Visibility.Visible;
        }

        public long Id { get; }
        public bool? Ausgewaehlt { get; }
        public bool KannGeplantWerden { get; }
        public string ReferenzUndKunde { get; }
        public string Strecke { get; }
        public string Fenster { get; }
        public string GutUndKapazitaet { get; }
        public string Status { get; }
        public string TourText { get; }
        public Visibility TourSichtbar { get; }
        public Visibility ZuweisenSichtbar { get; }
    }
}
