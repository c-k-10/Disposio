using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Disposio.Views
{
    // D03 „Sendung erfassen / bearbeiten“. Ohne Startwert neu, mit Auftrags-Id (aus D02 „Details“) bearbeiten.
    // Gespeichert wird in einem Befehl (SendungRepository.SpeichernAsync). Danach öffnet sich die Sendungsliste (D02).
    public sealed partial class D03SendungFormular : UserControl
    {
        private const string NeuerKundeTag = "neu";
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        private readonly FirmenAnsichtKontext _kontext;
        private readonly SendungRepository _repository = new();
        private readonly long? _id;
        private CancellationTokenSource? _abbruch;
        private bool _speichert;

        // Felder, die beim letzten Speichern rot markiert wurden.
        private readonly HashSet<Control> _markiert = new();

        // Die fünf Abschnitte links: Karte im Formular, Knopf in der Liste und die Felder, die dazugehören.
        private sealed record Abschnitt(Border Karte, Button Knopf, Rectangle Balken, Ellipse Punkt, TextBlock Symbol,
                                        string Nummer, Control[] Felder);

        private readonly SolidColorBrush _gruen = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x2E, 0xA6, 0x6A));
        private Abschnitt[] _abschnitte = Array.Empty<Abschnitt>();
        private ScrollViewer? _scroll;

        public D03SendungFormular(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            _id = kontext.Parameter as long?;

            PrioritaetBox.ItemsSource = SendungWerte.Prioritaeten;
            PrioritaetBox.SelectedItem = SendungWerte.PrioritaetStandard;
            GutartBox.ItemsSource = SendungWerte.Gutarten;
            VerpackungBox.ItemsSource = SendungWerte.Verpackungstypen;
            EquipmentBox.ItemsSource = SendungWerte.Equipment;
            EquipmentBox.SelectedIndex = 0;

            var heute = DateTimeOffset.Now.Date;
            AbholdatumBox.Date = heute;
            LieferdatumBox.Date = heute;
            SpeichernButton.Content = _id is null ? "Sendung speichern" : "Änderungen speichern";

            // Lademeter sofort neu rechnen, wenn sich Anzahl oder Maße ändern.
            AnzahlBox.TextChanged += (_, _) => LademeterAnzeigen();
            LaengeBox.TextChanged += (_, _) => LademeterAnzeigen();
            BreiteBox.TextChanged += (_, _) => LademeterAnzeigen();
            LademeterAnzeigen();
            TemperaturHinweisAnzeigen();
            AbschnitteAnlegen();
            MarkierungZuruecksetzenBeiEingabe();
            AbschnitteAktualisieren();

            Loaded += async (_, _) =>
            {
                ScrollAnmelden();
                await LadenAsync();
            };
            Unloaded += (_, _) =>
            {
                _abbruch?.Cancel();
                ScrollAbmelden();
            };
        }

        // ---- Laden ----

        private async Task LadenAsync()
        {
            if (_kontext.IstVorschau)
            {
                LadeFertig("In der Vorschau ohne Anmeldung werden keine Firmendaten geladen und keine Sendungen gespeichert.");
                return;
            }

            var abbruch = new CancellationTokenSource();
            _abbruch = abbruch;
            try
            {
                SendungFormularDaten daten = await _repository.FormularLadenAsync(_id, abbruch.Token);
                if (abbruch.IsCancellationRequested)
                    return;

                // Wie in F02: ComboBoxItem mit Text als Content und der Id als Tag. Am Ende „+ Neuer Kunde“.
                foreach (KundeAuswahl kunde in daten.Kunden)
                    KundeBox.Items.Add(new ComboBoxItem { Content = kunde.Anzeige, Tag = kunde.Id });
                KundeBox.Items.Add(new ComboBoxItem { Content = "+ Neuer Kunde", Tag = NeuerKundeTag });
                KundennummerBox.Text = daten.KundennummerVorschlag;
                // Ohne Kunden ist „Neuer Kunde“ die einzige Möglichkeit.
                if (daten.Kunden.Count == 0)
                    KundeBox.SelectedIndex = 0;

                if (daten.Vorhanden is { } sendung)
                {
                    Ausfuellen(sendung);
                    if (daten.Tournummer is { } tour)
                    {
                        // Verplante Sendungen nur anzeigen: Ihre Stopps hängen schon an der Tour.
                        Sperren(FormularBereich);
                        LadeFertig($"Diese Sendung ist in Tour {tour} verplant und kann hier nicht mehr geändert werden.");
                        return;
                    }
                }
                else
                {
                    AuftragsnummerBox.Text = daten.AuftragsnummerVorschlag;
                }

                SpeichernButton.IsEnabled = true;
                AbschnitteAktualisieren();
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

        // Füllt das Formular mit einer gespeicherten Sendung (Bearbeiten).
        private void Ausfuellen(SendungEntwurf s)
        {
            AuftragsnummerBox.Text = s.Auftragsnummer;
            KundenreferenzBox.Text = s.Kundenreferenz ?? "";
            PrioritaetBox.SelectedItem = SendungWerte.Prioritaeten.Contains(s.Prioritaet) ? s.Prioritaet : null;
            KundeBox.SelectedItem = KundeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is long id && id == s.KundeId);

            AbholadresseBox.Text = s.Abholadresse;
            AbholdatumBox.Date = s.Abholdatum.ToDateTime(TimeOnly.MinValue);
            AbholVonBox.Text = s.AbholfensterVon.ToString("HH:mm", Deutsch);
            AbholBisBox.Text = s.AbholfensterBis.ToString("HH:mm", Deutsch);
            LieferadresseBox.Text = s.Lieferadresse;
            LieferdatumBox.Date = s.Lieferdatum.ToDateTime(TimeOnly.MinValue);
            LieferVonBox.Text = s.LieferfensterVon.ToString("HH:mm", Deutsch);
            LieferBisBox.Text = s.LieferfensterBis.ToString("HH:mm", Deutsch);

            AnsprechpartnerBox.Text = s.Ansprechpartner;
            EquipmentBox.SelectedItem = SendungWerte.Equipment.Contains(s.LadeEntladeequipment) ? s.LadeEntladeequipment : null;
            AvisierungBox.IsChecked = s.AvisierungErforderlich;

            GutartBox.SelectedItem = SendungWerte.Gutarten.Contains(s.Gutart) ? s.Gutart : null;
            VerpackungBox.SelectedItem = SendungWerte.Verpackungstypen.Contains(s.Verpackungstyp) ? s.Verpackungstyp : null;
            AnzahlBox.Text = s.Anzahl.ToString(Deutsch);
            GewichtBox.Text = Text(s.GewichtKg);
            LaengeBox.Text = Text(s.LaengeM);
            BreiteBox.Text = Text(s.BreiteM);
            HoeheBox.Text = Text(s.HoeheM);
            WarenwertBox.Text = Text(s.Warenwert);
            TempMinBox.Text = s.TemperaturMinC is decimal min ? Text(min) : "";
            TempMaxBox.Text = s.TemperaturMaxC is decimal max ? Text(max) : "";

            StapelbarBox.IsChecked = s.Stapelbar;
            DrehbarBox.IsChecked = s.Drehbar;
            GefahrgutBox.IsChecked = s.Gefahrgut;
            VersicherungBox.IsChecked = s.Versicherung;
            LadehilfsmittelBox.IsChecked = s.Ladehilfsmitteltausch;
            ZollBox.IsChecked = s.Zollrelevanz;
        }

        // Alle Eingabefelder unterhalb des Elements sperren (verplante Sendung).
        private static void Sperren(DependencyObject element)
        {
            if (element is Control control)
                control.IsEnabled = false;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                Sperren(VisualTreeHelper.GetChild(element, i));
        }

        // ---- Reaktionen auf Auswahl ----

        private void KundeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool neu = (KundeBox.SelectedItem as ComboBoxItem)?.Tag as string == NeuerKundeTag;
            NeuerKundeBereich.Visibility = neu ? Visibility.Visible : Visibility.Collapsed;
        }

        private void GutartBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => TemperaturHinweisAnzeigen();

        // Bei Europaletten die Standardmaße 1,20 × 0,80 m vorschlagen, wenn noch nichts eingetragen ist.
        private void VerpackungBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LaengeBox is null || BreiteBox is null)
                return;
            if (VerpackungBox.SelectedItem as string == SendungWerte.Europalette &&
                LaengeBox.Text.Length == 0 && BreiteBox.Text.Length == 0)
            {
                LaengeBox.Text = "1,20";
                BreiteBox.Text = "0,80";
            }
        }

        private void TemperaturHinweisAnzeigen()
        {
            if (TemperaturHinweis is null)
                return;
            bool frische = GutartBox.SelectedItem as string == SendungWerte.GutartFrische;
            TemperaturHinweis.Text = frische
                ? "Frische: Mindest- und Höchsttemperatur sind Pflicht."
                : "Temperaturen nur bei Frische nötig.";
        }

        // Lademeter = Anzahl × Länge × Breite / 2,4 m. Gleiche Rechnung wie in der Sendungsliste (D02).
        private void LademeterAnzeigen()
        {
            if (LademeterText is null)
                return;
            bool ok = int.TryParse(AnzahlBox.Text.Trim(), out int anzahl) & ZahlVersuchen(LaengeBox.Text, out decimal laenge)
                      & ZahlVersuchen(BreiteBox.Text, out decimal breite);
            LademeterText.Text = ok && anzahl > 0 && laenge > 0 && breite > 0
                ? $"≈ {Math.Round(anzahl * laenge * breite / 2.4m, 1).ToString("0.0", Deutsch)} Lademeter"
                : "Lademeter: Anzahl, Länge und Breite eingeben";
        }

        // ---- Speichern ----

        private void Abbrechen_Click(object sender, RoutedEventArgs e) => _kontext.Oeffnen("D02");

        private async void Speichern_Click(object sender, RoutedEventArgs e)
        {
            if (_speichert || _kontext.IstVorschau)
                return;

            FehlerText.Visibility = Visibility.Collapsed;
            AlleMarkierungenEntfernen();

            // Erst alle Felder prüfen und die fehlerhaften rot umranden, dann erst speichern.
            List<(Control Feld, string Meldung)> fehlerListe = FelderPruefen();
            if (fehlerListe.Count > 0)
            {
                FehlerMarkieren(fehlerListe);
                return;
            }

            SendungEntwurf entwurf;
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
            object? beschriftung = SpeichernButton.Content;
            SpeichernButton.Content = "Wird gespeichert …";
            try
            {
                await _repository.SpeichernAsync(entwurf, _id);
                string meldung = _id is null
                    ? $"Sendung {entwurf.Auftragsnummer} wurde angelegt."
                    : $"Sendung {entwurf.Auftragsnummer} wurde gespeichert.";
                // Zur Sendungsliste, auf den Abholtag, mit einer Erfolgsmeldung.
                _kontext.Oeffnen("D02", new SendungGespeichert(entwurf.Abholdatum, meldung));
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (NummerVergebenException fehler)
            {
                // Die Datenbank meldet eine schon vergebene Nummer: das passende Feld markieren.
                Control feld = fehler.Message.Contains("Kundennummer") ? KundennummerBox : AuftragsnummerBox;
                FehlerMarkieren(new List<(Control, string)> { (feld, fehler.Message) });
            }
            catch (ArgumentException fehler)
            {
                FehlerAnzeigen(fehler.Message);
            }
            catch (Exception)
            {
                // Keine technischen Details anzeigen. Die Eingaben bleiben stehen.
                FehlerAnzeigen("Die Sendung konnte nicht gespeichert werden. Bitte prüfen Sie die Verbindung und versuchen Sie es erneut.");
            }
            finally
            {
                _speichert = false;
                SpeichernButton.IsEnabled = true;
                SpeichernButton.Content = beschriftung;
            }
        }

        // Liest das Formular. Die Prüfregeln stehen zusätzlich im Repository.
        private SendungEntwurf EntwurfLesen()
        {
            object? kundeTag = (KundeBox.SelectedItem as ComboBoxItem)?.Tag;
            if (kundeTag is null)
                throw new ArgumentException("Bitte einen Kunden wählen oder „+ Neuer Kunde“ anlegen.");
            NeuerKunde? neuerKunde = kundeTag as string == NeuerKundeTag
                ? new NeuerKunde(KundennummerBox.Text, FirmennameBox.Text, KundeAnsprechpartnerBox.Text, KundeAdresseBox.Text,
                    KundePlzBox.Text, KundeOrtBox.Text, KundeTelefonBox.Text, KundeEmailBox.Text)
                : null;

            string gutart = GutartBox.SelectedItem as string ?? throw new ArgumentException("Bitte eine Gutart wählen.");
            string verpackung = VerpackungBox.SelectedItem as string ?? throw new ArgumentException("Bitte einen Verpackungstyp wählen.");
            string equipment = EquipmentBox.SelectedItem as string ?? throw new ArgumentException("Bitte das Lade-/Entladeequipment wählen.");
            string prioritaet = PrioritaetBox.SelectedItem as string ?? throw new ArgumentException("Bitte eine Priorität wählen.");

            if (!int.TryParse(AnzahlBox.Text.Trim(), NumberStyles.Integer, Deutsch, out int anzahl))
                throw new ArgumentException("Bitte bei „Anzahl“ eine ganze Zahl eingeben.");

            return new SendungEntwurf(
                AuftragsnummerBox.Text,
                KundenreferenzBox.Text,
                kundeTag as long?,
                neuerKunde,
                AbholadresseBox.Text,
                Datum(AbholdatumBox, "Abholdatum"),
                Uhrzeit(AbholVonBox.Text, "Abholung von"),
                Uhrzeit(AbholBisBox.Text, "Abholung bis"),
                LieferadresseBox.Text,
                Datum(LieferdatumBox, "Lieferdatum"),
                Uhrzeit(LieferVonBox.Text, "Zustellung von"),
                Uhrzeit(LieferBisBox.Text, "Zustellung bis"),
                AnsprechpartnerBox.Text,
                AvisierungBox.IsChecked == true,
                gutart,
                verpackung,
                anzahl,
                Zahl(GewichtBox.Text, "Gewicht"),
                Zahl(LaengeBox.Text, "Länge"),
                Zahl(BreiteBox.Text, "Breite"),
                Zahl(HoeheBox.Text, "Höhe"),
                OptionaleZahl(TempMinBox.Text, "Temperatur min."),
                OptionaleZahl(TempMaxBox.Text, "Temperatur max."),
                Zahl(WarenwertBox.Text, "Warenwert"),
                StapelbarBox.IsChecked == true,
                DrehbarBox.IsChecked == true,
                GefahrgutBox.IsChecked == true,
                VersicherungBox.IsChecked == true,
                LadehilfsmittelBox.IsChecked == true,
                ZollBox.IsChecked == true,
                equipment,
                prioritaet,
                SendungWerte.StatusOffen);
        }

        private void FehlerAnzeigen(string text)
        {
            FehlerText.Text = text;
            FehlerText.Visibility = Visibility.Visible;
        }

        // ---- Fehlerhafte Felder rot markieren ----

        // Prüft alle Felder in der Reihenfolge des Formulars. Gleiche Regeln wie SendungRepository.Pruefen,
        // aber hier wird jeder Fehler seinem Feld zugeordnet, damit man sieht, wo etwas fehlt.
        private List<(Control Feld, string Meldung)> FelderPruefen()
        {
            var fehler = new List<(Control, string)>();
            void Fehler(Control feld, string meldung) => fehler.Add((feld, meldung));
            void Pflicht(TextBox feld, string name)
            {
                if (string.IsNullOrWhiteSpace(feld.Text))
                    Fehler(feld, $"{name} fehlt.");
            }
            void Auswahl(ComboBox feld, string meldung)
            {
                if (feld.SelectedItem is null)
                    Fehler(feld, meldung);
            }
            decimal? ZahlImBereich(TextBox feld, string name, decimal minimum, decimal maximum, bool pflicht = true)
            {
                if (string.IsNullOrWhiteSpace(feld.Text))
                {
                    if (pflicht)
                        Fehler(feld, $"{name} fehlt.");
                    return null;
                }
                if (!ZahlVersuchen(feld.Text, out decimal wert))
                {
                    Fehler(feld, $"Bei „{name}“ bitte eine Zahl eingeben.");
                    return null;
                }
                if (wert < minimum || wert > maximum)
                {
                    Fehler(feld, $"{name} muss zwischen {minimum.ToString(Deutsch)} und {maximum.ToString("N0", Deutsch)} liegen.");
                    return null;
                }
                return wert;
            }
            TimeOnly? Zeit(TextBox feld, string name)
            {
                if (UhrzeitVersuchen(feld.Text, out TimeOnly zeit))
                    return zeit;
                Fehler(feld, string.IsNullOrWhiteSpace(feld.Text)
                    ? $"{name} fehlt."
                    : $"Bitte bei „{name}“ eine Uhrzeit wie 08:30 eingeben.");
                return null;
            }
            void Fenster(TextBox vonFeld, TextBox bisFeld, string name)
            {
                TimeOnly? von = Zeit(vonFeld, $"{name} von");
                TimeOnly? bis = Zeit(bisFeld, $"{name} bis");
                if (von is { } v && bis is { } b && b < v)
                    Fehler(bisFeld, $"Beim Zeitfenster „{name}“ liegt „bis“ vor „von“.");
            }

            // Auftrag
            Pflicht(AuftragsnummerBox, "Auftragsnummer");
            Auswahl(PrioritaetBox, "Bitte eine Priorität wählen.");

            // Kunde
            object? kundeTag = (KundeBox.SelectedItem as ComboBoxItem)?.Tag;
            if (kundeTag is null)
                Fehler(KundeBox, "Bitte einen Kunden wählen oder „+ Neuer Kunde“ anlegen.");
            else if (kundeTag as string == NeuerKundeTag)
            {
                Pflicht(KundennummerBox, "Kundennummer");
                Pflicht(FirmennameBox, "Firmenname");
                Pflicht(KundeAnsprechpartnerBox, "Ansprechpartner des Kunden");
                Pflicht(KundeAdresseBox, "Straße und Hausnummer des Kunden");
                Pflicht(KundePlzBox, "PLZ");
                Pflicht(KundeOrtBox, "Ort");
                Pflicht(KundeTelefonBox, "Telefon");
                string email = KundeEmailBox.Text.Trim();
                if (email.Length == 0)
                    Fehler(KundeEmailBox, "E-Mail des Kunden fehlt.");
                else if (!MailAddress.TryCreate(email, out MailAddress? adresse) || adresse.Address != email)
                    Fehler(KundeEmailBox, "Bitte eine gültige E-Mail-Adresse für den Kunden eingeben.");
            }

            // Transportorte
            Pflicht(AbholadresseBox, "Abholadresse");
            Pflicht(LieferadresseBox, "Lieferadresse");
            if (AbholdatumBox.Date is null)
                Fehler(AbholdatumBox, "Bitte ein Abholdatum wählen.");
            if (LieferdatumBox.Date is null)
                Fehler(LieferdatumBox, "Bitte ein Lieferdatum wählen.");
            else if (AbholdatumBox.Date is { } abholung && LieferdatumBox.Date.Value.Date < abholung.Date)
                Fehler(LieferdatumBox, "Das Lieferdatum darf nicht vor dem Abholdatum liegen.");
            Fenster(AbholVonBox, AbholBisBox, "Abholung");
            Fenster(LieferVonBox, LieferBisBox, "Zustellung");

            // Vor Ort
            Pflicht(AnsprechpartnerBox, "Ansprechpartner vor Ort");
            Auswahl(EquipmentBox, "Bitte das Lade-/Entladeequipment wählen.");

            // Ladegut
            Auswahl(GutartBox, "Bitte eine Gutart wählen.");
            Auswahl(VerpackungBox, "Bitte einen Verpackungstyp wählen.");
            if (string.IsNullOrWhiteSpace(AnzahlBox.Text))
                Fehler(AnzahlBox, "Anzahl fehlt.");
            else if (!int.TryParse(AnzahlBox.Text.Trim(), NumberStyles.Integer, Deutsch, out int anzahl) || anzahl is < 1 or > 10_000)
                Fehler(AnzahlBox, "Die Anzahl muss eine ganze Zahl zwischen 1 und 10.000 sein.");
            ZahlImBereich(GewichtBox, "Gewicht (kg)", 0.01m, 60_000);
            ZahlImBereich(LaengeBox, "Länge (m)", 0.01m, 20);
            ZahlImBereich(BreiteBox, "Breite (m)", 0.01m, 3);
            ZahlImBereich(HoeheBox, "Höhe (m)", 0.01m, 4);
            ZahlImBereich(WarenwertBox, "Warenwert (€)", 0, 100_000_000);

            bool frische = GutartBox.SelectedItem as string == SendungWerte.GutartFrische;
            decimal? min = ZahlImBereich(TempMinBox, "Temperatur min. (°C)", -30, 30, pflicht: frische);
            decimal? max = ZahlImBereich(TempMaxBox, "Temperatur max. (°C)", -30, 30, pflicht: frische);
            if (frische && min is decimal tMin && max is decimal tMax && tMin > tMax)
            {
                Fehler(TempMinBox, "Die Mindesttemperatur darf nicht über der Höchsttemperatur liegen.");
                Fehler(TempMaxBox, "Die Höchsttemperatur darf nicht unter der Mindesttemperatur liegen.");
            }

            return fehler;
        }

        // Rahmen rot, Meldung als Tooltip am Feld, unten eine Liste aller Fehler. Zum ersten Feld wird gescrollt.
        private void FehlerMarkieren(List<(Control Feld, string Meldung)> fehler)
        {
            Brush rot = Application.Current.Resources.TryGetValue("ErrorBrush", out object? wert) && wert is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Red);
            foreach (var (feld, meldung) in fehler)
            {
                feld.BorderBrush = rot;
                feld.BorderThickness = new Thickness(2);
                ToolTipService.SetToolTip(feld, meldung);
                _markiert.Add(feld);
            }

            AbschnitteAktualisieren();
            FehlerAnzeigen(fehler.Count == 1
                ? fehler[0].Meldung
                : $"Bitte die {fehler.Count} rot markierten Felder prüfen:\n" +
                  string.Join("\n", fehler.Select(f => "• " + f.Meldung).Distinct()));

            // Nur hinscrollen, nicht fokussieren: Ein fokussiertes Feld zeigt sonst den Fokusrahmen statt Rot.
            fehler[0].Feld.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3 });
        }

        private void MarkierungEntfernen(Control feld)
        {
            if (!_markiert.Remove(feld))
                return;
            // Zurück auf die Werte aus dem Stil (FormTextBox / FormComboBox / FormDatePicker).
            feld.ClearValue(Control.BorderBrushProperty);
            feld.ClearValue(Control.BorderThicknessProperty);
            ToolTipService.SetToolTip(feld, null);
            if (_markiert.Count == 0)
                FehlerText.Visibility = Visibility.Collapsed;
        }

        private void AlleMarkierungenEntfernen()
        {
            foreach (Control feld in _markiert.ToList())
                MarkierungEntfernen(feld);
        }

        // Sobald man ein markiertes Feld ändert, verschwindet sein roter Rahmen.
        private void MarkierungZuruecksetzenBeiEingabe()
        {
            foreach (TextBox box in new[]
                     {
                         AuftragsnummerBox, KundennummerBox, FirmennameBox, KundeAnsprechpartnerBox, KundeAdresseBox,
                         KundePlzBox, KundeOrtBox, KundeTelefonBox, KundeEmailBox, AbholadresseBox, LieferadresseBox,
                         AbholVonBox, AbholBisBox, LieferVonBox, LieferBisBox, AnsprechpartnerBox, AnzahlBox, GewichtBox,
                         LaengeBox, BreiteBox, HoeheBox, WarenwertBox, TempMinBox, TempMaxBox
                     })
                box.TextChanged += (_, _) => { MarkierungEntfernen(box); AbschnitteAktualisieren(); };

            foreach (ComboBox box in new[] { PrioritaetBox, KundeBox, EquipmentBox, GutartBox, VerpackungBox })
                box.SelectionChanged += (_, _) => { MarkierungEntfernen(box); AbschnitteAktualisieren(); };

            AbholdatumBox.DateChanged += (_, _) =>
            {
                MarkierungEntfernen(AbholdatumBox);
                MarkierungEntfernen(LieferdatumBox);
                AbschnitteAktualisieren();
            };
            LieferdatumBox.DateChanged += (_, _) => { MarkierungEntfernen(LieferdatumBox); AbschnitteAktualisieren(); };
            // Mit der Gutart ändert sich, ob die Temperaturen Pflicht sind.
            GutartBox.SelectionChanged += (_, _) => { MarkierungEntfernen(TempMinBox); MarkierungEntfernen(TempMaxBox); };
        }

        // ---- Abschnitte links (Navigation und Fortschritt) ----

        private void AbschnitteAnlegen()
        {
            _abschnitte = new[]
            {
                new Abschnitt(AuftragKarte, NavAuftrag, NavAuftragBalken, NavAuftragPunkt, NavAuftragSymbol, "1",
                    new Control[] { AuftragsnummerBox, KundenreferenzBox, PrioritaetBox }),
                new Abschnitt(KundeKarte, NavKunde, NavKundeBalken, NavKundePunkt, NavKundeSymbol, "2",
                    new Control[]
                    {
                        KundeBox, KundennummerBox, FirmennameBox, KundeAnsprechpartnerBox, KundeAdresseBox,
                        KundePlzBox, KundeOrtBox, KundeTelefonBox, KundeEmailBox
                    }),
                new Abschnitt(OrteKarte, NavOrte, NavOrteBalken, NavOrtePunkt, NavOrteSymbol, "3",
                    new Control[]
                    {
                        AbholadresseBox, LieferadresseBox, AbholdatumBox, LieferdatumBox,
                        AbholVonBox, AbholBisBox, LieferVonBox, LieferBisBox
                    }),
                new Abschnitt(VorOrtKarte, NavVorOrt, NavVorOrtBalken, NavVorOrtPunkt, NavVorOrtSymbol, "4",
                    new Control[] { AnsprechpartnerBox, EquipmentBox }),
                new Abschnitt(LadegutKarte, NavLadegut, NavLadegutBalken, NavLadegutPunkt, NavLadegutSymbol, "5",
                    new Control[]
                    {
                        GutartBox, VerpackungBox, AnzahlBox, GewichtBox, LaengeBox, BreiteBox, HoeheBox,
                        WarenwertBox, TempMinBox, TempMaxBox
                    })
            };
            AktivenAbschnittSetzen(_abschnitte[0]);
        }

        // Rot „!“ = ein Feld ist nach dem Speichern rot markiert. Grün „✓“ = alle Pflichtangaben stimmen.
        // Sonst die Nummer des Abschnitts: Es fehlt noch etwas (wird erst beim Speichern rot).
        private void AbschnitteAktualisieren()
        {
            if (_abschnitte.Length == 0)
                return;
            var offeneFelder = FelderPruefen().Select(f => f.Feld).ToHashSet();
            int vollstaendig = 0;
            foreach (Abschnitt a in _abschnitte)
            {
                if (a.Felder.Any(_markiert.Contains))
                    PunktSetzen(a, "!", Ressource("ErrorBrush"), Ressource("ErrorBrush"), Ressource("SurfaceBrush"));
                else if (!a.Felder.Any(offeneFelder.Contains))
                {
                    PunktSetzen(a, "✓", _gruen, _gruen, new SolidColorBrush(Microsoft.UI.Colors.White));
                    vollstaendig++;
                }
                else
                    PunktSetzen(a, a.Nummer, new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        Ressource("BorderBrush"), Ressource("MutedTextBrush"));
            }
            FortschrittText.Text = vollstaendig == _abschnitte.Length
                ? "Alle Abschnitte vollständig."
                : $"{vollstaendig} von {_abschnitte.Length} Abschnitten vollständig";
        }

        private static void PunktSetzen(Abschnitt a, string symbol, Brush fuellung, Brush rand, Brush schrift)
        {
            a.Symbol.Text = symbol;
            a.Symbol.Foreground = schrift;
            a.Punkt.Fill = fuellung;
            a.Punkt.Stroke = rand;
        }

        private static Brush Ressource(string schluessel) => (Brush)Application.Current.Resources[schluessel];

        private void Abschnitt_Click(object sender, RoutedEventArgs e)
        {
            Abschnitt? a = _abschnitte.FirstOrDefault(x => ReferenceEquals(x.Knopf, sender));
            if (a is null)
                return;
            AktivenAbschnittSetzen(a);
            a.Karte.StartBringIntoView(new BringIntoViewOptions
            {
                VerticalAlignmentRatio = 0,
                VerticalOffset = -16,
                AnimationDesired = true
            });
        }

        private void AktivenAbschnittSetzen(Abschnitt aktiv)
        {
            foreach (Abschnitt a in _abschnitte)
            {
                bool istAktiv = ReferenceEquals(a, aktiv);
                a.Balken.Opacity = istAktiv ? 1 : 0;
                a.Knopf.Background = istAktiv ? Ressource("InputBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }

        // Die Seite scrollt im ScrollViewer der FirmenAnsicht. Damit die Abschnittsliste sichtbar bleibt,
        // wird sie beim Scrollen nach unten mitgeschoben (höchstens bis zum Ende des Formulars).
        private void ScrollAnmelden()
        {
            DependencyObject? element = this;
            while (element is not null and not ScrollViewer)
                element = VisualTreeHelper.GetParent(element);
            _scroll = element as ScrollViewer;
            if (_scroll is null)
                return;
            _scroll.ViewChanged += Scroll_ViewChanged;
            SizeChanged += Groesse_Changed;
            NavigationNachziehen();
        }

        private void ScrollAbmelden()
        {
            if (_scroll is not null)
                _scroll.ViewChanged -= Scroll_ViewChanged;
            SizeChanged -= Groesse_Changed;
            _scroll = null;
        }

        private void Scroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => NavigationNachziehen();

        private void Groesse_Changed(object sender, SizeChangedEventArgs e) => NavigationNachziehen();

        private void NavigationNachziehen()
        {
            if (_scroll is null || _abschnitte.Length == 0)
                return;
            try
            {
                // Oberkante des Formulars im sichtbaren Bereich (negativ, wenn schon darüber gescrollt).
                double oben = FormularRaster.TransformToVisual(_scroll).TransformPoint(new Point(0, 0)).Y;
                double spielraum = Math.Max(0, FormularRaster.ActualHeight - AbschnittNavigation.ActualHeight);
                NavVerschiebung.Y = Math.Clamp(16 - oben, 0, spielraum);

                // Aktiv ist der letzte Abschnitt, dessen Karte das obere Drittel erreicht hat.
                double grenze = _scroll.ViewportHeight * 0.3;
                Abschnitt aktiv = _abschnitte[0];
                foreach (Abschnitt a in _abschnitte)
                    if (a.Karte.TransformToVisual(_scroll).TransformPoint(new Point(0, 0)).Y <= grenze)
                        aktiv = a;
                // Ganz unten angekommen: Der letzte Abschnitt erreicht das obere Drittel evtl. nie.
                if (_scroll.ScrollableHeight > 0 && _scroll.VerticalOffset >= _scroll.ScrollableHeight - 1)
                    aktiv = _abschnitte[^1];
                AktivenAbschnittSetzen(aktiv);
            }
            catch (ArgumentException)
            {
                // Die Ansicht wird gerade aus dem Baum entfernt.
            }
        }

        // ---- Eingaben umwandeln ----

        private static DateOnly Datum(CalendarDatePicker feld, string name) =>
            feld.Date is DateTimeOffset datum
                ? DateOnly.FromDateTime(datum.Date)
                : throw new ArgumentException($"Bitte ein {name} wählen.");

        // Erlaubt „8:00“, „08:00“ und „0800“.
        private static TimeOnly Uhrzeit(string? text, string feld) =>
            UhrzeitVersuchen(text, out TimeOnly zeit)
                ? zeit
                : throw new ArgumentException($"Bitte bei „{feld}“ eine Uhrzeit wie 08:30 eingeben.");

        private static bool UhrzeitVersuchen(string? text, out TimeOnly zeit)
        {
            string wert = (text ?? "").Trim();
            if (wert.Length == 4 && wert.All(char.IsDigit))
                wert = wert[..2] + ":" + wert[2..];
            return TimeOnly.TryParseExact(wert, new[] { "H:mm", "HH:mm" }, Deutsch, DateTimeStyles.None, out zeit);
        }

        private static decimal Zahl(string? text, string feld) =>
            ZahlVersuchen(text, out decimal wert) ? wert : throw new ArgumentException($"Bitte bei „{feld}“ eine Zahl eingeben.");

        private static decimal? OptionaleZahl(string? text, string feld) =>
            string.IsNullOrWhiteSpace(text) ? null : Zahl(text, feld);

        // „1,20“ (deutsch) und „1.20“ werden beide verstanden. Mit Komma gilt ein Punkt als Tausendertrenner („1.200,5“).
        private static bool ZahlVersuchen(string? text, out decimal wert)
        {
            string t = (text ?? "").Trim();
            return t.Contains(',')
                ? decimal.TryParse(t, NumberStyles.Number, Deutsch, out wert)
                : decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out wert);
        }

        private static string Text(decimal wert) => wert.ToString("0.##", Deutsch);
    }
}
