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

        // Die vier Schritte des Assistenten: Karten im Formular, Knopf links und die Felder, die dazugehören.
        private sealed record Schritt(string Titel, string Hinweis, Border[] Karten, Button Knopf, Rectangle Balken,
                                      Ellipse Punkt, TextBlock Symbol, string Nummer, Control[] Felder);

        private readonly SolidColorBrush _gruen = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x2E, 0xA6, 0x6A));
        private Schritt[] _schritte = Array.Empty<Schritt>();
        private int _aktuell;

        // Adressen des gewählten Kunden für „Frühere ▾“. Die automatisch eingetragene Firmenadresse wird gemerkt,
        // damit ein Kundenwechsel sie ersetzen darf – eine selbst getippte Adresse aber nie überschrieben wird.
        private KundenAdressen? _adressen;
        private string? _autoAbholadresse;
        private CancellationTokenSource? _adressenLaden;
        private bool _gesperrt;

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
            SchritteAnlegen();
            MarkierungZuruecksetzenBeiEingabe();
            SchrittZeigen(0);

            Loaded += async (_, _) => await LadenAsync();
            Unloaded += (_, _) =>
            {
                _abbruch?.Cancel();
                _adressenLaden?.Cancel();
            };
            AbholadresseBox.TextChanged += (_, _) => AdressHinweiseAktualisieren();
            LieferadresseBox.TextChanged += (_, _) => AdressHinweiseAktualisieren();
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
                        _gesperrt = true;
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
                SchritteAktualisieren();
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
            _ = AdressenLadenAsync((KundeBox.SelectedItem as ComboBoxItem)?.Tag as long?);
        }

        // ---- Adressen des Kunden (Schritt 2) ----

        // Lädt Firmenadresse und frühere Adressen des Kunden. Die Abholadresse wird mit der Firmenadresse
        // vorbelegt, wenn sie leer ist oder noch die automatisch eingetragene eines anderen Kunden enthält.
        private async Task AdressenLadenAsync(long? kundeId)
        {
            _adressenLaden?.Cancel();
            _adressen = null;
            AdressKnoepfeAktualisieren();

            if (kundeId is not long id || _kontext.IstVorschau)
            {
                // Neuer Kunde oder keine Auswahl: eine automatisch eingetragene Adresse wieder entfernen.
                if (_autoAbholadresse is not null && AbholadresseBox.Text == _autoAbholadresse)
                    AbholadresseBox.Text = "";
                _autoAbholadresse = null;
                AdressHinweiseAktualisieren();
                return;
            }

            var abbruch = new CancellationTokenSource();
            _adressenLaden = abbruch;
            try
            {
                KundenAdressen adressen = await _repository.AdressenLadenAsync(id, abbruch.Token);
                if (abbruch.IsCancellationRequested)
                    return;
                _adressen = adressen;
                AdressKnoepfeAktualisieren();

                if (!_gesperrt && adressen.Firmenadresse is { } firma &&
                    (string.IsNullOrWhiteSpace(AbholadresseBox.Text) || AbholadresseBox.Text == _autoAbholadresse))
                {
                    _autoAbholadresse = firma;
                    AbholadresseBox.Text = firma;
                }
                AdressHinweiseAktualisieren();
            }
            catch (OperationCanceledException)
            {
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                // Die Vorschläge sind nur eine Hilfe. Ohne sie tippt man die Adresse wie bisher.
            }
            finally
            {
                if (ReferenceEquals(_adressenLaden, abbruch))
                    _adressenLaden = null;
                abbruch.Dispose();
            }
        }

        private void AdressKnoepfeAktualisieren()
        {
            bool vorhanden = !_gesperrt && _adressen is { } a && (a.Firmenadresse is not null || a.Fruehere.Count > 0);
            AbholAdressenButton.IsEnabled = LieferAdressenButton.IsEnabled = vorhanden;
        }

        private void AdressHinweiseAktualisieren()
        {
            if (AbholAdresseHinweis is null || LieferAdresseHinweis is null)
                return;
            bool auto = _autoAbholadresse is not null && AbholadresseBox.Text == _autoAbholadresse;
            AbholAdresseHinweis.Text = "Firmenadresse des Kunden eingetragen – bei Bedarf einfach ändern.";
            AbholAdresseHinweis.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;

            bool tipp = !_gesperrt && string.IsNullOrWhiteSpace(LieferadresseBox.Text) && _adressen is { } a &&
                        (a.Fruehere.Count > 0 || a.Firmenadresse is not null);
            LieferAdresseHinweis.Text = "Bekannte Adressen dieses Kunden finden Sie unter „Frühere ▾“.";
            LieferAdresseHinweis.Visibility = tipp ? Visibility.Visible : Visibility.Collapsed;
        }

        // Menü mit Firmenadresse und früheren Adressen. Ein Klick trägt die Adresse ins Feld ein (danach frei änderbar).
        private void Adressauswahl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button knopf || _adressen is not { } adressen)
                return;
            TextBox ziel = ReferenceEquals(knopf, AbholAdressenButton) ? AbholadresseBox : LieferadresseBox;

            var menue = new MenuFlyout();
            void Eintrag(string text, string adresse)
            {
                var item = new MenuFlyoutItem { Text = text };
                item.Click += (_, _) =>
                {
                    // Bewusst gewählt: gilt nicht mehr als automatisch eingetragen.
                    if (ReferenceEquals(ziel, AbholadresseBox))
                        _autoAbholadresse = null;
                    ziel.Text = adresse;
                    ziel.SelectionStart = ziel.Text.Length;
                };
                menue.Items.Add(item);
            }

            if (adressen.Firmenadresse is { } firma)
                Eintrag($"Firmenadresse: {firma}", firma);
            if (adressen.Fruehere.Count > 0)
            {
                if (menue.Items.Count > 0)
                    menue.Items.Add(new MenuFlyoutSeparator());
                foreach (FruehereAdresse f in adressen.Fruehere)
                    Eintrag($"{f.Adresse}   ·   zuletzt {f.Art}", f.Adresse);
            }
            menue.ShowAt(knopf);
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

            // Liegt das erste fehlerhafte Feld in einem anderen Schritt, dorthin wechseln.
            int schritt = SchrittVon(fehler[0].Feld);
            if (schritt >= 0 && schritt != _aktuell)
                SchrittZeigen(schritt);
            SchritteAktualisieren();
            FehlerAnzeigen(fehler.Count == 1
                ? fehler[0].Meldung
                : $"Bitte die {fehler.Count} rot markierten Felder prüfen:\n" +
                  string.Join("\n", fehler.Select(f => "• " + f.Meldung).Distinct()));

            // Nur hinscrollen, nicht fokussieren: Ein fokussiertes Feld zeigt sonst den Fokusrahmen statt Rot.
            // Erst nach dem Neuaufbau, falls gerade der Schritt gewechselt wurde.
            Control erstes = fehler[0].Feld;
            DispatcherQueue.TryEnqueue(() =>
                erstes.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3 }));
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
                box.TextChanged += (_, _) => { MarkierungEntfernen(box); SchritteAktualisieren(); };

            foreach (ComboBox box in new[] { PrioritaetBox, KundeBox, EquipmentBox, GutartBox, VerpackungBox })
                box.SelectionChanged += (_, _) => { MarkierungEntfernen(box); SchritteAktualisieren(); };

            AbholdatumBox.DateChanged += (_, _) =>
            {
                MarkierungEntfernen(AbholdatumBox);
                MarkierungEntfernen(LieferdatumBox);
                SchritteAktualisieren();
            };
            LieferdatumBox.DateChanged += (_, _) => { MarkierungEntfernen(LieferdatumBox); SchritteAktualisieren(); };
            // Mit der Gutart ändert sich, ob die Temperaturen Pflicht sind.
            GutartBox.SelectionChanged += (_, _) => { MarkierungEntfernen(TempMinBox); MarkierungEntfernen(TempMaxBox); };
        }

        // ---- Assistent: Schritte ----

        private void SchritteAnlegen()
        {
            _schritte = new[]
            {
                new Schritt("Auftrag & Kunde", "Auftragsnummer, Priorität und für welchen Kunden transportiert wird.",
                    new[] { AuftragKarte, KundeKarte }, NavSchritt1, NavSchritt1Balken, NavSchritt1Punkt, NavSchritt1Symbol, "1",
                    new Control[]
                    {
                        AuftragsnummerBox, KundenreferenzBox, PrioritaetBox, KundeBox, KundennummerBox, FirmennameBox,
                        KundeAnsprechpartnerBox, KundeAdresseBox, KundePlzBox, KundeOrtBox, KundeTelefonBox, KundeEmailBox
                    }),
                new Schritt("Orte & Zeiten", "Wo und wann abgeholt und zugestellt wird – und wer vor Ort Bescheid weiß.",
                    new[] { OrteKarte, VorOrtKarte }, NavSchritt2, NavSchritt2Balken, NavSchritt2Punkt, NavSchritt2Symbol, "2",
                    new Control[]
                    {
                        AbholadresseBox, LieferadresseBox, AbholdatumBox, LieferdatumBox, AbholVonBox, AbholBisBox,
                        LieferVonBox, LieferBisBox, AnsprechpartnerBox, EquipmentBox
                    }),
                new Schritt("Ladegut", "Was transportiert wird: Art, Menge, Maße und besondere Anforderungen.",
                    new[] { LadegutKarte }, NavSchritt3, NavSchritt3Balken, NavSchritt3Punkt, NavSchritt3Symbol, "3",
                    new Control[]
                    {
                        GutartBox, VerpackungBox, AnzahlBox, GewichtBox, LaengeBox, BreiteBox, HoeheBox,
                        WarenwertBox, TempMinBox, TempMaxBox
                    }),
                new Schritt("Prüfen & speichern", "Bitte alles noch einmal prüfen. Mit „Ändern“ springen Sie zum passenden Schritt.",
                    new[] { ZusammenfassungKarte }, NavSchritt4, NavSchritt4Balken, NavSchritt4Punkt, NavSchritt4Symbol, "4",
                    Array.Empty<Control>())
            };
        }

        private int SchrittVon(Control feld) => Array.FindIndex(_schritte, s => s.Felder.Contains(feld));

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

        // Fehler eines Schritts (nur dessen Felder). Leer = Schritt vollständig.
        private List<(Control Feld, string Meldung)> FehlerIm(int index, List<(Control Feld, string Meldung)> alle) =>
            alle.Where(f => _schritte[index].Felder.Contains(f.Feld)).ToList();

        // Wechselt zu einem späteren Schritt nur, wenn alle Schritte davor vollständig sind.
        // Sonst geht es zum ersten Schritt mit Fehlern, und dessen Felder werden rot markiert.
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

        // Punkte links: Akzent = aktueller Schritt, rotes „!“ = rot markiertes Feld, grünes ✓ = vollständig,
        // sonst die Nummer. „Prüfen“ wird grün, wenn alle drei Eingabeschritte vollständig sind.
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

            object? kundeTag = (KundeBox.SelectedItem as ComboBoxItem)?.Tag;
            bool neuerKunde = kundeTag as string == NeuerKundeTag;
            string kunde = neuerKunde
                ? $"Neuer Kunde: {Wert(FirmennameBox.Text)} ({Wert(KundennummerBox.Text)})"
                : (KundeBox.SelectedItem as ComboBoxItem)?.Content as string ?? "—";

            var auftrag = new List<(string, string)>
            {
                ("Auftragsnummer", Wert(AuftragsnummerBox.Text)),
                ("Kundenreferenz", Wert(KundenreferenzBox.Text)),
                ("Priorität", PrioritaetBox.SelectedItem as string ?? "—"),
                ("Kunde", kunde)
            };
            if (neuerKunde)
            {
                auftrag.Add(("Anschrift", $"{Wert(KundeAdresseBox.Text)}, {KundePlzBox.Text.Trim()} {KundeOrtBox.Text.Trim()}".Trim()));
                auftrag.Add(("Kontakt", $"{Wert(KundeAnsprechpartnerBox.Text)} · {Wert(KundeTelefonBox.Text)} · {Wert(KundeEmailBox.Text)}"));
            }
            Bereich("Auftrag & Kunde", 0, auftrag);

            Bereich("Orte & Zeiten", 1, new List<(string, string)>
            {
                ("Abholung", Wert(AbholadresseBox.Text)),
                ("", $"{DatumText(AbholdatumBox)} · {Wert(AbholVonBox.Text)}–{Wert(AbholBisBox.Text)} Uhr"),
                ("Zustellung", Wert(LieferadresseBox.Text)),
                ("", $"{DatumText(LieferdatumBox)} · {Wert(LieferVonBox.Text)}–{Wert(LieferBisBox.Text)} Uhr"),
                ("Vor Ort", Wert(AnsprechpartnerBox.Text)),
                ("Equipment", EquipmentBox.SelectedItem as string ?? "—")
            });

            string gutart = GutartBox.SelectedItem as string ?? "—";
            var ladegut = new List<(string, string)>
            {
                ("Gut", $"{gutart} · {VerpackungBox.SelectedItem as string ?? "—"}"),
                ("Menge", $"{Wert(AnzahlBox.Text)} × {Wert(LaengeBox.Text)} × {Wert(BreiteBox.Text)} × {Wert(HoeheBox.Text)} m (L × B × H)"),
                ("Gewicht", $"{Wert(GewichtBox.Text)} kg"),
                ("Lademeter", LademeterText.Text),
                ("Warenwert", $"{Wert(WarenwertBox.Text)} €")
            };
            if (gutart == SendungWerte.GutartFrische)
                ladegut.Add(("Temperatur", $"{Wert(TempMinBox.Text)} bis {Wert(TempMaxBox.Text)} °C"));
            var optionen = new (CheckBox Box, string Name)[]
            {
                (StapelbarBox, "Stapelbar"), (DrehbarBox, "Drehbar"), (GefahrgutBox, "Gefahrgut"),
                (VersicherungBox, "Versicherung"), (LadehilfsmittelBox, "Ladehilfsmitteltausch"),
                (ZollBox, "Zollrelevant"), (AvisierungBox, "Avisierung erforderlich")
            }.Where(o => o.Box.IsChecked == true).Select(o => o.Name).ToList();
            ladegut.Add(("Besonderheiten", optionen.Count == 0 ? "keine" : string.Join(", ", optionen)));
            Bereich("Ladegut", 2, ladegut);
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

        private static string DatumText(CalendarDatePicker feld) =>
            feld.Date is DateTimeOffset datum ? datum.ToString("ddd dd.MM.yyyy", Deutsch) : "—";

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
