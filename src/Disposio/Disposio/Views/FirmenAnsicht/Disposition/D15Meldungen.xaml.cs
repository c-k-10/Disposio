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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Disposio.Views
{
    // D15 „Meldungen bearbeiten“. Oben der Eingang (Tank- und Schadensmeldungen der Fahrer-App), unten die gewählte
    // Meldung zur Prüfung. Speichern ändert Meldung, Fahrzeugstatus und Aktionsprotokoll in einem Befehl
    // (MeldungRepository.SpeichernAsync). Fehlende oder falsche Angaben werden rot umrandet wie in D03.
    public sealed partial class D15Meldungen : UserControl
    {
        private const string AlleStatus = "Alle";
        private const string NurOffene = "Nur offene";
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        private readonly FirmenAnsichtKontext _kontext;
        private readonly MeldungRepository _repository = new();
        private IReadOnlyList<Meldung> _meldungen = Array.Empty<Meldung>();
        private IReadOnlyList<Meldung> _gefiltert = Array.Empty<Meldung>();
        private IReadOnlyList<MeldungBearbeiter> _bearbeiter = Array.Empty<MeldungBearbeiter>();
        private Meldung? _aktuell;
        private string? _originalAdresse;
        private CancellationTokenSource? _laden;
        private bool _speichert;
        private bool _fuellt;

        // Aus D01 („Schaden gemeldet“): diese Schadensmeldung nach dem Laden gleich öffnen.
        private string? _startSchluessel;

        // Rot markierte Felder.
        private readonly HashSet<Control> _markiert = new();

        public D15Meldungen(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            if (kontext.Parameter is long schadenId)
                _startSchluessel = $"{MeldungWerte.ArtSchaden}:{schadenId}";

            StatusFilter.Items.Add(AlleStatus);
            StatusFilter.Items.Add(NurOffene);
            foreach (string status in MeldungWerte.Status)
                StatusFilter.Items.Add(status);
            StatusFilter.SelectedItem = AlleStatus;
            StatusFilter.SelectionChanged += (_, _) => FilterAnzeigen();
            DatumAuswahl.Date = DateTimeOffset.Now.Date;

            MarkierungZuruecksetzenBeiEingabe();

            Loaded += async (_, _) => await LadenAsync();
            Unloaded += (_, _) => _laden?.Cancel();
        }

        private DateOnly GewaehlterTag => DatumAuswahl.Date is DateTimeOffset datum
            ? DateOnly.FromDateTime(datum.Date)
            : DateOnly.FromDateTime(DateTime.Now);

        // ---- Laden und Filtern ----

        private async Task LadenAsync()
        {
            if (_kontext.IstVorschau)
            {
                StatusText.Text = "In der Vorschau ohne Anmeldung werden keine Firmendaten geladen.";
                LeerText.Text = "Keine Meldungen in der Vorschau.";
                LeerText.Visibility = Visibility.Visible;
                return;
            }

            _laden?.Cancel();
            var abbruch = new CancellationTokenSource();
            _laden = abbruch;
            LadeAnzeige.IsActive = true;
            LadeAnzeige.Visibility = Visibility.Visible;
            StatusText.Text = "Meldungen werden geladen …";
            try
            {
                MeldungenDaten daten = await _repository.LadenAsync(GewaehlterTag, abbruch.Token);
                if (abbruch.IsCancellationRequested)
                    return;
                _meldungen = daten.Meldungen;
                _bearbeiter = daten.Bearbeiter;
                StatusText.Text = "";
                FilterAnzeigen();
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
                StatusText.Text = "Die Meldungen konnten nicht geladen werden. Bitte prüfen Sie die Verbindung.";
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

        private void FilterAnwenden_Click(object sender, RoutedEventArgs e) => _ = LadenAsync();

        private void SuchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                FilterAnzeigen();
            }
        }

        // Suche (Kennzeichen, Fahrer, Meldung) und Status filtern die geladene Liste ohne neue Abfrage.
        private void FilterAnzeigen()
        {
            string suche = SuchBox.Text.Trim();
            string status = StatusFilter.SelectedItem as string ?? AlleStatus;
            _gefiltert = _meldungen
                .Where(m => suche.Length == 0 ||
                            m.Kennzeichen.Contains(suche, StringComparison.OrdinalIgnoreCase) ||
                            (m.Fahrer ?? "").Contains(suche, StringComparison.OrdinalIgnoreCase) ||
                            m.Titel.Contains(suche, StringComparison.OrdinalIgnoreCase))
                .Where(m => status == AlleStatus ||
                            (status == NurOffene ? m.IstOffen : string.Equals(m.Status, status, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            int offen = _gefiltert.Count(m => m.IstOffen);
            StatusText.Text = _gefiltert.Count == 0 ? "" : $"{_gefiltert.Count} Meldungen · {offen} offen";
            LeerText.Text = _meldungen.Count == 0
                ? "Keine Meldungen bis zu diesem Datum. Neue Tank- und Schadensmeldungen kommen aus der Fahrer-App."
                : "Keine Meldung passt zu Suche und Status.";
            LeerText.Visibility = _gefiltert.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Vorgang-Auswahl im Formular = dieselben Meldungen wie im Eingang.
            _fuellt = true;
            VorgangBox.Items.Clear();
            foreach (Meldung m in _gefiltert)
                VorgangBox.Items.Add(new ComboBoxItem { Content = $"{MeldungZeile.Kurztitel(m)} · {m.Kennzeichen}", Tag = m.Schluessel });
            _fuellt = false;

            // Auswahl behalten; sonst Startwert aus D01, sonst erste offene, sonst erste Meldung.
            Meldung? auswahl =
                _gefiltert.FirstOrDefault(m => m.Schluessel == _startSchluessel) ??
                _gefiltert.FirstOrDefault(m => m.Schluessel == _aktuell?.Schluessel) ??
                _gefiltert.FirstOrDefault(m => m.IstOffen) ??
                _gefiltert.FirstOrDefault();
            _startSchluessel = null;
            Oeffnen(auswahl);
        }

        private void ListeAnzeigen()
        {
            MeldungListe.ItemsSource = _gefiltert
                .Select(m => new MeldungZeile(m, m.Schluessel == _aktuell?.Schluessel))
                .ToList();
        }

        // ---- Auswahl ----

        private void Zeile_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Border { Tag: string schluessel })
                Oeffnen(_gefiltert.FirstOrDefault(m => m.Schluessel == schluessel));
        }

        private void VorgangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_fuellt || (VorgangBox.SelectedItem as ComboBoxItem)?.Tag is not string schluessel)
                return;
            if (schluessel != _aktuell?.Schluessel)
                Oeffnen(_gefiltert.FirstOrDefault(m => m.Schluessel == schluessel));
        }

        // Zeigt die Meldung im Formular mit den gespeicherten Werten. Ungespeicherte Eingaben gehen dabei verloren.
        private void Oeffnen(Meldung? meldung)
        {
            _aktuell = meldung;
            ListeAnzeigen();
            Formular.Visibility = meldung is null ? Visibility.Collapsed : Visibility.Visible;
            if (meldung is null)
                return;

            _fuellt = true;
            AlleMarkierungenEntfernen();
            FehlerText.Visibility = Visibility.Collapsed;

            VorgangBox.SelectedItem = VorgangBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag as string == meldung.Schluessel);

            // Bearbeitungsstatus: feste Liste, ein unbekannter Wert aus der Fahrer-App bleibt zusätzlich wählbar.
            StatusBox.Items.Clear();
            foreach (string status in MeldungWerte.Status)
                StatusBox.Items.Add(status);
            if (meldung.Status.Length > 0 && !MeldungWerte.Status.Contains(meldung.Status))
                StatusBox.Items.Add(meldung.Status);
            StatusBox.SelectedItem = meldung.Status.Length > 0 ? meldung.Status : null;

            // Zuständig: gespeicherte Person, sonst das angemeldete Konto (falls es in der Liste steht).
            ZustaendigBox.Items.Clear();
            foreach (MeldungBearbeiter b in _bearbeiter)
                ZustaendigBox.Items.Add(new ComboBoxItem { Content = b.Anzeige, Tag = b.Id });
            long? zustaendig = meldung.BearbeiterId ?? BenutzerSitzung.AktuellerBenutzer?.Id;
            ZustaendigBox.SelectedItem = ZustaendigBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is long id && id == zustaendig);

            // Tankvorgang: Betrag und Liter. Schaden: nur Kosten.
            BetragBox.Text = meldung.Kosten.ToString("0.00", Deutsch);
            MengeBox.Text = meldung.Liter is decimal liter ? liter.ToString("0.##", Deutsch) : "";
            MengeBox.Visibility = meldung.IstTank ? Visibility.Visible : Visibility.Collapsed;
            MengeSpalte.Width = meldung.IstTank ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            BetragLabel.Text = meldung.IstTank ? "Betrag / Menge laut Beleg *" : "Kosten laut Kostenvoranschlag *";
            BetragBox.PlaceholderText = "EUR";

            NotizBox.Text = meldung.Notiz ?? "";

            FahrerhinweisText.Text = string.IsNullOrWhiteSpace(meldung.Hinweis) ? "—" : meldung.Hinweis;

            // Verfügbarkeit: Verfügbar / Wartung / Gesperrt. Ein anderer aktueller Status (z. B. „Im Einsatz“)
            // steht als „unverändert“ zusätzlich zur Wahl.
            VerfuegbarkeitBox.Items.Clear();
            foreach (var (wert, anzeige) in MeldungWerte.Verfuegbarkeit)
                VerfuegbarkeitBox.Items.Add(new ComboBoxItem { Content = anzeige, Tag = wert });
            if (meldung.FahrzeugStatus.Length > 0 && MeldungWerte.Verfuegbarkeit.All(v => v.Wert != meldung.FahrzeugStatus))
                VerfuegbarkeitBox.Items.Insert(0, new ComboBoxItem
                {
                    Content = $"Unverändert ({meldung.FahrzeugStatus})",
                    Tag = meldung.FahrzeugStatus
                });
            VerfuegbarkeitBox.SelectedItem = VerfuegbarkeitBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag as string == meldung.FahrzeugStatus);
            FahrzeugHinweis.Text = $"{meldung.Kennzeichen} ist derzeit „{(meldung.FahrzeugStatus.Length > 0 ? meldung.FahrzeugStatus : "ohne Status")}“. " +
                                   "„Gesperrt“ und „Wartung“ verhindern neue Zuweisungen.";

            AnhangAnzeigen(meldung);
            _fuellt = false;
        }

        // ---- Anhang (Foto / Tankbeleg) ----

        private void AnhangAnzeigen(Meldung m)
        {
            AnhangBild.Source = null;
            AnhangBild.Visibility = Visibility.Collapsed;
            _originalAdresse = m.AnhangAdresse;
            OriginalKnopf.Visibility = Uri.TryCreate(m.AnhangAdresse, UriKind.Absolute, out _) ? Visibility.Visible : Visibility.Collapsed;
            OriginalKnopf.Content = m.Anhaenge > 1 ? $"Original öffnen (neuestes von {m.Anhaenge})" : "Original öffnen";

            if (m.AnhangAdresse is { } adresse &&
                Uri.TryCreate(CloudinarySpeicher.VorschauAdresse(adresse, m.AnhangMimeTyp ?? ""), UriKind.Absolute, out Uri? bild))
            {
                AnhangText.Visibility = Visibility.Collapsed;
                BildLadeAnzeige.IsActive = true;
                BildLadeAnzeige.Visibility = Visibility.Visible;
                var quelle = new BitmapImage(bild);
                quelle.ImageOpened += (_, _) =>
                {
                    BildLadeAnzeige.IsActive = false;
                    BildLadeAnzeige.Visibility = Visibility.Collapsed;
                };
                AnhangBild.Source = quelle;
                AnhangBild.Visibility = Visibility.Visible;
            }
            else
            {
                BildLadeAnzeige.IsActive = false;
                BildLadeAnzeige.Visibility = Visibility.Collapsed;
                AnhangText.Visibility = Visibility.Visible;
                AnhangText.Text = m.IstTank
                    ? "Kein Tankbeleg vorhanden.\nTipp: Status „Beleg fehlt“ wählen und den Fahrer um ein Foto bitten."
                    : "Kein Foto zu dieser Schadensmeldung vorhanden.";
            }
        }

        private void AnhangBild_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            BildLadeAnzeige.IsActive = false;
            BildLadeAnzeige.Visibility = Visibility.Collapsed;
            AnhangBild.Visibility = Visibility.Collapsed;
            AnhangText.Visibility = Visibility.Visible;
            AnhangText.Text = "Für diese Datei gibt es keine Vorschau.\n„Original öffnen“ zeigt sie im Browser.";
        }

        private async void OriginalOeffnen_Click(object sender, RoutedEventArgs e)
        {
            if (_originalAdresse is not null && Uri.TryCreate(_originalAdresse, UriKind.Absolute, out Uri? adresse))
                await Launcher.LaunchUriAsync(adresse);
        }

        // ---- Speichern ----

        // Verwirft die Eingaben und zeigt wieder die gespeicherten Werte.
        private void Abbrechen_Click(object sender, RoutedEventArgs e) => Oeffnen(_aktuell);

        private async void Speichern_Click(object sender, RoutedEventArgs e)
        {
            if (_speichert || _kontext.IstVorschau || _aktuell is not { } meldung)
                return;

            FehlerText.Visibility = Visibility.Collapsed;
            AlleMarkierungenEntfernen();
            var fehler = FelderPruefen(meldung);
            if (fehler.Count > 0)
            {
                FehlerMarkieren(fehler);
                return;
            }

            ZahlVersuchen(BetragBox.Text, out decimal kosten);
            decimal? liter = meldung.IstTank && ZahlVersuchen(MengeBox.Text, out decimal menge) ? menge : null;
            var bearbeitung = new MeldungBearbeitung(
                meldung.Art,
                meldung.Id,
                (string)StatusBox.SelectedItem!,
                NotizBox.Text,
                (long)((ComboBoxItem)ZustaendigBox.SelectedItem!).Tag,
                kosten,
                liter,
                (string)((ComboBoxItem)VerfuegbarkeitBox.SelectedItem!).Tag);

            _speichert = true;
            SpeichernButton.IsEnabled = false;
            SpeichernButton.Content = "Wird gespeichert …";
            try
            {
                await _repository.SpeichernAsync(bearbeitung);
                await LadenAsync();
                StatusText.Text = $"Gespeichert: {MeldungZeile.Kurztitel(meldung)} · {meldung.Kennzeichen} " +
                                  $"({DateTime.Now.ToString("HH:mm", Deutsch)} Uhr)";
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (ArgumentException fehlerText)
            {
                FehlerAnzeigen(fehlerText.Message);
            }
            catch (Exception)
            {
                // Keine technischen Details anzeigen. Die Eingaben bleiben stehen.
                FehlerAnzeigen("Die Bearbeitung konnte nicht gespeichert werden. Bitte prüfen Sie die Verbindung und versuchen Sie es erneut.");
            }
            finally
            {
                _speichert = false;
                SpeichernButton.IsEnabled = true;
                SpeichernButton.Content = "Bearbeitung speichern";
            }
        }

        // Gleiche Regeln wie MeldungRepository.SpeichernAsync, aber jeder Fehler gehört zu einem Feld.
        private List<(Control Feld, string Meldung)> FelderPruefen(Meldung meldung)
        {
            var fehler = new List<(Control, string)>();
            if (StatusBox.SelectedItem is not string)
                fehler.Add((StatusBox, "Bitte einen Bearbeitungsstatus wählen."));
            if ((ZustaendigBox.SelectedItem as ComboBoxItem)?.Tag is not long)
                fehler.Add((ZustaendigBox, "Bitte wählen, wer zuständig ist."));

            if (string.IsNullOrWhiteSpace(BetragBox.Text))
                fehler.Add((BetragBox, "Betrag fehlt."));
            else if (!ZahlVersuchen(BetragBox.Text, out decimal kosten) || kosten is < 0 or > 1_000_000)
                fehler.Add((BetragBox, "Der Betrag muss eine Zahl zwischen 0 und 1.000.000 € sein."));

            if (meldung.IstTank)
            {
                if (string.IsNullOrWhiteSpace(MengeBox.Text))
                    fehler.Add((MengeBox, "Menge (Liter) fehlt."));
                else if (!ZahlVersuchen(MengeBox.Text, out decimal liter) || liter is <= 0 or > 5000)
                    fehler.Add((MengeBox, "Die Menge muss eine Zahl zwischen 0 und 5.000 Litern sein."));
            }

            if (string.IsNullOrWhiteSpace(NotizBox.Text))
                fehler.Add((NotizBox, "Bitte eine Rückfrage oder Bearbeitungsnotiz eingeben."));
            if ((VerfuegbarkeitBox.SelectedItem as ComboBoxItem)?.Tag is not string)
                fehler.Add((VerfuegbarkeitBox, "Bitte die Verfügbarkeit des Fahrzeugs nach der Prüfung wählen."));
            return fehler;
        }

        private void FehlerAnzeigen(string text)
        {
            FehlerText.Text = text;
            FehlerText.Visibility = Visibility.Visible;
        }

        // ---- Fehlerhafte Felder rot markieren (wie D03) ----

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
            FehlerAnzeigen(fehler.Count == 1
                ? fehler[0].Meldung
                : $"Bitte die {fehler.Count} rot markierten Felder prüfen:\n" +
                  string.Join("\n", fehler.Select(f => "• " + f.Meldung)));
            // Nur hinscrollen, nicht fokussieren: Ein fokussiertes Feld zeigt sonst den Fokusrahmen statt Rot.
            fehler[0].Feld.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3 });
        }

        private void MarkierungEntfernen(Control feld)
        {
            if (!_markiert.Remove(feld))
                return;
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

        private void MarkierungZuruecksetzenBeiEingabe()
        {
            foreach (TextBox box in new[] { BetragBox, MengeBox, NotizBox })
                box.TextChanged += (_, _) => MarkierungEntfernen(box);
            foreach (ComboBox box in new[] { StatusBox, ZustaendigBox, VerfuegbarkeitBox })
                box.SelectionChanged += (_, _) => MarkierungEntfernen(box);
        }

        // „312,40“ (deutsch) und „312.40“ werden beide verstanden. Mit Komma gilt ein Punkt als Tausendertrenner.
        private static bool ZahlVersuchen(string? text, out decimal wert)
        {
            string t = (text ?? "").Trim().Replace("€", "").Replace("EUR", "", StringComparison.OrdinalIgnoreCase)
                .Replace("l", "", StringComparison.OrdinalIgnoreCase).Trim();
            return t.Contains(',')
                ? decimal.TryParse(t, NumberStyles.Number, Deutsch, out wert)
                : decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out wert);
        }
    }

    // Eine Zeile im Eingang mit fertig formatierten Texten und Farben.
    public sealed class MeldungZeile
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public MeldungZeile(Meldung m, bool markiert)
        {
            Schluessel = m.Schluessel;
            IstMarkiert = markiert;
            Titel = Kurztitel(m);
            FahrzeugUndFahrer = string.IsNullOrWhiteSpace(m.Fahrer) ? m.Kennzeichen : $"{m.Kennzeichen} / {m.Fahrer}";
            // Meldungen haben nur ein Datum (keine Uhrzeit).
            Zeitpunkt = m.Datum.ToString("dd.MM.", Deutsch);
            Status = m.Status.Length > 0 ? m.Status : "—";

            Brush R(string schluessel) => (Brush)Application.Current.Resources[schluessel];
            bool achtung = m.Status.Contains("Beleg fehlt", StringComparison.OrdinalIgnoreCase) ||
                           m.Status.Contains("Rückfrage", StringComparison.OrdinalIgnoreCase);
            if (!m.IstOffen)
            {
                StatusRand = R("BorderBrush");
                StatusSchrift = R("MutedTextBrush");
            }
            else if (achtung)
            {
                StatusRand = R("ErrorBrush");
                StatusSchrift = R("ErrorBrush");
            }
            else
            {
                StatusRand = R("AccentBrush");
                StatusSchrift = R("TextBrush");
            }

            // Offener Tankvorgang ohne Beleg: kleiner Hinweis unter dem Status.
            bool belegFehlt = m.IstTank && m.IstOffen && m.Anhaenge == 0 &&
                              !m.Status.Contains("Beleg fehlt", StringComparison.OrdinalIgnoreCase);
            Zusatz = belegFehlt ? "Beleg fehlt" : "";
            ZusatzSichtbar = belegFehlt ? Visibility.Visible : Visibility.Collapsed;
        }

        // „Schaden · Seitentür“ bzw. „Tankvorgang · 182 l Diesel“
        public static string Kurztitel(Meldung m) => m.IstTank
            ? $"Tankvorgang · {(m.Liter is decimal l ? l.ToString("0.##", Deutsch) + " l " : "")}{m.Titel}".TrimEnd()
            : $"Schaden · {m.Titel}";

        public string Schluessel { get; }
        public bool IstMarkiert { get; }
        public string Titel { get; }
        public string FahrzeugUndFahrer { get; }
        public string Zeitpunkt { get; }
        public string Status { get; }
        public Brush StatusRand { get; }
        public Brush StatusSchrift { get; }
        public string Zusatz { get; }
        public Visibility ZusatzSichtbar { get; }
    }
}
