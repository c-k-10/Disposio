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

            Loaded += async (_, _) => await LadenAsync();
            Unloaded += (_, _) => _abbruch?.Cancel();
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
                FehlerAnzeigen(fehler.Message);
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

        // ---- Eingaben umwandeln ----

        private static DateOnly Datum(CalendarDatePicker feld, string name) =>
            feld.Date is DateTimeOffset datum
                ? DateOnly.FromDateTime(datum.Date)
                : throw new ArgumentException($"Bitte ein {name} wählen.");

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
