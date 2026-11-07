using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.System;

namespace Disposio.Views
{
    // D11: Sendungen laden (Tag, Suche oder Tour), eine wählen, deren Dokumente und Abliefernachweise zeigen.
    // „Vorschau“ zeigt Bilder und die erste PDF-Seite (über Cloudinary), „Original öffnen“ den Browser.
    // „Dokument zuordnen“ lädt eine Datei zu Cloudinary hoch und verknüpft sie mit der Sendung.
    public sealed partial class D11Frachtdokumente : UserControl
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        private readonly FirmenAnsichtKontext _kontext;
        private readonly FrachtdokumentRepository _repository = new();
        private long? _tourId;
        private IReadOnlyList<DokumentSendung> _sendungen = Array.Empty<DokumentSendung>();
        private DokumentSendung? _aktuell;
        private string? _originalAdresse;
        private (string Name, byte[] Inhalt)? _datei;
        private bool _auswahlIgnorieren;
        private bool _arbeitet;

        public D11Frachtdokumente(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            if (kontext.Parameter is long tourId)
                _tourId = tourId;

            DatumAuswahl.Date = DateTimeOffset.Now.Date;
            _auswahlIgnorieren = true;
            StatusAuswahl.ItemsSource = DokumentStatus.Auswahl;
            StatusAuswahl.SelectedIndex = 0;
            _auswahlIgnorieren = false;
            ArtAuswahl.ItemsSource = DokumentStatus.Arten;
            ArtAuswahl.SelectedIndex = 0;

            bool speicher = CloudinarySpeicher.Verfuegbar;
            SpeicherHinweis.Visibility = speicher ? Visibility.Collapsed : Visibility.Visible;
            ZuordnenKnopf.IsEnabled = DateiKnopf.IsEnabled = speicher;

            Loaded += async (_, _) => await LadenAsync();
        }

        private DateOnly GewaehlterTag =>
            DatumAuswahl.Date is DateTimeOffset d ? DateOnly.FromDateTime(d.Date) : DateOnly.FromDateTime(DateTime.Today);

        // ── Sendungen ────────────────────────────────────────────────────────────────────

        private async Task LadenAsync(long? auswaehlen = null)
        {
            if (_kontext.IstVorschau)
            {
                StatusText.Text = "In der Vorschau ohne Anmeldung werden keine Dokumente geladen.";
                return;
            }
            LadeAnzeige.IsActive = true;
            LadeAnzeige.Visibility = Visibility.Visible;
            StatusText.Text = "Sendungen werden geladen …";
            try
            {
                _sendungen = await _repository.SendungenLadenAsync(GewaehlterTag, SuchBox.Text, _tourId);
                ListeAnzeigen(auswaehlen ?? _aktuell?.Id);
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                StatusText.Text = "Die Sendungen konnten nicht geladen werden. Bitte prüfen Sie die Verbindung und versuchen Sie es erneut.";
            }
            finally
            {
                LadeAnzeige.IsActive = false;
                LadeAnzeige.Visibility = Visibility.Collapsed;
            }
        }

        private void ListeAnzeigen(long? auswaehlen)
        {
            string status = StatusAuswahl.SelectedItem as string ?? DokumentStatus.Alle;
            var treffer = _sendungen.Where(s => status == DokumentStatus.Alle || s.Status == status).ToList();

            string quelle = _tourId is not null ? "der Tour"
                : SuchBox.Text.Trim().Length > 0 ? $"zu „{SuchBox.Text.Trim()}“ (alle Tage)"
                : $"am {GewaehlterTag.ToString("dd.MM.yyyy", Deutsch)}";
            StatusText.Text = treffer.Count == 1 ? $"1 Sendung {quelle}" : $"{treffer.Count} Sendungen {quelle}";
            LeerText.Text = $"Keine Sendung {quelle} passt zum Filter.";
            LeerText.Visibility = treffer.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var zeilen = treffer.Select(s => new SendungTrefferZeile(s)).ToList();
            _auswahlIgnorieren = true;
            SendungListe.ItemsSource = zeilen;
            SendungListe.Visibility = treffer.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            var ziel = zeilen.FirstOrDefault(z => z.Sendung.Id == auswaehlen) ?? zeilen.FirstOrDefault();
            SendungListe.SelectedItem = ziel;
            _auswahlIgnorieren = false;

            if (ziel is null)
            {
                _aktuell = null;
                DokumentBereich.Visibility = Visibility.Collapsed;
            }
            else
            {
                _ = SendungOeffnenAsync(ziel.Sendung);
            }
        }

