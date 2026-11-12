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

namespace Disposio.Views
{
    // D05 „Tour planen“ als Assistent in 4 Schritten (wie D03): Tourdaten → Sendungen & Stopps →
    // Fahrer & Fahrzeug → Prüfen & speichern. Die Stopps entstehen aus gewählten Sendungen (Abholung +
    // Zustellung) und aus eigenen Stopps. Die Reihenfolge der Liste ist die Reihenfolge der Tour.
    // Fehlerhafte Felder werden rot umrandet. Gespeichert wird alles in einem Befehl
    // (TourRepository.AnlegenAsync). Danach öffnet sich die Tourenübersicht (D04).
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

        // Rot markierte Felder (TextBox, ComboBox, Datum) und Karten (z. B. „Stopps in Reihenfolge“).
        private readonly HashSet<FrameworkElement> _markiert = new();

        // Die vier Schritte: Karten im Formular, Knopf links und die Felder, die dazugehören.
        private sealed record Schritt(string Titel, string Hinweis, Border[] Karten, Button Knopf, Rectangle Balken,
                                      Ellipse Punkt, TextBlock Symbol, string Nummer, FrameworkElement[] Felder);

        private readonly SolidColorBrush _gruen = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x2E, 0xA6, 0x6A));
        private Schritt[] _schritte = Array.Empty<Schritt>();
        private int _aktuell;

        // Automatisch eingetragene Adresse der Spedition (Start- und Zielort). Null = nichts vorbelegt.
        private string? _firmenadresse;

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
            SchritteAnlegen();
            MarkierungZuruecksetzenBeiEingabe();
            StartortBox.TextChanged += (_, _) => OrtHinweiseAktualisieren();
            ZielortBox.TextChanged += (_, _) => OrtHinweiseAktualisieren();
            StoppListeAnzeigen();
            SchrittZeigen(0);

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

                // Start und Ziel ist normalerweise die eigene Spedition. Nur leere Felder vorbelegen;
                // die Adresse lässt sich danach frei ändern.
                if (daten.Firmenadresse is { Length: > 0 } firma)
                {
                    _firmenadresse = firma.Length > 200 ? firma[..200] : firma;
                    if (string.IsNullOrWhiteSpace(StartortBox.Text))
                        StartortBox.Text = _firmenadresse;
                    if (string.IsNullOrWhiteSpace(ZielortBox.Text))
                        ZielortBox.Text = _firmenadresse;
                    OrtHinweiseAktualisieren();
                }

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
                SchritteAktualisieren();
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
            foreach (FrameworkElement feld in new FrameworkElement[]
                     { StoppArtBox, StoppBezeichnungBox, StoppAdresseBox, StoppVonBox, StoppBisBox })
                MarkierungEntfernen(feld);

            // Alle Felder des eigenen Stopps prüfen und die fehlerhaften rot umranden.
            var fehler = new List<(FrameworkElement Feld, string Meldung)>();
            string? art = StoppArtBox.SelectedItem as string;
            if (art is null)
                fehler.Add((StoppArtBox, "Bitte die Art des Stopps wählen."));
            if (string.IsNullOrWhiteSpace(StoppBezeichnungBox.Text))
                fehler.Add((StoppBezeichnungBox, "Bitte eine Bezeichnung für den Stopp eingeben."));
            if (string.IsNullOrWhiteSpace(StoppAdresseBox.Text))
                fehler.Add((StoppAdresseBox, "Bitte eine Adresse für den Stopp eingeben."));
            TimeOnly? von = Zeit(StoppVonBox, "Von", fehler);
            TimeOnly? bis = Zeit(StoppBisBox, "Bis", fehler);
            if (von is { } v && bis is { } b && b < v)
                fehler.Add((StoppBisBox, "Beim Zeitfenster muss „Bis“ nach „Von“ liegen."));
            if (DatumBox.Date is null)
                fehler.Add((DatumBox, "Bitte zuerst in Schritt 1 ein Datum für die Tour wählen."));

            if (fehler.Count > 0)
            {
                Markieren(fehler);
                StoppFehlerText.Text = fehler.Count == 1
                    ? fehler[0].Meldung
                    : string.Join("\n", fehler.Select(f => "• " + f.Meldung));
                StoppFehlerText.Visibility = Visibility.Visible;
                return;
            }

            DateOnly tag = TourTag();
            _stopps.Add(new StoppPosten(art!, StoppBezeichnungBox.Text.Trim(), StoppAdresseBox.Text.Trim(),
                tag.ToDateTime(von!.Value), tag.ToDateTime(bis!.Value), null, null, null));
            StoppBezeichnungBox.Text = StoppAdresseBox.Text = StoppVonBox.Text = StoppBisBox.Text = "";
            // Das Leeren der Felder soll keine roten Rahmen hinterlassen.
            foreach (FrameworkElement feld in new FrameworkElement[]
                     { StoppArtBox, StoppBezeichnungBox, StoppAdresseBox, StoppVonBox, StoppBisBox })
                MarkierungEntfernen(feld);
            StoppListeAnzeigen();
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
            // Die Stoppliste hat sich geändert: eine rote Markierung der Liste gilt nicht mehr.
            MarkierungEntfernen(ReihenfolgeKarte);
            SchritteAktualisieren();
        }

        // ---- Speichern ----

        private void Abbrechen_Click(object sender, RoutedEventArgs e) => _kontext.Oeffnen("D04");

        private async void Speichern_Click(object sender, RoutedEventArgs e)
        {
            if (_speichert || _kontext.IstVorschau)
                return;

            FehlerText.Visibility = Visibility.Collapsed;
            AlleMarkierungenEntfernen();

            // Erst alle Felder prüfen und die fehlerhaften rot umranden, dann erst speichern.
            var fehlerListe = FelderPruefen();
            if (fehlerListe.Count > 0)
            {
                FehlerMarkieren(fehlerListe);
                return;
            }

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
                // Die Datenbank meldet eine schon vergebene Tournummer: das Feld in Schritt 1 markieren.
                FehlerMarkieren(new List<(FrameworkElement, string)> { (TournummerBox, fehler.Message) });
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

        // Hinweis unter Start-/Zielort, solange dort noch die automatisch eingetragene Firmenadresse steht.
        private void OrtHinweiseAktualisieren()
        {
            bool start = _firmenadresse is not null && StartortBox.Text == _firmenadresse;
            bool ziel = _firmenadresse is not null && ZielortBox.Text == _firmenadresse;
            StartortHinweis.Visibility = start ? Visibility.Visible : Visibility.Collapsed;
            ZielortHinweis.Visibility = ziel ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- Prüfen und rot markieren (wie D03) ----

        // Prüft alle Schritte. Gleiche Regeln wie TourRepository, aber jeder Fehler gehört zu einem Feld
        // (oder zur Stoppliste), damit man sieht, wo etwas fehlt.
        private List<(FrameworkElement Feld, string Meldung)> FelderPruefen()
        {
            var fehler = new List<(FrameworkElement, string)>();
            void Pflicht(TextBox feld, string name)
            {
                if (string.IsNullOrWhiteSpace(feld.Text))
                    fehler.Add((feld, $"{name} fehlt."));
            }

            // Schritt 1: Tourdaten
            Pflicht(TournummerBox, "Tournummer");
            if ((AbteilungBox.SelectedItem as ComboBoxItem)?.Tag is not long)
                fehler.Add((AbteilungBox, "Bitte einen Bereich (Abteilung) wählen."));
            if (DatumBox.Date is null)
                fehler.Add((DatumBox, "Bitte ein Datum für die Tour wählen."));
            TimeOnly? start = Zeit(StartBox, "Start", fehler);
            TimeOnly? ende = Zeit(EndeBox, "Ende", fehler);
            if (start is { } s && ende is { } e && e <= s)
                fehler.Add((EndeBox, "Das Tourende muss nach dem Tourstart liegen."));
            Pflicht(StartortBox, "Startort");
            Pflicht(ZielortBox, "Zielort");

            // Schritt 2: Stopps
            if (_stopps.Count == 0)
                fehler.Add((ReihenfolgeKarte, "Bitte mindestens eine Sendung auswählen oder einen eigenen Stopp hinzufügen."));
            else if (_stopps.Count > 100)
                fehler.Add((ReihenfolgeKarte, "Eine Tour darf höchstens 100 Stopps haben."));
            for (int i = 0; i < _stopps.Count; i++)
            {
                if (_stopps[i].Vorgang == TourWerte.ArtZustellung && _stopps[i].AuftragId is long id &&
                    _stopps.Skip(i + 1).Any(x => x.AuftragId == id && x.Vorgang == TourWerte.ArtAbholung))
                {
                    fehler.Add((ReihenfolgeKarte,
                        $"Bei Sendung {_stopps[i].Auftragsnummer} steht die Zustellung vor der Abholung. Bitte die Reihenfolge ändern."));
                    break;
                }
            }

            // Schritt 3 (Fahrer & Fahrzeug) ist freiwillig.
            return fehler;
        }

        // Prüft eine Uhrzeit. Leer oder ungültig: Fehler am Feld.
        private static TimeOnly? Zeit(TextBox feld, string name, List<(FrameworkElement, string)> fehler)
        {
            if (UhrzeitVersuchen(feld.Text, out TimeOnly zeit))
                return zeit;
            fehler.Add((feld, string.IsNullOrWhiteSpace(feld.Text)
                ? $"{name} fehlt."
                : $"Bitte bei „{name}“ eine Uhrzeit wie 08:30 eingeben."));
            return null;
        }

        // Rahmen rot, Meldung als Tooltip am Feld.
        private void Markieren(List<(FrameworkElement Feld, string Meldung)> fehler)
        {
            Brush rot = Application.Current.Resources.TryGetValue("ErrorBrush", out object? wert) && wert is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Red);
            foreach (var (feld, meldung) in fehler)
            {
                switch (feld)
                {
                    case Control control:
                        control.BorderBrush = rot;
                        control.BorderThickness = new Thickness(2);
                        break;
                    case Border karte:
                        karte.BorderBrush = rot;
                        karte.BorderThickness = new Thickness(2);
                        break;
                }
                ToolTipService.SetToolTip(feld, meldung);
                _markiert.Add(feld);
            }
            SchritteAktualisieren();
        }

        // Markieren, unten alle Meldungen auflisten und zum Schritt des ersten Fehlers wechseln.
        private void FehlerMarkieren(List<(FrameworkElement Feld, string Meldung)> fehler)
        {
            int schritt = SchrittVon(fehler[0].Feld);
            if (schritt >= 0 && schritt != _aktuell)
                SchrittZeigen(schritt);
            Markieren(fehler);
            FehlerAnzeigen(fehler.Count == 1
                ? fehler[0].Meldung
                : $"Bitte die {fehler.Count} rot markierten Angaben prüfen:\n" +
                  string.Join("\n", fehler.Select(f => "• " + f.Meldung).Distinct()));

            // Nur hinscrollen, nicht fokussieren: Ein fokussiertes Feld zeigt sonst den Fokusrahmen statt Rot.
            FrameworkElement erstes = fehler[0].Feld;
            DispatcherQueue.TryEnqueue(() =>
                erstes.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3 }));
        }

        private void MarkierungEntfernen(FrameworkElement feld)
        {
            if (!_markiert.Remove(feld))
                return;
            // Zurück auf die Werte aus dem Stil (FormTextBox, FormComboBox, FormDatePicker, KarteStyle).
            if (feld is Control control)
            {
                control.ClearValue(Control.BorderBrushProperty);
                control.ClearValue(Control.BorderThicknessProperty);
            }
            else if (feld is Border karte)
            {
                karte.ClearValue(Border.BorderBrushProperty);
                karte.ClearValue(Border.BorderThicknessProperty);
            }
            ToolTipService.SetToolTip(feld, null);
            if (_markiert.Count == 0)
                FehlerText.Visibility = Visibility.Collapsed;
        }

        private void AlleMarkierungenEntfernen()
        {
            foreach (FrameworkElement feld in _markiert.ToList())
                MarkierungEntfernen(feld);
        }

        // Sobald man ein markiertes Feld ändert, verschwindet sein roter Rahmen.
        private void MarkierungZuruecksetzenBeiEingabe()
        {
            foreach (TextBox box in new[]
                     {
                         TournummerBox, StartBox, EndeBox, StartortBox, ZielortBox,
                         StoppBezeichnungBox, StoppAdresseBox, StoppVonBox, StoppBisBox
                     })
                box.TextChanged += (_, _) => { MarkierungEntfernen(box); SchritteAktualisieren(); };

            foreach (ComboBox box in new[] { AbteilungBox, StoppArtBox, FahrerBox, FahrzeugBox })
                box.SelectionChanged += (_, _) => { MarkierungEntfernen(box); SchritteAktualisieren(); };

            DatumBox.DateChanged += (_, _) => { MarkierungEntfernen(DatumBox); SchritteAktualisieren(); };
        }

        // ---- Assistent: Schritte ----

        private void SchritteAnlegen()
        {
            _schritte = new[]
            {
                new Schritt("Tourdaten", "Tournummer, Bereich, Tag, Uhrzeiten sowie Start- und Zielort der Tour.",
                    new[] { TourdatenKarte }, NavSchritt1, NavSchritt1Balken, NavSchritt1Punkt, NavSchritt1Symbol, "1",
                    new FrameworkElement[] { TournummerBox, AbteilungBox, DatumBox, StartBox, EndeBox, StartortBox, ZielortBox }),
                new Schritt("Sendungen & Stopps", "Offene Sendungen übernehmen, bei Bedarf eigene Stopps ergänzen und die Reihenfolge festlegen.",
                    new[] { SendungenKarte, EigenerStoppKarte, ReihenfolgeKarte }, NavSchritt2, NavSchritt2Balken, NavSchritt2Punkt,
                    NavSchritt2Symbol, "2",
                    new FrameworkElement[]
                    {
                        ReihenfolgeKarte, StoppArtBox, StoppBezeichnungBox, StoppAdresseBox, StoppVonBox, StoppBisBox
                    }),
                new Schritt("Fahrer & Fahrzeug", "Freiwillig: Ohne Auswahl wird die Tour „Geplant“ und kann später zugewiesen werden.",
                    new[] { FahrerKarte }, NavSchritt3, NavSchritt3Balken, NavSchritt3Punkt, NavSchritt3Symbol, "3",
                    new FrameworkElement[] { FahrerBox, FahrzeugBox }),
                new Schritt("Prüfen & speichern", "Bitte alles noch einmal prüfen. Mit „Ändern“ springen Sie zum passenden Schritt.",
                    new[] { ZusammenfassungKarte }, NavSchritt4, NavSchritt4Balken, NavSchritt4Punkt, NavSchritt4Symbol, "4",
                    Array.Empty<FrameworkElement>())
            };
        }

        private int SchrittVon(FrameworkElement feld) => Array.FindIndex(_schritte, s => s.Felder.Contains(feld));

        // Zeigt nur die Karten des Schritts und passt Kopf und Knöpfe an.
        private void SchrittZeigen(int index)
        {
            _aktuell = index;
            Schritt aktuell = _schritte[index];
            foreach (Schritt schritt in _schritte)
                foreach (Border karte in schritt.Karten)
                    karte.Visibility = ReferenceEquals(schritt, aktuell) ? Visibility.Visible : Visibility.Collapsed;

            SchrittZaehler.Text = $"Schritt {index + 1} von {_schritte.Length}";
            SchrittTitel.Text = aktuell.Titel;
            SchrittHinweis.Text = aktuell.Hinweis;

            bool letzter = index == _schritte.Length - 1;
            ZurueckButton.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;
            WeiterButton.Visibility = letzter ? Visibility.Collapsed : Visibility.Visible;
            SpeichernButton.Visibility = letzter ? Visibility.Visible : Visibility.Collapsed;
            if (letzter)
                ZusammenfassungAufbauen();

            SchritteAktualisieren();
            DispatcherQueue?.TryEnqueue(() => SchrittKopf.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 }));
        }

        private List<(FrameworkElement Feld, string Meldung)> FehlerIm(int index, List<(FrameworkElement Feld, string Meldung)> alle) =>
            alle.Where(f => _schritte[index].Felder.Contains(f.Feld)).ToList();

        // Nach vorne nur, wenn alle Schritte davor vollständig sind. Sonst zum ersten Schritt mit Fehlern.
        private void VorwaertsGehen(int ziel)
        {
            var alle = FelderPruefen();
            for (int i = _aktuell; i < ziel; i++)
            {
                var fehler = FehlerIm(i, alle);
                if (fehler.Count > 0)
                {
                    AlleMarkierungenEntfernen();
                    FehlerMarkieren(fehler);
                    return;
                }
            }
            AlleMarkierungenEntfernen();
            FehlerText.Visibility = Visibility.Collapsed;
            SchrittZeigen(ziel);
        }

        private void Weiter_Click(object sender, RoutedEventArgs e)
        {
            if (_aktuell < _schritte.Length - 1)
                VorwaertsGehen(_aktuell + 1);
        }

        private void Zurueck_Click(object sender, RoutedEventArgs e)
        {
            if (_aktuell > 0)
                SchrittZeigen(_aktuell - 1);
        }

        private void Schritt_Click(object sender, RoutedEventArgs e)
        {
            int ziel = Array.FindIndex(_schritte, s => ReferenceEquals(s.Knopf, sender));
            if (ziel < 0 || ziel == _aktuell)
                return;
            if (ziel < _aktuell)
                SchrittZeigen(ziel);
            else
                VorwaertsGehen(ziel);
        }

        // Punkte links: Akzent = aktueller Schritt, rotes „!“ = rot markiert, grünes ✓ = vollständig, sonst Nummer.
        private void SchritteAktualisieren()
        {
            if (_schritte.Length == 0)
                return;
            var alle = FelderPruefen();
            int vollstaendig = 0;
            for (int i = 0; i < _schritte.Length; i++)
            {
                Schritt s = _schritte[i];
                bool istLetzter = i == _schritte.Length - 1;
                bool fertig = istLetzter ? vollstaendig == _schritte.Length - 1 : FehlerIm(i, alle).Count == 0;
                bool aktiv = i == _aktuell;

                s.Balken.Opacity = aktiv ? 1 : 0;
                s.Knopf.Background = aktiv ? Ressource("InputBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

                if (s.Felder.Any(_markiert.Contains))
                    PunktSetzen(s, "!", Ressource("ErrorBrush"), Ressource("ErrorBrush"), Ressource("SurfaceBrush"));
                else if (fertig && !aktiv)
                    PunktSetzen(s, "✓", _gruen, _gruen, new SolidColorBrush(Microsoft.UI.Colors.White));
                else if (aktiv)
                    PunktSetzen(s, s.Nummer, Ressource("AccentBrush"), Ressource("AccentBrush"), Ressource("AccentTextBrush"));
                else
                    PunktSetzen(s, s.Nummer, new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        Ressource("BorderBrush"), Ressource("MutedTextBrush"));

                if (fertig && !istLetzter)
                    vollstaendig++;
            }
            FortschrittText.Text = vollstaendig == _schritte.Length - 1
                ? "Alle Angaben vollständig."
                : $"{vollstaendig} von {_schritte.Length - 1} Schritten vollständig";
        }

        private static void PunktSetzen(Schritt s, string symbol, Brush fuellung, Brush rand, Brush schrift)
        {
            s.Symbol.Text = symbol;
            s.Symbol.Foreground = schrift;
            s.Punkt.Fill = fuellung;
            s.Punkt.Stroke = rand;
        }

        private static Brush Ressource(string schluessel) => (Brush)Application.Current.Resources[schluessel];

        // ---- Schritt 4: Zusammenfassung ----

        private void ZusammenfassungAufbauen()
        {
            ZusammenfassungListe.Children.Clear();

            string datum = DatumBox.Date is DateTimeOffset d ? d.ToString("ddd dd.MM.yyyy", Deutsch) : "—";
            Bereich("Tourdaten", 0, new List<(string, string)>
            {
                ("Tournummer", Wert(TournummerBox.Text)),
                ("Bereich", (AbteilungBox.SelectedItem as ComboBoxItem)?.Content as string ?? "—"),
                ("Zeitraum", $"{datum} · {Wert(StartBox.Text)}–{Wert(EndeBox.Text)} Uhr"),
                ("Strecke", $"{Wert(StartortBox.Text)} → {Wert(ZielortBox.Text)}")
            });

            var stopps = new List<(string, string)>
            {
                ("Anzahl", _stopps.Count == 1 ? "1 Stopp" : $"{_stopps.Count} Stopps")
            };
            for (int i = 0; i < _stopps.Count; i++)
            {
                StoppPosten p = _stopps[i];
                stopps.Add(($"{i + 1}. {p.Art}", $"{p.Bezeichnung} · {p.Von:HH\\:mm}–{p.Bis:HH\\:mm}" +
                    (p.Auftragsnummer is null ? "" : $" · Sendung {p.Auftragsnummer}")));
            }
            Bereich("Sendungen & Stopps", 1, stopps);

            object? fahrer = (FahrerBox.SelectedItem as ComboBoxItem)?.Tag;
            object? fahrzeug = (FahrzeugBox.SelectedItem as ComboBoxItem)?.Tag;
            Bereich("Fahrer & Fahrzeug", 2, new List<(string, string)>
            {
                ("Fahrer", fahrer is null ? "später zuweisen" : (FahrerBox.SelectedItem as ComboBoxItem)?.Content as string ?? "—"),
                ("Fahrzeug", fahrzeug is null ? "später zuweisen" : (FahrzeugBox.SelectedItem as ComboBoxItem)?.Content as string ?? "—"),
                ("Status danach", fahrer is not null && fahrzeug is not null ? TourWerte.StatusZugewiesen : TourWerte.StatusGeplant)
            });
        }

        // Ein Bereich der Zusammenfassung: Überschrift mit „Ändern“ und darunter Beschriftung / Wert.
        private void Bereich(string titel, int schritt, List<(string Label, string Wert)> zeilen)
        {
            var inhalt = new StackPanel { Spacing = 6 };

            var kopf = new Grid();
            kopf.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            kopf.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            kopf.Children.Add(new TextBlock
            {
                Text = titel,
                Style = (Style)Application.Current.Resources["NormalText"],
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            var aendern = new Button
            {
                Content = "Ändern",
                Style = (Style)Application.Current.Resources["DashboardButton"],
                MinHeight = 32,
                Padding = new Thickness(12, 2, 12, 2)
            };
            aendern.Click += (_, _) => SchrittZeigen(schritt);
            Grid.SetColumn(aendern, 1);
            kopf.Children.Add(aendern);
            inhalt.Children.Add(kopf);

            foreach (var (label, wert) in zeilen)
            {
                var zeile = new Grid { ColumnSpacing = 16 };
                zeile.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                zeile.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                zeile.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["HinweisText"] });
                var wertText = new TextBlock
                {
                    Text = wert,
                    Style = (Style)Application.Current.Resources["NormalText"],
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(wertText, 1);
                zeile.Children.Add(wertText);
                inhalt.Children.Add(zeile);
            }

            ZusammenfassungListe.Children.Add(new Border
            {
                Background = Ressource("InputBrush"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, 14),
                Child = inhalt
            });
        }

        private static string Wert(string? text) => string.IsNullOrWhiteSpace(text) ? "—" : text.Trim();

        private DateOnly TourTag() =>
            DatumBox.Date is DateTimeOffset datum
                ? DateOnly.FromDateTime(datum.Date)
                : throw new ArgumentException("Bitte ein Datum für die Tour wählen.");

        private static TimeOnly Uhrzeit(string? text, string feld) =>
            UhrzeitVersuchen(text, out TimeOnly zeit)
                ? zeit
                : throw new ArgumentException($"Bitte bei „{feld}“ eine Uhrzeit wie 08:30 eingeben.");

        // Erlaubt „8:00“, „08:00“ und „0800“.
        private static bool UhrzeitVersuchen(string? text, out TimeOnly zeit)
        {
            string wert = (text ?? "").Trim();
            if (wert.Length == 4 && wert.All(char.IsDigit))
                wert = wert[..2] + ":" + wert[2..];
            return TimeOnly.TryParseExact(wert, new[] { "H:mm", "HH:mm" }, Deutsch, DateTimeStyles.None, out zeit);
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
