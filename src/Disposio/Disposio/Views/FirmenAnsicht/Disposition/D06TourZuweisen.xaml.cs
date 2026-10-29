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
using Windows.ApplicationModel.DataTransfer;

namespace Disposio.Views
{
    // D06 „Tour zuweisen“. Startwert ist die Tour-Id aus D04 („Zuweisen“). Angezeigt werden nur Fahrer und Fahrzeuge,
    // die im Zeitraum der Tour keine ANDERE offene Tour haben. Ob ein Fahrzeug zur Ladung passt (Kühlung, Nutzlast,
    // Lademeter, Paletten), prüft ZuweisungsRegeln – dieselben Regeln wie in D16.
    // Fahrer und Fahrzeug werden per Drag & Drop in die Felder „Zuweisung“ gezogen oder in der Liste angeklickt.
    // Gespeichert wird über TourRepository.ZuweisenAsync.
    public sealed partial class D06TourZuweisen : UserControl
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        private readonly FirmenAnsichtKontext _kontext;
        private readonly TourRepository _repository = new();
        private readonly long? _tourId;
        private TourZuweisungKopf? _tour;
        private CancellationTokenSource? _abbruch;
        private bool _speichert;
        // Was gerade gezogen wird: eine FahrerZeile oder eine FahrzeugAuswahlZeile.
        private object? _gezogen;