        private async Task SendungOeffnenAsync(DokumentSendung s)
        {
            bool neu = _aktuell?.Id != s.Id;
            _aktuell = s;
            DokumentBereich.Visibility = Visibility.Visible;
            DokumenteTitel.Text = $"Dokumente zu {s.Referenz}";
            if (neu)
                ReferenzBox.Text = s.Referenz;
            SendungsvorschauZeigen(s);
            try
            {
                SendungDokumente daten = await _repository.DokumenteLadenAsync(s.Id);
                if (_aktuell?.Id != s.Id)
                    return;
                var eintraege = daten.Nachweise.Select(n => new DokumentEintragZeile(n))
                    .Concat(daten.Dokumente.Select(d => new DokumentEintragZeile(d)))
                    .OrderBy(e => e.Zeit)
                    .ToList();
                EintragListe.ItemsSource = eintraege;
                KeineDokumente.Visibility = eintraege.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                KeineDokumente.Text = "Die Dokumente konnten nicht geladen werden.";
                KeineDokumente.Visibility = Visibility.Visible;
            }
        }

        // ── Vorschau ─────────────────────────────────────────────────────────────────────

        // Ohne gewähltes Dokument: Eckdaten der Sendung (wie der Platzhalter im Wireframe).
        private void SendungsvorschauZeigen(DokumentSendung s)
        {
            VorschauTitel.Text = "Vorschau · Sendung";
            VorschauBild.Source = null;
            VorschauBild.Visibility = Visibility.Collapsed;
            BildLadeAnzeige.IsActive = false;
            BildLadeAnzeige.Visibility = Visibility.Collapsed;
            OriginalKnopf.Visibility = Visibility.Collapsed;
            _originalAdresse = null;
            VorschauText.Visibility = Visibility.Visible;
            VorschauText.Text = string.Join("\n", new[]
            {
                $"Kundenreferenz {s.Referenz}",
                $"{s.Anzahl} × {s.Verpackungstyp} · {s.GewichtKg.ToString("N0", Deutsch)} kg",
                $"Empfänger {s.Kunde}",
                $"Lieferung {s.Lieferdatum.ToString("dd.MM.yyyy", Deutsch)} · {s.Lieferadresse}",
                "",
                "„Vorschau“ bei einem Dokument zeigt es hier an."
            });
        }

