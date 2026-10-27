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
    // D05 „Tour planen“. Die Stopps entstehen aus gewählten Sendungen (Abholung + Zustellung) und aus
    // eigenen Stopps. Die Reihenfolge der Liste ist die Reihenfolge der Tour. Gespeichert wird alles
    // in einem Befehl (TourRepository.AnlegenAsync). Danach öffnet sich die Tourenübersicht (D04).
    public sealed partial class D05TourPlanen : UserControl
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        private readonly FirmenAnsichtKontext _kontext;
        private readonly TourRepository _repository = new();
        private readonly List<StoppPosten> _stopps = new();
        private IReadOnlyList<OffeneSendung> _sendungen = Array.Empty<OffeneSendung>();
        private CancellationTokenSource? _abbruch;
        // Aus D02 „Manuell verplanen“: diese Sendungen nach dem Laden gleich übernehmen.
        private readonly IReadOnlyList<long> _vorausgewaehlt;
        private bool _speichert;

        public D05TourPlanen(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;

            // D04 gibt den gerade angezeigten Tag mit, D02 zusätzlich die gewählten Sendungen. Sonst gilt heute.
            DateOnly tag = kontext.Parameter switch
            {
                DateOnly mitgegeben => mitgegeben,
                TourPlanenStart start => start.Tag,
                _ => DateOnly.FromDateTime(DateTime.Now)
            };
            _vorausgewaehlt = kontext.Parameter is TourPlanenStart mitSendungen ? mitSendungen.SendungIds : Array.Empty<long>();
            DatumBox.Date = tag.ToDateTime(TimeOnly.MinValue);
            StoppArtBox.ItemsSource = TourWerte.StoppArten;
            StoppArtBox.SelectedIndex = 0;
            StoppListeAnzeigen();

            Loaded += async (_, _) => await LadenAsync();
            Unloaded += (_, _) => _abbruch?.Cancel();
        }

        // ---- Laden ----

        private async Task LadenAsync()
        {
            if (_kontext.IstVorschau)
            {
                LadeFertig("In der Vorschau ohne Anmeldung werden keine Firmendaten geladen und keine Touren gespeichert.");
                return;
            }

            var abbruch = new CancellationTokenSource();
            _abbruch = abbruch;
            try
            {
                TourPlanungsdaten daten = await _repository.PlanungsdatenLadenAsync(abbruch.Token);
                if (abbruch.IsCancellationRequested)
                    return;

                if (string.IsNullOrWhiteSpace(TournummerBox.Text))
                    TournummerBox.Text = daten.TournummerVorschlag;

                // Wie in F02: ComboBoxItem mit Text als Content und der Id als Tag.
                foreach (AbteilungAuswahl abteilung in daten.Abteilungen)
                    AbteilungBox.Items.Add(new ComboBoxItem { Content = abteilung.Bezeichnung, Tag = abteilung.Id });
                if (daten.Abteilungen.Count == 1)
                    AbteilungBox.SelectedIndex = 0;

                // Erster Eintrag „später zuweisen“ (Tag null) = keine Auswahl.
                FahrerBox.Items.Add(new ComboBoxItem { Content = "– später zuweisen –", Tag = null });
                foreach (FahrerAuswahl fahrer in daten.Fahrer)
                    FahrerBox.Items.Add(new ComboBoxItem { Content = fahrer.Anzeige, Tag = fahrer.FahrerId });
                FahrerBox.SelectedIndex = 0;

                FahrzeugBox.Items.Add(new ComboBoxItem { Content = "– später zuweisen –", Tag = null });
                foreach (FahrzeugAuswahl fahrzeug in daten.Fahrzeuge)
                    FahrzeugBox.Items.Add(new ComboBoxItem { Content = fahrzeug.Anzeige, Tag = fahrzeug.Id });
                FahrzeugBox.SelectedIndex = 0;

                _sendungen = daten.Sendungen;
                // Vorausgewählte Sendungen übernehmen. Inzwischen verplante fehlen in der Liste und entfallen.
                foreach (OffeneSendung sendung in _sendungen.Where(s => _vorausgewaehlt.Contains(s.Id)))
                    SendungUebernehmen(sendung);
                SendungListeAnzeigen();
                StoppListeAnzeigen();

                SpeichernButton.IsEnabled = true;
                LadeFertig(daten.Abteilungen.Count == 0
                    ? "Ihre Firma hat noch keine Abteilung. Eine Tour braucht einen Bereich; bitte zuerst eine Abteilung anlegen."
                    : "");
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
                LadeFertig("Das Formular konnte nicht geladen werden. Bitte prüfen Sie die Verbindung und öffnen Sie es erneut.");
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
            LadeAnzeige.Visibility = Visibility.Collapsed;
            LadeText.Text = text;
            LadeText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---- Sendungen ----

        private void SendungListeAnzeigen()
        {
            var gewaehlt = _stopps.Where(s => s.AuftragId is not null).Select(s => s.AuftragId!.Value).ToHashSet();
            SendungListe.ItemsSource = _sendungen.Select(s => new SendungZeile(s, gewaehlt.Contains(s.Id))).ToList();
            SendungLeerText.Visibility = _sendungen.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Häkchen gesetzt: Abholung und Zustellung ans Ende anhängen. Häkchen weg: beide Stopps entfernen.
        private void Sendung_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { Tag: long id } box)
                return;
            OffeneSendung? sendung = _sendungen.FirstOrDefault(s => s.Id == id);
            if (sendung is null)
                return;

            _stopps.RemoveAll(s => s.AuftragId == id);
            if (box.IsChecked == true)
                SendungUebernehmen(sendung);
            StoppListeAnzeigen();
        }

        // Hängt Abholung und Zustellung einer Sendung ans Ende der Stoppliste.
        private void SendungUebernehmen(OffeneSendung sendung)
        {
            _stopps.Add(new StoppPosten(TourWerte.ArtAbholung, OrtAusAdresse(sendung.Abholadresse), sendung.Abholadresse,
                sendung.Abholdatum.ToDateTime(sendung.AbholfensterVon), sendung.Abholdatum.ToDateTime(sendung.AbholfensterBis),
                sendung.Id, TourWerte.ArtAbholung, sendung.Auftragsnummer));
            _stopps.Add(new StoppPosten(TourWerte.ArtZustellung, OrtAusAdresse(sendung.Lieferadresse), sendung.Lieferadresse,
                sendung.Lieferdatum.ToDateTime(sendung.LieferfensterVon), sendung.Lieferdatum.ToDateTime(sendung.LieferfensterBis),
                sendung.Id, TourWerte.ArtZustellung, sendung.Auftragsnummer));
        }

        // „Hafenstr. 1, 29221 Celle“ → „Celle“. Ohne Komma bleibt die ganze Adresse.
        private static string OrtAusAdresse(string adresse)
        {
            string teil = adresse.Split(',').Last().Trim();
            string ort = new string(teil.SkipWhile(zeichen => char.IsDigit(zeichen) || zeichen == ' ').ToArray()).Trim();
            return ort.Length > 0 ? ort : adresse.Trim();
        }

        // ---- Eigene Stopps ----

        private void StoppHinzufuegen_Click(object sender, RoutedEventArgs e)
        {
            StoppFehlerText.Visibility = Visibility.Collapsed;
            try
            {
                string art = StoppArtBox.SelectedItem as string ?? throw new ArgumentException("Bitte die Art des Stopps wählen.");
                string bezeichnung = Pflicht(StoppBezeichnungBox.Text, "Bitte eine Bezeichnung für den Stopp eingeben.");
                string adresse = Pflicht(StoppAdresseBox.Text, "Bitte eine Adresse für den Stopp eingeben.");
                DateOnly tag = TourTag();
                TimeOnly von = Uhrzeit(StoppVonBox.Text, "Von");
                TimeOnly bis = Uhrzeit(StoppBisBox.Text, "Bis");
                if (bis < von)
                    throw new ArgumentException("Beim Zeitfenster muss „Bis“ nach „Von“ liegen.");

                _stopps.Add(new StoppPosten(art, bezeichnung, adresse, tag.ToDateTime(von), tag.ToDateTime(bis), null, null, null));
                StoppBezeichnungBox.Text = StoppAdresseBox.Text = StoppVonBox.Text = StoppBisBox.Text = "";
                StoppListeAnzeigen();
            }
            catch (ArgumentException fehler)
            {
                StoppFehlerText.Text = fehler.Message;
                StoppFehlerText.Visibility = Visibility.Visible;
            }
        }

        private void StoppHoch_Click(object sender, RoutedEventArgs e) => Verschieben(sender, -1);

        private void StoppRunter_Click(object sender, RoutedEventArgs e) => Verschieben(sender, +1);

        private void Verschieben(object sender, int richtung)
        {
            if (sender is not Button { Tag: int index })
                return;
            int ziel = index + richtung;
            if (index < 0 || ziel < 0 || ziel >= _stopps.Count)
                return;
            (_stopps[index], _stopps[ziel]) = (_stopps[ziel], _stopps[index]);
            StoppListeAnzeigen();
        }

        // Nur eigene Stopps haben ✕. Stopps einer Sendung verschwinden über das Häkchen bei der Sendung.
        private void StoppEntfernen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: int index } && index >= 0 && index < _stopps.Count && _stopps[index].AuftragId is null)
            {
                _stopps.RemoveAt(index);
                StoppListeAnzeigen();
            }
        }

        private void StoppListeAnzeigen()
        {
            StoppListe.ItemsSource = _stopps.Select((stopp, index) => new StoppZeile(stopp, index, _stopps.Count)).ToList();
            StoppLeerText.Visibility = _stopps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- Speichern ----

        private void Abbrechen_Click(object sender, RoutedEventArgs e) => _kontext.Oeffnen("D04");

        private async void Speichern_Click(object sender, RoutedEventArgs e)
        {
            if (_speichert || _kontext.IstVorschau)
                return;

            FehlerText.Visibility = Visibility.Collapsed;
            TourEntwurf entwurf;
            try
            {
                entwurf = EntwurfLesen();
            }
            catch (ArgumentException fehler)
            {
                FehlerAnzeigen(fehler.Message);
                return;
            }

            _speichert = true;
            SpeichernButton.IsEnabled = false;
            SpeichernButton.Content = "Wird gespeichert …";
            try
            {
                await _repository.AnlegenAsync(entwurf);
                // Zur Tourenübersicht, auf den Tag der neuen Tour, mit einer Erfolgsmeldung.
                _kontext.Oeffnen("D04", new TourGespeichert(DateOnly.FromDateTime(entwurf.Start),
                    $"Tour {entwurf.Tournummer} wurde angelegt."));
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (TournummerVergebenException fehler)
            {
                FehlerAnzeigen(fehler.Message);
            }
            catch (ArgumentException fehler)
            {
                FehlerAnzeigen(fehler.Message);
            }
            catch (Exception)
            {
                // Keine technischen Details anzeigen. Die Eingaben bleiben stehen.
                FehlerAnzeigen("Die Tour konnte nicht gespeichert werden. Bitte prüfen Sie die Verbindung und versuchen Sie es erneut.");
            }
            finally
            {
                _speichert = false;
                SpeichernButton.IsEnabled = true;
                SpeichernButton.Content = "Tour speichern";
            }
        }

        // Liest das Formular. Die Prüfregeln stehen zusätzlich im Repository.
        private TourEntwurf EntwurfLesen()
        {
            if ((AbteilungBox.SelectedItem as ComboBoxItem)?.Tag is not long abteilungId)
                throw new ArgumentException("Bitte einen Bereich (Abteilung) wählen.");

            DateOnly tag = TourTag();
            DateTime start = tag.ToDateTime(Uhrzeit(StartBox.Text, "Start"));
            DateTime ende = tag.ToDateTime(Uhrzeit(EndeBox.Text, "Ende"));
            if (ende <= start)
                throw new ArgumentException("Das Tourende muss nach dem Tourstart liegen.");

            return new TourEntwurf(
                TournummerBox.Text,
                abteilungId,
                start,
                ende,
                StartortBox.Text,
                ZielortBox.Text,
                (FahrerBox.SelectedItem as ComboBoxItem)?.Tag as long?,
                (FahrzeugBox.SelectedItem as ComboBoxItem)?.Tag as long?,
                _stopps.Select(s => new TourStoppEntwurf(s.Art, s.Bezeichnung, s.Adresse, s.Von, s.Bis, s.AuftragId, s.Vorgang)).ToList());
        }

        private void FehlerAnzeigen(string text)
        {
            FehlerText.Text = text;
            FehlerText.Visibility = Visibility.Visible;
        }

        private DateOnly TourTag() =>
            DatumBox.Date is DateTimeOffset datum
                ? DateOnly.FromDateTime(datum.Date)
                : throw new ArgumentException("Bitte ein Datum für die Tour wählen.");

        // Erlaubt „8:00“, „08:00“ und „0800“.
        private static TimeOnly Uhrzeit(string? text, string feld)
        {
            string wert = (text ?? "").Trim();
            if (wert.Length == 4 && wert.All(char.IsDigit))
                wert = wert[..2] + ":" + wert[2..];
            if (TimeOnly.TryParseExact(wert, new[] { "H:mm", "HH:mm" }, Deutsch, DateTimeStyles.None, out TimeOnly zeit))
                return zeit;
            throw new ArgumentException($"Bitte bei „{feld}“ eine Uhrzeit wie 08:30 eingeben.");
        }

        private static string Pflicht(string? text, string meldung)
        {
            string wert = (text ?? "").Trim();
            return wert.Length > 0 ? wert : throw new ArgumentException(meldung);
        }
    }

    // Ein Stopp im Formular. Bei Stopps aus einer Sendung sind AuftragId, Vorgang und Auftragsnummer gesetzt.
    internal sealed record StoppPosten(string Art, string Bezeichnung, string Adresse, DateTime Von, DateTime Bis,
        long? AuftragId, string? Vorgang, string? Auftragsnummer);

    // Eine Sendung in der Liste mit Häkchen.
    public sealed class SendungZeile
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public SendungZeile(OffeneSendung sendung, bool ausgewaehlt)
        {
            Id = sendung.Id;
            Ausgewaehlt = ausgewaehlt;
            Titel = string.IsNullOrWhiteSpace(sendung.Kunde)
                ? $"{sendung.Auftragsnummer} · Priorität {sendung.Prioritaet}"
                : $"{sendung.Auftragsnummer} · {sendung.Kunde} · Priorität {sendung.Prioritaet}";
            Abholung = $"Abholung: {sendung.Abholdatum.ToString("dd.MM.", Deutsch)} {sendung.AbholfensterVon:HH\\:mm}–{sendung.AbholfensterBis:HH\\:mm} · {sendung.Abholadresse}";
            Zustellung = $"Zustellung: {sendung.Lieferdatum.ToString("dd.MM.", Deutsch)} {sendung.LieferfensterVon:HH\\:mm}–{sendung.LieferfensterBis:HH\\:mm} · {sendung.Lieferadresse}";
        }

        public long Id { get; }
        public bool? Ausgewaehlt { get; }
        public string Titel { get; }
        public string Abholung { get; }
        public string Zustellung { get; }
    }

    // Eine Zeile der Stoppliste mit fertig formatierten Texten.
    public sealed class StoppZeile
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        internal StoppZeile(StoppPosten stopp, int index, int anzahl)
        {
            Index = index;
            Nummer = (index + 1).ToString(Deutsch);
            Art = stopp.Art;
            Bezeichnung = stopp.Bezeichnung;
            Adresse = stopp.Adresse;
            Zeitfenster = $"{stopp.Von.ToString("dd.MM.", Deutsch)} {stopp.Von:HH\\:mm}–{stopp.Bis:HH\\:mm}";
            Herkunft = stopp.Auftragsnummer is null ? "Eigener Stopp" : $"Sendung {stopp.Auftragsnummer}";
            KannHoch = index > 0;
            KannRunter = index < anzahl - 1;
            EntfernenSichtbar = stopp.AuftragId is null ? Visibility.Visible : Visibility.Collapsed;
        }

        public int Index { get; }
        public string Nummer { get; }
        public string Art { get; }
        public string Bezeichnung { get; }
        public string Adresse { get; }
        public string Zeitfenster { get; }
        public string Herkunft { get; }
        public bool KannHoch { get; }
        public bool KannRunter { get; }
        public Visibility EntfernenSichtbar { get; }
    }
}
