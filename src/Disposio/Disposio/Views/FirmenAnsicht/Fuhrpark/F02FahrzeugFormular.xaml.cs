using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Disposio.Views
{
    // F02 legt ein Fahrzeug an. Das Formular prüft, ob alles ausgefüllt und als Zahl lesbar ist;
    // Wertebereiche und eindeutige Kennzeichen prüft danach das FahrzeugRepository bzw. die Datenbank.
    public sealed partial class F02FahrzeugFormular : UserControl
    {
        private readonly FirmenAnsichtKontext _kontext;
        private readonly IFahrzeugRepository _repository = new FahrzeugRepository();
        private bool _speichert;

        public F02FahrzeugFormular(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;

            Fuellen(KlasseBox, FahrzeugWerte.Fahrzeugtypen);
            Fuellen(StatusBox, FahrzeugWerte.StatusBeimAnlegen);
            Fuellen(AufbauBox, FahrzeugWerte.Aufbauten);
            StatusBox.SelectedIndex = 0;   // „Verfügbar“
            AufbauBox.SelectedIndex = 0;   // „Trockengut“, deshalb ohne Solltemperatur
            AbteilungBox.Items.Add(new ComboBoxItem { Content = "Keine Abteilung", Tag = null });
            AbteilungBox.SelectedIndex = 0;

            Loaded += async (_, _) => await AbteilungenLadenAsync();
        }

        private static void Fuellen(ComboBox box, IReadOnlyList<string> werte)
        {
            foreach (string wert in werte)
                box.Items.Add(new ComboBoxItem { Content = wert, Tag = wert });
        }

        private async System.Threading.Tasks.Task AbteilungenLadenAsync()
        {
            if (_kontext.IstVorschau)
                return;

            try
            {
                foreach (AbteilungAuswahl abteilung in await _repository.AbteilungenLadenAsync())
                    AbteilungBox.Items.Add(new ComboBoxItem { Content = abteilung.Bezeichnung, Tag = abteilung.Id });
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                // Ohne Abteilungsliste kann das Fahrzeug trotzdem ohne Abteilung angelegt werden.
                AbteilungBox.IsEnabled = false;
            }
        }

        // Nur ein gekühlter Aufbau („Frische“) hat eine Solltemperatur.
        private void Aufbau_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool gekuehlt = Auswahl(AufbauBox) == FahrzeugWerte.Kuehlaufbau;
            TemperaturBox.IsEnabled = gekuehlt;
            TemperaturLabel.Text = gekuehlt ? "Solltemperatur (°C) *" : "Solltemperatur (°C)";
            if (!gekuehlt)
                TemperaturBox.Text = "";
        }

        private void Abbrechen_Click(object sender, RoutedEventArgs e)
        {
            if (!_speichert)
                _kontext.Oeffnen("F01");
        }

        private async void Speichern_Click(object sender, RoutedEventArgs e)
        {
            // Ein Doppelklick während des Speicherns darf kein zweites Fahrzeug anlegen.
            if (_speichert)
                return;

            FehlerAnzeige.Visibility = Visibility.Collapsed;
            if (!EingabenLesen(out FahrzeugEntwurf? entwurf))
                return;

            _speichert = true;
            SpeichernButton.IsEnabled = AbbrechenButton.IsEnabled = false;
            SpeichernButton.Content = "Speichert …";
            try
            {
                Fahrzeug gespeichert = await _repository.AnlegenAsync(entwurf);
                // Erst nach der Bestätigung aus Neon zurück zur Liste, mit Erfolgsmeldung.
                _kontext.Oeffnen("F01", $"Fahrzeug {gespeichert.Kennzeichen} wurde angelegt.");
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (FahrzeugKennzeichenVergebenException exception)
            {
                Fehler(exception.Message, KennzeichenBox);
            }
            catch (ArgumentException exception)
            {
                // Diese Meldungen stammen aus unserer eigenen Prüfung im FahrzeugRepository.
                Fehler(exception.Message, null);
            }
            catch (InvalidOperationException) when (_kontext.IstVorschau)
            {
                Fehler("Die Eingaben sind gültig. In der Vorschau ohne Anmeldung wird aber nichts gespeichert.", null);
            }
            catch (Exception)
            {
                Fehler("Speichern konnte nicht bestätigt werden. Ihre Eingaben bleiben erhalten. Bitte prüfen Sie die " +
                    "Verbindung und laden Sie die Fahrzeugliste neu, bevor Sie es erneut versuchen.", null);
            }
            finally
            {
                _speichert = false;
                SpeichernButton.IsEnabled = AbbrechenButton.IsEnabled = true;
                SpeichernButton.Content = "Fahrzeug speichern";
            }
        }

        /// <summary>
        /// Liest alle Felder. Beim ersten Problem erscheint eine Meldung und das Feld bekommt den Fokus.
        /// </summary>
        private bool EingabenLesen([NotNullWhen(true)] out FahrzeugEntwurf? entwurf)
        {
            entwurf = null;
            string kennzeichen = FahrzeugWerte.KennzeichenVereinheitlichen(KennzeichenBox.Text);
            if (kennzeichen.Length == 0)
                return Fehler("Bitte ein Kennzeichen eingeben.", KennzeichenBox);
            if (Auswahl(KlasseBox) is not string klasse)
                return Fehler("Bitte eine Fahrzeugklasse auswählen.", KlasseBox);
            if (string.IsNullOrWhiteSpace(HerstellerBox.Text))
                return Fehler("Bitte den Hersteller eingeben.", HerstellerBox);
            if (string.IsNullOrWhiteSpace(ModellBox.Text))
                return Fehler("Bitte das Modell eingeben.", ModellBox);
            if (!GanzzahlLesen(BaujahrBox.Text, out int baujahr))
                return Fehler("Bitte das Baujahr als Zahl eingeben, zum Beispiel 2021.", BaujahrBox);
            if (!GanzzahlLesen(KilometerBox.Text, out int kilometerstand))
                return Fehler("Bitte den Kilometerstand als ganze Zahl eingeben, zum Beispiel 184.250.", KilometerBox);
            if (TuevPicker.Date is not DateTimeOffset tuev)
                return Fehler("Bitte das Datum der nächsten TÜV-Prüfung wählen.", TuevPicker);
            if (Auswahl(StatusBox) is not string status)
                return Fehler("Bitte einen Status auswählen.", StatusBox);
            if (Auswahl(AufbauBox) is not string aufbau)
                return Fehler("Bitte einen Aufbau auswählen.", AufbauBox);

            decimal? solltemperatur = null;
            if (aufbau == FahrzeugWerte.Kuehlaufbau)
            {
                if (!KommazahlLesen(TemperaturBox.Text, negativErlaubt: true, out decimal temperatur))
                    return Fehler("Bitte die Solltemperatur in °C eingeben, zum Beispiel 4 oder -18.", TemperaturBox);
                solltemperatur = temperatur;
            }

            if (!GanzzahlLesen(NutzlastBox.Text, out int nutzlast))
                return Fehler("Bitte die Nutzlast in kg als ganze Zahl eingeben, zum Beispiel 25000.", NutzlastBox);
            if (!GanzzahlLesen(StellplaetzeBox.Text, out int stellplaetze))
                return Fehler("Bitte die Euro-Palettenstellplätze als ganze Zahl eingeben.", StellplaetzeBox);
            if (!KommazahlLesen(LaengeBox.Text, negativErlaubt: false, out decimal laenge))
                return Fehler("Bitte die nutzbare Ladelänge in Metern eingeben, zum Beispiel 13,6.", LaengeBox);
            if (!KommazahlLesen(LademeterBox.Text, negativErlaubt: false, out decimal lademeter))
                return Fehler("Bitte die nutzbaren Lademeter eingeben, zum Beispiel 13,6.", LademeterBox);
            if (!KommazahlLesen(BreiteBox.Text, negativErlaubt: false, out decimal breite))
                return Fehler("Bitte die Innenbreite in Metern eingeben, zum Beispiel 2,45.", BreiteBox);
            if (!KommazahlLesen(HoeheBox.Text, negativErlaubt: false, out decimal hoehe))
                return Fehler("Bitte die Innenhöhe in Metern eingeben, zum Beispiel 2,6.", HoeheBox);

            long? abteilungId = (AbteilungBox.SelectedItem as ComboBoxItem)?.Tag is long id ? id : null;
            entwurf = new FahrzeugEntwurf(kennzeichen, klasse, HerstellerBox.Text, ModellBox.Text, baujahr,
                kilometerstand, status, DateOnly.FromDateTime(tuev.Date), aufbau, nutzlast, laenge, breite, hoehe,
                lademeter, stellplaetze, solltemperatur, abteilungId);
            return true;
        }

        private bool Fehler(string text, Control? feld)
        {
            FehlerAnzeige.Text = text;
            FehlerAnzeige.Visibility = Visibility.Visible;
            feld?.Focus(FocusState.Programmatic);
            return false;
        }

        private static string? Auswahl(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

        // Ganze Zahl: Tausenderpunkte und Leerzeichen sind erlaubt, zum Beispiel „184.250“.
        private static bool GanzzahlLesen(string text, out int wert)
        {
            string bereinigt = text.Replace(".", "").Replace(" ", "").Replace(" ", "").Trim();
            return int.TryParse(bereinigt, NumberStyles.None, CultureInfo.InvariantCulture, out wert);
        }

        // Kommazahl mit Komma oder Punkt („13,6“ oder „13.6“) und höchstens zwei Nachkommastellen.
        private static bool KommazahlLesen(string text, bool negativErlaubt, out decimal wert)
        {
            string bereinigt = text.Trim().Replace(',', '.');
            NumberStyles stil = NumberStyles.AllowDecimalPoint | (negativErlaubt ? NumberStyles.AllowLeadingSign : NumberStyles.None);
            return decimal.TryParse(bereinigt, stil, CultureInfo.InvariantCulture, out wert) && decimal.Round(wert, 2) == wert;
        }
    }
}