        private void Vorschau_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: DokumentEintragZeile zeile })
                return;

            VorschauTitel.Text = $"Vorschau · {zeile.Art}";
            if (zeile.Dokument is { } d)
            {
                _originalAdresse = d.Adresse;
                OriginalKnopf.Visibility = Uri.TryCreate(d.Adresse, UriKind.Absolute, out _) ? Visibility.Visible : Visibility.Collapsed;
                if (Uri.TryCreate(CloudinarySpeicher.VorschauAdresse(d.Adresse, d.MimeTyp), UriKind.Absolute, out Uri? bild))
                {
                    VorschauText.Visibility = Visibility.Collapsed;
                    BildLadeAnzeige.IsActive = true;
                    BildLadeAnzeige.Visibility = Visibility.Visible;
                    var quelle = new BitmapImage(bild);
                    quelle.ImageOpened += (_, _) =>
                    {
                        BildLadeAnzeige.IsActive = false;
                        BildLadeAnzeige.Visibility = Visibility.Collapsed;
                    };
                    VorschauBild.Source = quelle;
                    VorschauBild.Visibility = Visibility.Visible;
                }
                else
                {
                    VorschauFehlt();
                }
            }
            else
            {
                // Abliefernachweis: alle Angaben als Text.
                _originalAdresse = null;
                OriginalKnopf.Visibility = Visibility.Collapsed;
                VorschauBild.Source = null;
                VorschauBild.Visibility = Visibility.Collapsed;
                VorschauText.Visibility = Visibility.Visible;
                VorschauText.Text = zeile.Details;
            }
        }

        private void VorschauBild_ImageFailed(object sender, ExceptionRoutedEventArgs e) => VorschauFehlt();

        private void VorschauFehlt()
        {
            BildLadeAnzeige.IsActive = false;
            BildLadeAnzeige.Visibility = Visibility.Collapsed;
            VorschauBild.Visibility = Visibility.Collapsed;
            VorschauText.Visibility = Visibility.Visible;
            VorschauText.Text = "Für diese Datei gibt es keine Vorschau.\n„Original öffnen“ zeigt sie im Browser.";
        }

        private async void OriginalOeffnen_Click(object sender, RoutedEventArgs e)
        {
            if (_originalAdresse is not null && Uri.TryCreate(_originalAdresse, UriKind.Absolute, out Uri? adresse))
                await Launcher.LaunchUriAsync(adresse);
        }

        // ── Filter ───────────────────────────────────────────────────────────────────────

        private async void FilterAnwenden_Click(object sender, RoutedEventArgs e)
        {
            _tourId = null; // eigener Filter ersetzt „Sendungen der Tour“
            await LadenAsync();
        }

        private async void SuchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
                return;
            e.Handled = true;
            _tourId = null;
            await LadenAsync();
        }

        private void StatusAuswahl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_auswahlIgnorieren && IsLoaded)
                ListeAnzeigen(_aktuell?.Id);
        }

        private async void SendungListe_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_auswahlIgnorieren && SendungListe.SelectedItem is SendungTrefferZeile zeile)
                await SendungOeffnenAsync(zeile.Sendung);
        }

        // ── Dokument ergänzen ────────────────────────────────────────────────────────────

        private async void DateiAuswaehlen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                foreach (string endung in new[] { ".pdf", ".jpg", ".jpeg", ".png" })
                    picker.FileTypeFilter.Add(endung);
                // WinUI 3: Der Dialog braucht das Fenster, zu dem er gehört.
                IntPtr fenster = Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, fenster);

                var datei = await picker.PickSingleFileAsync();
                if (datei is null)
                    return;
                var info = await datei.GetBasicPropertiesAsync();
                if (info.Size > (ulong)FrachtdokumentRepository.MaxBytes)
                {
                    ZuordnenText.Text = "Die Datei ist größer als 10 MB.";
                    return;
                }
                _datei = (datei.Name, await File.ReadAllBytesAsync(datei.Path));
                DateiText.Text = $"{datei.Name} · {Groesse((long)info.Size)}";
                ZuordnenText.Text = "";
            }
            catch (Exception)
            {
                ZuordnenText.Text = "Die Datei konnte nicht gelesen werden.";
            }
        }

        private async void Zuordnen_Click(object sender, RoutedEventArgs e)
        {
            if (_arbeitet)
                return;
            if (_datei is not { } datei)
            {
                ZuordnenText.Text = "Bitte eine Datei auswählen.";
                return;
            }

            _arbeitet = true;
            ZuordnenKnopf.IsEnabled = false;
            ZuordnenText.Text = "Wird hochgeladen …";
            try
            {
                long sendung = await _repository.ZuordnenAsync(ReferenzBox.Text, ArtAuswahl.SelectedItem as string ?? "",
                    datei.Name, datei.Inhalt, BemerkungBox.Text);
                ZuordnenText.Text = $"„{datei.Name}“ wurde der Sendung {ReferenzBox.Text.Trim()} zugeordnet.";
                _datei = null;
                DateiText.Text = "Keine Datei ausgewählt";
                BemerkungBox.Text = "";
                // Die Sendung anzeigen – auch wenn sie nicht zum aktuellen Filter gehört.
                if (!_sendungen.Any(s => s.Id == sendung))
                {
                    _tourId = null;
                    SuchBox.Text = ReferenzBox.Text.Trim();
                }
                _aktuell = null;
                await LadenAsync(sendung);
            }
            catch (ArgumentException fehler)
            {
                ZuordnenText.Text = fehler.Message;
            }
            catch (InvalidOperationException fehler) when (fehler.Message.StartsWith("Der Dokumentenspeicher", StringComparison.Ordinal))
            {
                ZuordnenText.Text = fehler.Message; // Speicher nicht eingerichtet oder Upload abgelehnt (CloudinarySpeicher)
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                // Häufigster Grund: Migration 014 (Spalte dokument.bemerkung) fehlt.
                ZuordnenText.Text = "Das Dokument konnte nicht gespeichert werden. Bitte erneut versuchen " +
                                    "(fehlt Migration 014_dokument_bemerkung.sql? Siehe Systemzustand im Admin-Dashboard).";
            }
            finally
            {
                _arbeitet = false;
                ZuordnenKnopf.IsEnabled = CloudinarySpeicher.Verfuegbar;
            }
        }

        internal static string Groesse(long bytes) => bytes >= 1024 * 1024
            ? $"{(bytes / 1024d / 1024d).ToString("0.0", Deutsch)} MB"
            : $"{Math.Max(1, bytes / 1024)} KB";

        // „Heute 12:54“, „Gestern 18:02“ oder „28.09. 09:15“
        internal static string Wann(DateTime zeit)
        {
            string uhr = zeit.ToString("HH:mm", Deutsch);
            if (zeit.Date == DateTime.Today)
                return $"Heute {uhr}";
            if (zeit.Date == DateTime.Today.AddDays(-1))
                return $"Gestern {uhr}";
            return $"{zeit.ToString("dd.MM.", Deutsch)} {uhr}";
        }
    }

    // Eine Zeile in der Trefferliste.
    public sealed class SendungTrefferZeile
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public SendungTrefferZeile(DokumentSendung s)
        {
            Sendung = s;
            Referenz = s.Referenz == s.Auftragsnummer ? s.Auftragsnummer : $"{s.Referenz} · {s.Auftragsnummer}";
            KundeUndDatum = $"{s.Kunde} · Lieferung {s.Lieferdatum.ToString("dd.MM.", Deutsch)}";
            DokumenteText = s.Dokumente == 1 ? "1 Dokument" : $"{s.Dokumente} Dokumente";
            Status = s.Status;
            StatusFarbe = (Brush)Application.Current.Resources[
                s.Status is DokumentStatus.Problem or DokumentStatus.NachweisFehlt ? "ErrorBrush" : "TextBrush"];
        }

        public DokumentSendung Sendung { get; }
        public string Referenz { get; }
        public string KundeUndDatum { get; }
        public string DokumenteText { get; }
        public string Status { get; }
        public Brush StatusFarbe { get; }
    }

    // Ein Eintrag links: ein Dokument oder ein Abliefernachweis (mit Problemmeldung).
    public sealed class DokumentEintragZeile
    {
        public DokumentEintragZeile(Frachtdokument d)
        {
            Dokument = d;
            Zeit = d.HochgeladenAm;
            Art = d.Art;
            // Die Fahrer-App speichert Zufallsnamen (Guid). Dann lieber „Unterschrift.png“ zeigen.
            string endung = Path.GetExtension(d.Dateiname);
            bool zufall = Guid.TryParse(Path.GetFileNameWithoutExtension(d.Dateiname), out _);
            Titel = zufall ? (d.Art == "Unterschrift" ? "Empfangsbestätigung" : d.Art) + endung : d.Dateiname;
            string herkunft = d.AusFahrerApp ? $"Fahrer-App ({d.HochgeladenVon})" : d.HochgeladenVon;
            string art = zufall ? (d.Art == "Unterschrift" ? "Unterschrift vorhanden" : d.Art) : d.Art;
            Untertitel = $"{D11Frachtdokumente.Wann(d.HochgeladenAm)} · {art} · {herkunft} · {D11Frachtdokumente.Groesse(d.Bytes)}"
                         + (string.IsNullOrWhiteSpace(d.Bemerkung) ? "" : $"\n„{d.Bemerkung}“");
            Details = "";
        }

        public DokumentEintragZeile(Abliefernachweis n)
        {
            Zeit = n.Zeitpunkt;
            Art = "Abliefernachweis";
            Titel = "Abliefernachweis";
            string empfaenger = string.IsNullOrWhiteSpace(n.Empfaenger) ? "Empfänger nicht angegeben" : $"Empfänger {n.Empfaenger}";
            Untertitel = $"{D11Frachtdokumente.Wann(n.Zeitpunkt)} · {empfaenger} · {n.Abgeliefert} von {n.Erwartet} abgeliefert · {n.Bestaetigungsart}";
            if (n.IstProblem)
            {
                Problem = !string.IsNullOrWhiteSpace(n.Ausnahmegrund)
                    ? $"Problem gemeldet: {n.Ausnahmegrund}"
                    : $"Problem: nur {n.Abgeliefert} von {n.Erwartet} abgeliefert";
            }
            Details = string.Join("\n", new[]
            {
                $"Abgeliefert {D11Frachtdokumente.Wann(n.Zeitpunkt)} durch {n.Fahrer}",
                empfaenger,
                $"{n.Abgeliefert} von {n.Erwartet} Einheiten · Bestätigung: {n.Bestaetigungsart}",
                string.IsNullOrWhiteSpace(n.Ausnahmegrund) ? "Keine Problemmeldung" : $"Problem: {n.Ausnahmegrund}",
                string.IsNullOrWhiteSpace(n.Bemerkung) ? "" : $"Bemerkung: {n.Bemerkung}"
            }.Where(t => t.Length > 0));
        }

        public Frachtdokument? Dokument { get; }
        public DokumentEintragZeile Selbst => this;
        public DateTime Zeit { get; }
        public string Art { get; }
        public string Titel { get; }
        public string Untertitel { get; }
        public string Problem { get; } = "";
        public Visibility ProblemSichtbar => Problem.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string Details { get; }
        public string VorschauBeschreibung => $"Vorschau: {Titel}";
    }
}