        public D06TourZuweisen(FirmenAnsichtKontext kontext)
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
                LadeFertig("Keine Tour gewählt. Bitte in „Touren“ bei einer offenen Tour auf „Zuweisen“ klicken.");
                return;
            }

            var abbruch = new CancellationTokenSource();
            _abbruch = abbruch;
            try
            {
                TourZuweisungDaten daten = await _repository.TourZuweisungLadenAsync(tourId, abbruch.Token);
                if (abbruch.IsCancellationRequested)
                    return;
                Anzeigen(daten);
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
                LadeFertig("Die Daten konnten nicht geladen werden. Bitte prüfen Sie die Verbindung und öffnen Sie die Ansicht erneut.");
            }
            finally
            {
                if (ReferenceEquals(_abbruch, abbruch))
                    _abbruch = null;
                abbruch.Dispose();
            }
        }

        private void Anzeigen(TourZuweisungDaten daten)
        {
            TourZuweisungKopf t = daten.Tour;
            _tour = t;

            TourTitel.Text = string.Join(" · ", new[] { t.Tournummer, t.Bereich, t.Status }.Where(teil => !string.IsNullOrWhiteSpace(teil)));
            string ende = t.Ende.Date == t.Start.Date ? t.Ende.ToString("HH:mm", Deutsch) : t.Ende.ToString("ddd, dd.MM. HH:mm", Deutsch);
            ZeitraumText.Text = $"{t.Start.ToString("ddd, dd.MM. HH:mm", Deutsch)} – {ende} · {t.Startort} → {t.Zielort}";
            LadungText.Text = t.Sendungen == 0
                ? "Noch keine Sendungen in dieser Tour – Ladung wird nicht geprüft."
                : string.Join(" · ", new[]
                {
                    t.Sendungen == 1 ? "1 Sendung" : $"{t.Sendungen} Sendungen",
                    $"{t.GewichtKg.ToString("N0", Deutsch)} kg",
                    $"{t.Lademeter.ToString("0.0", Deutsch)} Ldm",
                    t.Europaletten > 0 ? $"{t.Europaletten} Europaletten" : null,
                    t.Gekuehlt ? "gekühlt (Frische)" : null
                }.Where(teil => teil is not null));

            StoppListe.ItemsSource = daten.Stopps.Select(s => new TourStoppAnzeige(s)).ToList();
            StoppLeerText.Visibility = daten.Stopps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (!t.IstOffen)
            {
                // Abgeschlossene Touren nur anzeigen.
                LadeFertig($"Diese Tour ist „{t.Status}“ und kann nicht mehr zugewiesen werden.");
                FahrerListe.IsEnabled = FahrzeugListe.IsEnabled = false;
                AuswahlPruefen();
                return;
            }

            // Fahrer
            var fahrer = daten.Fahrer.Select(f => new FahrerZeile(f)).ToList();
            FahrerListe.ItemsSource = fahrer;
            FahrerTitel.Text = $"Freie Fahrer ({fahrer.Count})";
            FahrerLeerText.Visibility = fahrer.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Fahrzeuge: passende auswählbar, unpassende mit Grund als Hinweis.
            var passend = new List<FahrzeugAuswahlZeile>();
            var unpassend = new List<string>();
            foreach (FreiesFahrzeug f in daten.Fahrzeuge)
            {
                string? grund = ZuweisungsRegeln.FahrzeugPasstNicht(f, t.Gekuehlt, t.GewichtKg, t.Lademeter, t.Europaletten);
                if (grund is null)
                    passend.Add(new FahrzeugAuswahlZeile(f));
                else
                    unpassend.Add($"{f.Kennzeichen} ({grund})");
            }
            FahrzeugListe.ItemsSource = passend;
            FahrzeugTitel.Text = $"Freie Fahrzeuge ({passend.Count})";
            FahrzeugLeerText.Visibility = passend.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UnpassendText.Text = "Frei, aber nicht passend: " + string.Join(", ", unpassend);
            UnpassendText.Visibility = unpassend.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Bisherige Zuordnung (z. B. nur Fahrer schon gesetzt) gleich in die Felder übernehmen.
            FahrerListe.SelectedItem = fahrer.FirstOrDefault(z => z.Fahrer.FahrerId == t.FahrerId);
            FahrzeugListe.SelectedItem = passend.FirstOrDefault(z => z.Fahrzeug.Id == t.FahrzeugId);

            LadeFertig("");
            AuswahlPruefen();
        }

        private void LadeFertig(string text)
        {
            LadeAnzeige.IsActive = false;
            LadeAnzeige.Visibility = Visibility.Collapsed;
            LadeText.Text = text;
            LadeText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---- Auswahl ----

        private void Auswahl_SelectionChanged(object sender, SelectionChangedEventArgs e) => AuswahlPruefen();

        private void AuswahlPruefen()
        {
            if (AuswahlText is null || ZuweisenButton is null)
                return;
            var fahrerZeile = FahrerListe.SelectedItem as FahrerZeile;
            var fahrzeugZeile = FahrzeugListe.SelectedItem as FahrzeugAuswahlZeile;
            var fahrer = fahrerZeile?.Fahrer;
            var fahrzeug = fahrzeugZeile?.Fahrzeug;
            string? problem = fahrer is not null && fahrzeug is not null ? ZuweisungsRegeln.KlassePasstNicht(fahrer, fahrzeug) : null;

            AuswahlText.Text = (fahrer, fahrzeug) switch
            {
                (null, null) => "Bitte einen Fahrer und ein Fahrzeug in die Felder ziehen.",
                (null, _) => "Bitte noch einen Fahrer auswählen.",
                (_, null) => "Bitte noch ein Fahrzeug auswählen.",
                _ => problem ?? $"{fahrer!.Name} fährt Tour {_tour?.Tournummer} mit {fahrzeug!.Kennzeichen}."
            };
            ZuweisenButton.IsEnabled = _tour is { IstOffen: true } && fahrer is not null && fahrzeug is not null && problem is null && !_speichert;

            // Felder „Zuweisung“ zeigen die aktuelle Auswahl.
            SlotAnzeigen(FahrerSlotName, FahrerSlotDetails, FahrerEntfernen, fahrerZeile?.Name, fahrerZeile?.Details, "Fahrer hierher ziehen");
            SlotAnzeigen(FahrzeugSlotName, FahrzeugSlotDetails, FahrzeugEntfernen, fahrzeugZeile?.Titel, fahrzeugZeile?.Details, "Fahrzeug hierher ziehen");
        }

        private static void SlotAnzeigen(TextBlock name, TextBlock details, Button entfernen, string? titel, string? text, string leer)
        {
            name.Text = titel ?? leer;
            name.Opacity = titel is null ? 0.6 : 1.0;
            details.Text = text ?? "";
            details.Visibility = titel is null ? Visibility.Collapsed : Visibility.Visible;
            entfernen.Visibility = titel is null ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---- Drag & Drop ----

        // Beim Ziehen aus einer Liste merken, was gezogen wird. Der Text im Datenpaket ist nur für Windows.
        private void Liste_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            _gezogen = e.Items.FirstOrDefault();
            if (_gezogen is null || _tour is not { IstOffen: true })
            {
                e.Cancel = true;
                return;
            }
            e.Data.SetText(_gezogen is FahrerZeile f ? f.Name : (_gezogen as FahrzeugAuswahlZeile)?.Titel ?? "");
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        }

        private void Liste_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            _gezogen = null;
            Hervorheben(FahrerRahmen, false);
            Hervorheben(FahrzeugRahmen, false);
        }

        // Ein Feld nimmt nur die passende Art an: Fahrer ins Fahrer-Feld, Fahrzeug ins Fahrzeug-Feld.
        private void FahrerSlot_DragOver(object sender, DragEventArgs e) =>
            Annehmen(e, _gezogen is FahrerZeile, FahrerRahmen, "Als Fahrer setzen");

        private void FahrzeugSlot_DragOver(object sender, DragEventArgs e) =>
            Annehmen(e, _gezogen is FahrzeugAuswahlZeile, FahrzeugRahmen, "Als Fahrzeug setzen");

        private static void Annehmen(DragEventArgs e, bool passt, Rectangle rahmen, string text)
        {
            e.AcceptedOperation = passt ? DataPackageOperation.Copy : DataPackageOperation.None;
            if (e.DragUIOverride is { } anzeige)
            {
                anzeige.Caption = passt ? text : "Passt hier nicht";
                anzeige.IsCaptionVisible = true;
                anzeige.IsGlyphVisible = false;
            }
            Hervorheben(rahmen, passt);
        }

        private void Slot_DragLeave(object sender, DragEventArgs e)
        {
            Hervorheben(FahrerRahmen, false);
            Hervorheben(FahrzeugRahmen, false);
        }

        private void FahrerSlot_Drop(object sender, DragEventArgs e)
        {
            if (_gezogen is FahrerZeile fahrer)
                FahrerListe.SelectedItem = fahrer;   // löst AuswahlPruefen aus
            Hervorheben(FahrerRahmen, false);
        }

        private void FahrzeugSlot_Drop(object sender, DragEventArgs e)
        {
            if (_gezogen is FahrzeugAuswahlZeile fahrzeug)
                FahrzeugListe.SelectedItem = fahrzeug;
            Hervorheben(FahrzeugRahmen, false);
        }

        private void FahrerEntfernen_Click(object sender, RoutedEventArgs e) => FahrerListe.SelectedItem = null;

        private void FahrzeugEntfernen_Click(object sender, RoutedEventArgs e) => FahrzeugListe.SelectedItem = null;

        // Rahmen beim Darüberziehen in der Akzentfarbe (wie der pinke Knopf) und leicht gefüllt.
        private static void Hervorheben(Rectangle rahmen, bool an)
        {
            if (an)
            {
                Brush? akzent = Application.Current.Resources.TryGetValue("AccentBrush", out object? wert) ? wert as Brush : null;
                rahmen.Stroke = akzent ?? rahmen.Stroke;
                rahmen.StrokeThickness = 3;
                rahmen.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0x80, 0x80, 0x80));
            }
            else
            {
                if (Application.Current.Resources.TryGetValue("BorderBrush", out object? rand) && rand is Brush brush)
                    rahmen.Stroke = brush;
                rahmen.StrokeThickness = 2;
                rahmen.Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }

        // ---- Speichern ----

        private void Abbrechen_Click(object sender, RoutedEventArgs e) => _kontext.Oeffnen("D04");

        private async void Zuweisen_Click(object sender, RoutedEventArgs e)
        {
            if (_speichert || _tour is not { IstOffen: true } t
                || FahrerListe.SelectedItem is not FahrerZeile fahrer
                || FahrzeugListe.SelectedItem is not FahrzeugAuswahlZeile fahrzeug)
                return;

            FehlerText.Visibility = Visibility.Collapsed;
            _speichert = true;
            ZuweisenButton.IsEnabled = false;
            ZuweisenButton.Content = "Wird gespeichert …";
            try
            {
                await _repository.ZuweisenAsync(t.Id, fahrer.Fahrer.FahrerId, fahrzeug.Fahrzeug.Id);
                // Zurück zur Tourenübersicht, auf den Tag der Tour, mit einer Erfolgsmeldung.
                _kontext.Oeffnen("D04", new TourGespeichert(DateOnly.FromDateTime(t.Start),
                    $"Tour {t.Tournummer}: {fahrer.Name} mit {fahrzeug.Fahrzeug.Kennzeichen} zugewiesen."));
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (ArgumentException fehler)
            {
                FehlerAnzeigen(fehler.Message);
            }
            catch (Exception)
            {
                FehlerAnzeigen("Die Zuweisung konnte nicht gespeichert werden. Bitte prüfen Sie die Verbindung und versuchen Sie es erneut.");
            }
            finally
            {
                _speichert = false;
                ZuweisenButton.Content = "Fahrer & Fahrzeug zuweisen";
                AuswahlPruefen();
            }
        }

        private void FehlerAnzeigen(string text)
        {
            FehlerText.Text = text;
            FehlerText.Visibility = Visibility.Visible;
        }
    }

    // Ein Stopp in der Tourkarte, fertig formatiert.
    public sealed class TourStoppAnzeige
    {
        public TourStoppAnzeige(TourStoppKurz stopp)
        {
            Nummer = stopp.Reihenfolge.ToString(CultureInfo.InvariantCulture);
            Art = stopp.Art;
            Bezeichnung = stopp.Bezeichnung;
            Zeitfenster = stopp.Zeitfenster;
        }

        public string Nummer { get; }
        public string Art { get; }
        public string Bezeichnung { get; }
        public string Zeitfenster { get; }
    }
}
