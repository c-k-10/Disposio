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
    // D16 „Sendung zuweisen“. Startwert ist die Auftrags-Id aus D02. Angezeigt werden nur Fahrer und Fahrzeuge,
    // die im Zeitraum der Sendung (Abholfenster bis Lieferfenster) keine andere offene Tour haben.
    // Fahrzeuge, die nicht passen (Kühlung, Nutzlast, Lademeter), stehen mit Grund darunter (Regeln: ZuweisungsRegeln, wie in D06).
    // Gespeichert wird über TourRepository.AnlegenAsync: neue Tour mit zwei Stopps, Fahrer und Fahrzeug.
    // Fahrer und Fahrzeug werden per Drag & Drop in die Felder „Zuweisung“ gezogen. Das Feld zeigt die Auswahl der Liste:
    // Ablegen wählt den Eintrag in der Liste aus, Anklicken in der Liste füllt das Feld genauso.
    public sealed partial class D16SendungZuweisen : UserControl
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        private readonly FirmenAnsichtKontext _kontext;
        private readonly TourRepository _repository = new();
        private readonly long? _auftragId;
        private ZuweisungSendung? _sendung;
        private CancellationTokenSource? _abbruch;
        private bool _speichert;
        // Was gerade gezogen wird: eine FahrerZeile oder eine FahrzeugAuswahlZeile.
        private object? _gezogen;

        public D16SendungZuweisen(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            _auftragId = kontext.Parameter as long?;
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
            if (_auftragId is not long auftragId)
            {
                LadeFertig("Keine Sendung gewählt. Bitte in „Sendungen“ bei einer offenen Sendung auf „Zuweisen“ klicken.");
                return;
            }

            var abbruch = new CancellationTokenSource();
            _abbruch = abbruch;
            try
            {
                ZuweisungDaten daten = await _repository.ZuweisungLadenAsync(auftragId, abbruch.Token);
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

        private void Anzeigen(ZuweisungDaten daten)
        {
            ZuweisungSendung s = daten.Sendung;
            _sendung = s;

            SendungTitel.Text = string.IsNullOrWhiteSpace(s.Kunde)
                ? $"{s.Referenz} · Priorität {s.Prioritaet}"
                : $"{s.Referenz} · {s.Kunde} · Priorität {s.Prioritaet}";
            AbholungZeit.Text = $"{s.Abholdatum.ToString("ddd, dd.MM.", Deutsch)} · {Zeit(s.AbholfensterVon)}–{Zeit(s.AbholfensterBis)}";
            AbholungAdresse.Text = s.Abholadresse;
            ZustellungZeit.Text = $"{s.Lieferdatum.ToString("ddd, dd.MM.", Deutsch)} · {Zeit(s.LieferfensterVon)}–{Zeit(s.LieferfensterBis)}";
            ZustellungAdresse.Text = s.Lieferadresse;

            var ladegut = new List<string>
            {
                s.Gutart,
                $"{s.Anzahl} × {s.Verpackungstyp}",
                $"{s.GewichtKg.ToString("N0", Deutsch)} kg",
                $"{s.Lademeter.ToString("0.0", Deutsch)} Ldm"
            };
            if (s.TemperaturMinC is decimal min && s.TemperaturMaxC is decimal max)
                ladegut.Add($"{min.ToString("0.#", Deutsch)}–{max.ToString("0.#", Deutsch)} °C");
            if (s.Gefahrgut)
                ladegut.Add("Gefahrgut");
            LadegutText.Text = string.Join(" · ", ladegut);

            if (s.Tournummer is { } tour)
            {
                // Schon verplant: nur anzeigen.
                LadeFertig($"Diese Sendung ist bereits in Tour {tour} verplant.");
                FahrerListe.IsEnabled = FahrzeugListe.IsEnabled = TournummerBox.IsEnabled = AbteilungBox.IsEnabled = false;
                return;
            }

            // Fahrer
            FahrerListe.ItemsSource = daten.Fahrer.Select(f => new FahrerZeile(f)).ToList();
            FahrerTitel.Text = $"Freie Fahrer ({daten.Fahrer.Count})";
            FahrerLeerText.Visibility = daten.Fahrer.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Fahrzeuge: passende auswählbar, unpassende mit Grund als Hinweis.
            var passend = new List<FahrzeugAuswahlZeile>();
            var unpassend = new List<string>();
            foreach (FreiesFahrzeug f in daten.Fahrzeuge)
            {
                string? grund = ZuweisungsRegeln.FahrzeugPasstNicht(f, s.Gutart == SendungWerte.GutartFrische, s.GewichtKg,
                    s.Lademeter, s.Verpackungstyp == SendungWerte.Europalette ? s.Anzahl : 0);
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

            // Tour: Nummer vorschlagen, Bereich wie die Gutart (z. B. „Frische“), sonst die erste Abteilung.
            TournummerBox.Text = daten.TournummerVorschlag;
            foreach (AbteilungAuswahl abteilung in daten.Abteilungen)
                AbteilungBox.Items.Add(new ComboBoxItem { Content = abteilung.Bezeichnung, Tag = abteilung.Id });
            AbteilungBox.SelectedItem = AbteilungBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Content as string, s.Gutart, StringComparison.OrdinalIgnoreCase))
                ?? AbteilungBox.Items.OfType<ComboBoxItem>().FirstOrDefault();

            TourHinweis.Text = $"Es wird eine Tour vom {s.Von.ToString("dd.MM. HH:mm", Deutsch)} bis {s.Bis.ToString("dd.MM. HH:mm", Deutsch)} " +
                               $"mit zwei Stopps angelegt: Abholung in {Adressen.Ort(s.Abholadresse)} und Zustellung in {Adressen.Ort(s.Lieferadresse)}. " +
                               "Mit Fahrer und Fahrzeug hat sie den Status „Zugewiesen“.";
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
            var fahrer = (FahrerListe.SelectedItem as FahrerZeile)?.Fahrer;
            var fahrzeug = (FahrzeugListe.SelectedItem as FahrzeugAuswahlZeile)?.Fahrzeug;
            string? problem = fahrer is not null && fahrzeug is not null ? ZuweisungsRegeln.KlassePasstNicht(fahrer, fahrzeug) : null;

            AuswahlText.Text = (fahrer, fahrzeug) switch
            {
                (null, null) => "Bitte einen Fahrer und ein Fahrzeug auswählen.",
                (null, _) => "Bitte noch einen Fahrer auswählen.",
                (_, null) => "Bitte noch ein Fahrzeug auswählen.",
                _ => problem ?? $"{fahrer!.Name} fährt mit {fahrzeug!.Kennzeichen}."
            };
            ZuweisenButton.IsEnabled = _sendung is { Tournummer: null } && fahrer is not null && fahrzeug is not null && problem is null && !_speichert;

            // Felder „Zuweisung“ zeigen die aktuelle Auswahl.
            var fahrerZeile = FahrerListe.SelectedItem as FahrerZeile;
            var fahrzeugZeile = FahrzeugListe.SelectedItem as FahrzeugAuswahlZeile;
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
            if (_gezogen is null || _sendung is not { Tournummer: null })
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

        private void Abbrechen_Click(object sender, RoutedEventArgs e) => _kontext.Oeffnen("D02");

        private async void Zuweisen_Click(object sender, RoutedEventArgs e)
        {
            if (_speichert || _sendung is not { } s
                || FahrerListe.SelectedItem is not FahrerZeile fahrer
                || FahrzeugListe.SelectedItem is not FahrzeugAuswahlZeile fahrzeug)
                return;

            FehlerText.Visibility = Visibility.Collapsed;
            if ((AbteilungBox.SelectedItem as ComboBoxItem)?.Tag is not long abteilungId)
            {
                FehlerAnzeigen("Bitte einen Bereich (Abteilung) wählen.");
                return;
            }

            // Zwei Stopps wie in D05: Abholung und Zustellung mit den Zeitfenstern der Sendung.
            var stopps = new List<TourStoppEntwurf>
            {
                new(TourWerte.ArtAbholung, Adressen.Ort(s.Abholadresse), s.Abholadresse,
                    s.Abholdatum.ToDateTime(s.AbholfensterVon), s.Abholdatum.ToDateTime(s.AbholfensterBis), s.Id, TourWerte.ArtAbholung),
                new(TourWerte.ArtZustellung, Adressen.Ort(s.Lieferadresse), s.Lieferadresse,
                    s.Lieferdatum.ToDateTime(s.LieferfensterVon), s.Lieferdatum.ToDateTime(s.LieferfensterBis), s.Id, TourWerte.ArtZustellung)
            };
            // Tourende muss nach dem Start liegen. Bei einem Lieferfenster ohne Dauer eine Minute dazugeben.
            DateTime ende = s.Bis > s.Von ? s.Bis : s.Von.AddMinutes(1);
            var entwurf = new TourEntwurf(TournummerBox.Text, abteilungId, s.Von, ende,
                Adressen.Ort(s.Abholadresse), Adressen.Ort(s.Lieferadresse),
                fahrer.Fahrer.FahrerId, fahrzeug.Fahrzeug.Id, stopps);

            _speichert = true;
            ZuweisenButton.IsEnabled = false;
            ZuweisenButton.Content = "Wird gespeichert …";
            try
            {
                await _repository.AnlegenAsync(entwurf);
                _kontext.Oeffnen("D04", new TourGespeichert(s.Abholdatum,
                    $"Tour {entwurf.Tournummer} mit {fahrer.Name} und {fahrzeug.Fahrzeug.Kennzeichen} wurde angelegt."));
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
                FehlerAnzeigen("Die Tour konnte nicht gespeichert werden. Bitte prüfen Sie die Verbindung und versuchen Sie es erneut.");
            }
            finally
            {
                _speichert = false;
                ZuweisenButton.Content = "Tour anlegen & zuweisen";
                AuswahlPruefen();
            }
        }

        private void FehlerAnzeigen(string text)
        {
            FehlerText.Text = text;
            FehlerText.Visibility = Visibility.Visible;
        }

        private static string Zeit(TimeOnly zeit) => zeit.ToString("HH:mm", Deutsch);
    }

    // Listenzeilen. Die XAML-Vorlagen binden mit {x:Bind Name} daran.
    public sealed class FahrerZeile
    {
        public FahrerZeile(FreierFahrer fahrer)
        {
            Fahrer = fahrer;
            Name = fahrer.Name;
            Details = string.Join(" · ", new[] { $"Klasse {fahrer.Fuehrerscheinklasse}", fahrer.Abteilung, fahrer.Verfuegbarkeit }
                .Where(teil => !string.IsNullOrWhiteSpace(teil)));
        }

        public FreierFahrer Fahrer { get; }
        public string Name { get; }
        public string Details { get; }
    }

    public sealed class FahrzeugAuswahlZeile
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public FahrzeugAuswahlZeile(FreiesFahrzeug fahrzeug)
        {
            Fahrzeug = fahrzeug;
            Titel = $"{fahrzeug.Kennzeichen} · {fahrzeug.Fahrzeugtyp} · {fahrzeug.Aufbau}";
            Details = $"{fahrzeug.NutzbareLademeter.ToString("0.#", Deutsch)} Ldm · {fahrzeug.NutzlastKg.ToString("N0", Deutsch)} kg · " +
                      $"{fahrzeug.EpStellplaetze} Plätze · {fahrzeug.Status}" +
                      (fahrzeug.Abteilung is null ? "" : $" · {fahrzeug.Abteilung}");
        }

        public FreiesFahrzeug Fahrzeug { get; }
        public string Titel { get; }
        public string Details { get; }
    }
}
