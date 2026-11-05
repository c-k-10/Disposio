using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Disposio.Views
{
    // D09 lädt links die Gespräche und rechts den Verlauf der gewählten Tour. Alle 15 Sekunden wird beides
    // neu geladen, damit Antworten des Fahrers ohne Klick erscheinen. Senden: erst speichern (Original),
    // dann übersetzen lassen. Startwert: optional eine Tour-Id, die gleich ausgewählt wird.
    public sealed partial class D09Nachrichten : UserControl
    {
        private static readonly TimeSpan Abfrageabstand = TimeSpan.FromSeconds(15);

        private readonly FirmenAnsichtKontext _kontext;
        private readonly NachrichtenRepository _repository = new();
        private readonly DispatcherTimer _timer = new();
        // Aufgeklappte „Original/Übersetzung anzeigen“ bleiben beim automatischen Neuladen offen.
        private readonly HashSet<long> _aufgeklappt = new();
        private IReadOnlyList<Gespraech> _gespraeche = Array.Empty<Gespraech>();
        private long? _tourId;
        // Aus D07 „Fahrer kontaktieren“: Diese Tour soll angezeigt werden, auch wenn sie nicht heute fährt.
        private readonly long? _startTourId;
        private int _angezeigteNachrichten = -1;
        private bool _auswahlIgnorieren;
        private bool _laedt;
        private bool _sendet;

        public D09Nachrichten(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            if (kontext.Parameter is long tourId)
                _tourId = _startTourId = tourId;

            EingabeBox.PreviewKeyDown += EingabeBox_PreviewKeyDown;
            _timer.Interval = Abfrageabstand;
            _timer.Tick += async (_, _) => await AktualisierenAsync(still: true);

            Loaded += async (_, _) =>
            {
                await AktualisierenAsync(still: false);
                if (!_kontext.IstVorschau)
                    _timer.Start();
            };
            Unloaded += (_, _) => _timer.Stop();
        }

        // ── Laden ────────────────────────────────────────────────────────────────────────

        // still = Hintergrund-Aktualisierung: keine Ladeanzeige, Fehler nur kurz im Status.
        private async Task AktualisierenAsync(bool still)
        {
            if (_kontext.IstVorschau)
            {
                ListeStatusText.Text = "In der Vorschau ohne Anmeldung werden keine Nachrichten geladen.";
                return;
            }
            if (_laedt || _sendet)
                return;

            _laedt = true;
            if (!still)
            {
                ListeLadeAnzeige.IsActive = true;
                ListeLadeAnzeige.Visibility = Visibility.Visible;
                ListeStatusText.Text = "Wird geladen …";
            }
            try
            {
                // Die gewählte Tour bleibt in der Liste, auch wenn sie sonst herausfallen würde.
                _gespraeche = await _repository.GespraecheLadenAsync(_tourId ?? _startTourId);
                ListeAnzeigen();
                if (_startTourId is long start && _tourId == start && !_gespraeche.Any(g => g.TourId == start))
                    StatusText.Text = "Diese Tour hat noch keinen Fahrer. Bitte zuerst einen Fahrer zuweisen.";
                if (_tourId is long tourId && _gespraeche.Any(g => g.TourId == tourId))
                    await VerlaufLadenAsync(tourId);
                ListeStatusText.Text = _gespraeche.Count == 1 ? "1 Fahrer" : $"{_gespraeche.Count} Fahrer";
            }
            catch (UnauthorizedAccessException)
            {
                _timer.Stop();
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                ListeStatusText.Text = "Nachrichten konnten nicht geladen werden. Neuer Versuch in Kürze.";
            }
            finally
            {
                _laedt = false;
                ListeLadeAnzeige.IsActive = false;
                ListeLadeAnzeige.Visibility = Visibility.Collapsed;
            }
        }

        private void ListeAnzeigen()
        {
            var zeilen = _gespraeche.Select(g => new GespraechZeile(g, g.TourId == _tourId)).ToList();
            _auswahlIgnorieren = true;
            GespraechListe.ItemsSource = zeilen;
            GespraechListe.SelectedItem = zeilen.FirstOrDefault(z => z.TourId == _tourId);
            _auswahlIgnorieren = false;
            ListeLeer.Visibility = zeilen.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            Gespraech? gewaehlt = _gespraeche.FirstOrDefault(g => g.TourId == _tourId);
            KopfText.Text = gewaehlt is null
                ? "Bitte links einen Fahrer wählen."
                : $"{gewaehlt.FahrerName} · Sprache {Sprachen.Name(gewaehlt.FahrerSprache)}";
            EingabeLabel.Text = gewaehlt is null ? "Nachricht" : $"Nachricht an {gewaehlt.FahrerName}";
            KopfUnterText.Text = gewaehlt is null ? "" : $"Chat zur Tour {GespraechZeile.TourText(gewaehlt)}";
            KopfUnterText.Visibility = gewaehlt is null ? Visibility.Collapsed : Visibility.Visible;
            EingabeBox.IsEnabled = SendenButton.IsEnabled = gewaehlt is not null;
        }

        private async Task VerlaufLadenAsync(long tourId)
        {
            IReadOnlyList<ChatNachricht> nachrichten = await _repository.VerlaufLadenAsync(tourId);
            if (_tourId != tourId)
                return; // inzwischen anderer Fahrer gewählt

            VerlaufListe.ItemsSource = nachrichten.Select(n => new NachrichtZeile(n, _aufgeklappt.Contains(n.Id))).ToList();
            VerlaufLeer.Visibility = nachrichten.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Nur bei neuen Nachrichten nach unten springen, sonst bleibt die Leseposition.
            if (nachrichten.Count != _angezeigteNachrichten)
            {
                _angezeigteNachrichten = nachrichten.Count;
                VerlaufScroller.UpdateLayout();
                VerlaufScroller.ChangeView(null, VerlaufScroller.ScrollableHeight, null, disableAnimation: true);
            }

            // Die eben gelesenen Nachrichten zählen links nicht mehr als neu.
            var gelesen = _gespraeche.Select(g => g.TourId == tourId ? g with { Ungelesen = 0 } : g).ToList();
            if (gelesen.Any(g => g.TourId == tourId) && _gespraeche.First(g => g.TourId == tourId).Ungelesen > 0)
            {
                _gespraeche = gelesen;
                ListeAnzeigen();
            }
        }

        // ── Bedienung ────────────────────────────────────────────────────────────────────

        private async void Gespraech_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_auswahlIgnorieren || GespraechListe.SelectedItem is not GespraechZeile zeile || zeile.TourId == _tourId)
                return;

            _tourId = zeile.TourId;
            _angezeigteNachrichten = -1;
            _aufgeklappt.Clear();
            VerlaufListe.ItemsSource = null;
            StatusText.Text = "";
            EingabeBox.Text = "";
            ListeAnzeigen();
            try
            {
                await VerlaufLadenAsync(zeile.TourId);
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                StatusText.Text = "Der Verlauf konnte nicht geladen werden.";
            }
        }

        // Strg+Enter sendet, Enter allein macht eine neue Zeile.
        private void EingabeBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            bool strg = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
            if (e.Key == VirtualKey.Enter && strg)
            {
                e.Handled = true;
                Senden_Click(sender, new RoutedEventArgs());
            }
        }

        private async void Senden_Click(object sender, RoutedEventArgs e)
        {
            if (_tourId is not long tourId || _sendet)
                return;
            string text = EingabeBox.Text.Trim();
            if (text.Length == 0)
            {
                StatusText.Text = "Bitte eine Nachricht eingeben.";
                return;
            }

            _sendet = true;
            SendenButton.IsEnabled = false;
            StatusText.Text = "Wird gesendet …";
            try
            {
                // 1. Original speichern – ab hier geht der Text nicht mehr verloren.
                var (id, brauchtUebersetzung) = await _repository.SendenAsync(tourId, text);
                EingabeBox.Text = "";

                // 2. Übersetzen lassen. Ein Fehler hier ist kein Datenverlust.
                bool uebersetzt = !brauchtUebersetzung || await UebersetzungDienst.UebersetzenAsync(id);
                StatusText.Text = uebersetzt
                    ? "Gesendet."
                    : "Gesendet. Die Übersetzung ist fehlgeschlagen – das Original ist gespeichert, bitte „Erneut übersetzen“.";
                await VerlaufLadenAsync(tourId);
            }
            catch (ArgumentException fehler)
            {
                StatusText.Text = fehler.Message; // Text bleibt im Eingabefeld
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                StatusText.Text = "Die Nachricht konnte nicht gesendet werden. Der Text bleibt im Eingabefeld – bitte erneut versuchen.";
            }
            finally
            {
                _sendet = false;
                SendenButton.IsEnabled = _tourId is not null;
            }
        }

        private async void ErneutUebersetzen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: NachrichtZeile zeile } knopf || _tourId is not long tourId)
                return;
            knopf.IsEnabled = false;
            bool ok = await UebersetzungDienst.UebersetzenAsync(zeile.Id);
            StatusText.Text = ok ? "Übersetzt." : "Die Übersetzung ist weiterhin nicht möglich. Bitte später erneut versuchen.";
            try
            {
                await VerlaufLadenAsync(tourId);
            }
            catch (Exception)
            {
                knopf.IsEnabled = true;
            }
        }

        private void Umschalten_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not HyperlinkButton { Tag: NachrichtZeile zeile })
                return;
            zeile.Umschalten();
            if (!_aufgeklappt.Add(zeile.Id))
                _aufgeklappt.Remove(zeile.Id);
        }

        // Farben aus Themes/FirmenStil.xaml (App-Ressourcen). So passen sie zur Akzentfarbe der Firma und zu hell/dunkel.
        internal static Brush Farbe(string schluessel) => (Brush)Application.Current.Resources[schluessel];

        // Akzentfarbe der Firma, leicht durchsichtig: für eigene Nachrichten und den geöffneten Chat.
        internal static Brush AkzentGetoent()
        {
            var akzent = ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color;
            return new SolidColorBrush(ColorHelper.FromArgb(0x33, akzent.R, akzent.G, akzent.B));
        }

        internal static string Uhrzeit(DateTime zeit) =>
            zeit.Date == DateTime.Today
                ? zeit.ToString("HH:mm", CultureInfo.GetCultureInfo("de-DE"))
                : zeit.ToString("dd.MM. HH:mm", CultureInfo.GetCultureInfo("de-DE"));
    }

    // Eine Karte in der linken Liste, z. B. „Jan Kowalski / T-2401 · H-FC 401 / Neue Nachricht · 06:21“.
    public sealed class GespraechZeile
    {
        public GespraechZeile(Gespraech g, bool ausgewaehlt)
        {
            TourId = g.TourId;
            FahrerName = g.FahrerName;
            TourUndFahrzeug = TourText(g);

            // Geöffneter Chat: Akzentrahmen, Tönung, Balken links. Innenabstand gleicht Balken und Rahmen aus.
            Hintergrund = ausgewaehlt ? D09Nachrichten.AkzentGetoent() : D09Nachrichten.Farbe("SurfaceBrush");
            Rahmen = D09Nachrichten.Farbe(ausgewaehlt ? "AccentBrush" : "BorderBrush");
            Rahmenstaerke = new Thickness(ausgewaehlt ? 2 : 1);
            BalkenSichtbar = ausgewaehlt ? Visibility.Visible : Visibility.Collapsed;
            Innenabstand = ausgewaehlt ? new Thickness(13, 13, 17, 13) : new Thickness(18, 14, 18, 14);
            UngelesenText = g.Ungelesen.ToString(CultureInfo.InvariantCulture);
            UngelesenSichtbar = g.Ungelesen > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (g.LetzteNachricht is DateTime zeit)
            {
                Hinweis = g.Ungelesen > 0
                    ? $"Neue Nachricht · {D09Nachrichten.Uhrzeit(zeit)}"
                    : $"Letzte Nachricht · {D09Nachrichten.Uhrzeit(zeit)}";
            }
            HinweisSichtbar = Hinweis.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            HinweisSchrift = g.Ungelesen > 0 ? FontWeights.SemiBold : FontWeights.Normal;
        }

        // „T-2401 · H-FC 401“
        public static string TourText(Gespraech g) =>
            g.Kennzeichen.Length > 0 ? $"{g.Tournummer} · {g.Kennzeichen}" : g.Tournummer;

        public long TourId { get; }
        public Brush Hintergrund { get; }
        public Brush Rahmen { get; }
        public Thickness Rahmenstaerke { get; }
        public Visibility BalkenSichtbar { get; }
        public Thickness Innenabstand { get; }
        public string FahrerName { get; }
        public string TourUndFahrzeug { get; }
        public string UngelesenText { get; }
        public Visibility UngelesenSichtbar { get; }
        public string Hinweis { get; } = "";
        public Visibility HinweisSichtbar { get; }
        public Windows.UI.Text.FontWeight HinweisSchrift { get; }
    }

    // Eine Sprechblase. Fahrer: Übersetzung oben, Original zum Aufklappen.
    // Disponent: eigener Text oben, Übersetzung (was der Fahrer liest) zum Aufklappen.
    public sealed class NachrichtZeile : INotifyPropertyChanged
    {
        // Nachrichten des Fahrers ohne Füllung, eigene leicht in der Akzentfarbe der Firma.
        private static readonly Brush FahrerHintergrund = new SolidColorBrush(Colors.Transparent);

        private readonly string _zweittextName;
        private bool _offen;

        public NachrichtZeile(ChatNachricht n, bool aufgeklappt = false)
        {
            Id = n.Id;
            _offen = aufgeklappt;
            string zeit = D09Nachrichten.Uhrzeit(n.Zeit);
            string sprache = Sprachen.Name(n.Originalsprache);
            bool uebersetzt = n.Status == UebersetzungsStatus.Uebersetzt && !string.IsNullOrEmpty(n.Uebersetzung);
            bool fehlgeschlagen = n.Status == UebersetzungsStatus.Fehlgeschlagen;

            Ausrichtung = n.VomFahrer ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            Hintergrund = n.VomFahrer ? FahrerHintergrund : D09Nachrichten.AkzentGetoent();

            if (n.VomFahrer)
            {
                // Übersetzt lesen, Original prüfen können.
                Haupttext = uebersetzt ? n.Uebersetzung! : n.Text;
                Zweittext = n.Text;
                _zweittextName = "Original";
                Meta = n.Status switch
                {
                    UebersetzungsStatus.Uebersetzt => $"{zeit} · Aus {sprache} übersetzt",
                    UebersetzungsStatus.Ausstehend => $"{zeit} · Original ({sprache}) · Übersetzung läuft …",
                    UebersetzungsStatus.Fehlgeschlagen => $"{zeit} · Original ({sprache})",
                    _ => zeit
                };
                Fehlertext = "Übersetzung fehlgeschlagen – Sie sehen das Original.";
            }
            else
            {
                // Eigener Text, Übersetzung für den Fahrer prüfen können.
                Haupttext = n.Text;
                Zweittext = n.Uebersetzung ?? "";
                _zweittextName = "Übersetzung";
                string ziel = Sprachen.Ziel(n.Zielsprache);
                string wer = n.VonMir ? "" : $"{n.Absender} · ";
                Meta = wer + n.Status switch
                {
                    UebersetzungsStatus.Uebersetzt => $"{zeit} · Für Empfänger ins {ziel} übersetzt",
                    UebersetzungsStatus.Ausstehend => $"{zeit} · Übersetzung ins {ziel} läuft …",
                    UebersetzungsStatus.Fehlgeschlagen => $"{zeit} · Noch nicht übersetzt",
                    _ => zeit
                };
                Fehlertext = "Übersetzung fehlgeschlagen – der Fahrer sieht vorerst Ihr Original.";
            }

            UmschaltenSichtbar = uebersetzt ? Visibility.Visible : Visibility.Collapsed;
            FehlerSichtbar = fehlgeschlagen ? Visibility.Visible : Visibility.Collapsed;

            // Zustellung nur bei Nachrichten an den Fahrer. Die Fahrer-App setzt „zugestellt“ und „gelesen“.
            ZustellungSichtbar = n.VomFahrer ? Visibility.Collapsed : Visibility.Visible;
            if (n.Gelesen)
            {
                string wann = n.GelesenAm is DateTime am ? $" {D09Nachrichten.Uhrzeit(am)}" : "";
                Zustellung = $"✓✓ Gelesen{wann}";
                ZustellungErklaerung = "Der Fahrer hat die Nachricht geöffnet.";
                ZustellungFarbe = D09Nachrichten.Farbe("TextBrush"); // hell und fett, gut lesbar auf jeder Akzentfarbe
            }
            else if (n.Uebertragung == UebertragungsStatus.Zugestellt)
            {
                Zustellung = "✓✓ Zugestellt";
                ZustellungErklaerung = "Die Nachricht ist auf dem Handy des Fahrers angekommen, aber noch nicht geöffnet.";
                ZustellungFarbe = D09Nachrichten.Farbe("MutedTextBrush");
            }
            else
            {
                Zustellung = "✓ Gesendet";
                ZustellungErklaerung = "Die Nachricht ist gespeichert. Die Fahrer-App hat sie noch nicht abgeholt.";
                ZustellungFarbe = D09Nachrichten.Farbe("MutedTextBrush");
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public long Id { get; }
        public NachrichtZeile Selbst => this;
        public HorizontalAlignment Ausrichtung { get; }
        public Brush Hintergrund { get; }
        public string Haupttext { get; }
        public string Zweittext { get; }
        public string Meta { get; }
        public string Fehlertext { get; }
        public Visibility FehlerSichtbar { get; }
        public Visibility UmschaltenSichtbar { get; }
        public string Zustellung { get; }
        public string ZustellungErklaerung { get; }
        public Brush ZustellungFarbe { get; }
        public Visibility ZustellungSichtbar { get; }

        public string UmschaltText => _offen ? $"▼ {_zweittextName} ausblenden" : $"▶ {_zweittextName} anzeigen";
        public Visibility ZweittextSichtbar => _offen ? Visibility.Visible : Visibility.Collapsed;

        public void Umschalten()
        {
            _offen = !_offen;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UmschaltText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ZweittextSichtbar)));
        }
    }
}
