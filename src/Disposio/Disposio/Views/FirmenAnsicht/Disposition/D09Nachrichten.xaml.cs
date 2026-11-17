using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
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
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        // Kleiner als das bleibt die Ansicht nicht; in sehr kleinen Fenstern scrollt dann doch die Seite.
        private const double MindestHoehe = 480;
        // So nah am Ende gilt der Verlauf als „unten“ (dann springt er bei neuen Nachrichten mit).
        private const double UntenToleranz = 48;

        private readonly FirmenAnsichtKontext _kontext;
        private readonly NachrichtenRepository _repository = new();
        private readonly DispatcherTimer _timer = new();
        // Aufgeklappte „Original/Übersetzung anzeigen“ bleiben beim automatischen Neuladen offen.
        private readonly HashSet<long> _aufgeklappt = new();
        // Nachrichten des Fahrers, die beim Öffnen oder während der Chat offen ist noch ungelesen waren.
        // Sie bleiben hervorgehoben, bis man antwortet oder einen anderen Chat öffnet.
        private readonly HashSet<long> _neu = new();
        private IReadOnlyList<Gespraech> _gespraeche = Array.Empty<Gespraech>();
        private long? _tourId;
        // Aus D07 „Fahrer kontaktieren“: Diese Tour soll angezeigt werden, auch wenn sie nicht heute fährt.
        private readonly long? _startTourId;
        private int _angezeigteNachrichten = -1;
        // Nur neu zeichnen, wenn sich wirklich etwas geändert hat: kein Flackern, Scrollposition bleibt.
        private string _verlaufSignatur = "";
        private string _listeSignatur = "";
        private bool _auswahlIgnorieren;
        private bool _laedt;
        private bool _sendet;
        private ScrollViewer? _seitenScroll;

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
                LayoutUpdated += Ansicht_LayoutUpdated;
                await AktualisierenAsync(still: false);
                if (!_kontext.IstVorschau)
                    _timer.Start();
            };
            Unloaded += (_, _) =>
            {
                _timer.Stop();
                LayoutUpdated -= Ansicht_LayoutUpdated;
                _seitenScroll = null;
            };
        }

        // ── Höhe: genau der sichtbare Bereich ────────────────────────────────────────────

        // Die Firmenansicht legt jede Ansicht in einen ScrollViewer (FirmenAnsicht.ContentScroll). Damit nicht die
        // ganze Seite scrollt, wird D09 genau so hoch wie der Platz unter Titel und Beschreibung. LayoutUpdated
        // trifft auch Fenstergröße, Admin-Hinweis und umbrechende Texte. Gesetzt wird nur bei echter Änderung.
        private void Ansicht_LayoutUpdated(object? sender, object e)
        {
            _seitenScroll ??= SeitenScrollFinden();
            if (_seitenScroll?.Content is not FrameworkElement inhalt || _seitenScroll.ViewportHeight <= 0)
                return;

            double oben = TransformToVisual(inhalt).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            double unten = inhalt is StackPanel panel ? panel.Padding.Bottom : 0;
            double hoehe = Math.Max(MindestHoehe, Math.Floor(_seitenScroll.ViewportHeight - oben - unten - 1));
            if (double.IsNaN(Wurzel.Height) || Math.Abs(Wurzel.Height - hoehe) > 1)
                Wurzel.Height = hoehe;
        }

        private ScrollViewer? SeitenScrollFinden()
        {
            DependencyObject? knoten = VisualTreeHelper.GetParent(this);
            while (knoten is not null and not ScrollViewer)
                knoten = VisualTreeHelper.GetParent(knoten);
            return knoten as ScrollViewer;
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
            string suche = SucheBox.Text.Trim();
            // Der geöffnete Chat bleibt auch bei einer Suche in der Liste.
            var treffer = _gespraeche.Where(g => g.TourId == _tourId || Passt(g, suche)).ToList();

            ListeStatusText.Text = suche.Length > 0
                ? $"{treffer.Count} von {_gespraeche.Count} Fahrern"
                : _gespraeche.Count == 1 ? "1 Fahrer" : $"{_gespraeche.Count} Fahrer";
            ListeLeer.Text = suche.Length > 0
                ? "Kein Fahrer und keine Tour passt zur Suche."
                : "Heute gibt es keine Touren mit zugewiesenem Fahrer.";
            ListeLeer.Visibility = treffer.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Liste nur neu aufbauen, wenn sich etwas geändert hat – sonst springt sie alle 15 s nach oben.
            string signatur = string.Join("|", treffer.Select(g => $"{g.TourId}:{g.Ungelesen}:{g.LetzteNachricht:O}:{g.FahrerName}"))
                              + "#" + _tourId;
            if (signatur != _listeSignatur)
            {
                _listeSignatur = signatur;
                var zeilen = treffer.Select(g => new GespraechZeile(g, g.TourId == _tourId)).ToList();
                _auswahlIgnorieren = true;
                GespraechListe.ItemsSource = zeilen;
                GespraechListe.SelectedItem = zeilen.FirstOrDefault(z => z.TourId == _tourId);
                _auswahlIgnorieren = false;
            }

            KopfAnzeigen(_gespraeche.FirstOrDefault(g => g.TourId == _tourId));
        }

        private static bool Passt(Gespraech g, string suche) =>
            suche.Length == 0
            || g.FahrerName.Contains(suche, StringComparison.CurrentCultureIgnoreCase)
            || g.Tournummer.Contains(suche, StringComparison.CurrentCultureIgnoreCase)
            || g.Kennzeichen.Contains(suche, StringComparison.CurrentCultureIgnoreCase);

        private void KopfAnzeigen(Gespraech? gewaehlt)
        {
            if (gewaehlt is null)
            {
                KopfText.Text = "Kein Chat geöffnet";
                KopfUnterText.Text = "Links einen Fahrer wählen, um den Verlauf zu sehen.";
                KopfAvatar.Visibility = TourdetailsButton.Visibility = Visibility.Collapsed;
                EingabeBox.PlaceholderText = "Nachricht schreiben …";
                EingabeBox.IsEnabled = SendenButton.IsEnabled = false;
                return;
            }

            KopfText.Text = gewaehlt.FahrerName;
            KopfKuerzel.Text = GespraechZeile.KuerzelVon(gewaehlt.FahrerName);
            KopfAvatar.Visibility = TourdetailsButton.Visibility = Visibility.Visible;

            // „Tour T-2401 · H-FC 401 · spricht Polnisch, wird automatisch übersetzt“
            string sprache = Sprachen.Name(gewaehlt.FahrerSprache);
            string? meine = BenutzerSitzung.AktuellerBenutzer?.Sprache;
            string uebersetzung = meine is not null && meine != gewaehlt.FahrerSprache
                ? $"spricht {sprache}, wird automatisch übersetzt"
                : $"spricht {sprache}";
            KopfUnterText.Text = $"Tour {GespraechZeile.TourText(gewaehlt)} · {uebersetzung}";

            string vorname = gewaehlt.FahrerName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? gewaehlt.FahrerName;
            EingabeBox.PlaceholderText = $"Nachricht an {vorname} schreiben …";
            EingabeBox.IsEnabled = true;
            SendenButton.IsEnabled = !_sendet;
        }

        /// <param name="nachUnten">Nach dem eigenen Senden immer ans Ende springen.</param>
        private async Task VerlaufLadenAsync(long tourId, bool nachUnten = false)
        {
            IReadOnlyList<ChatNachricht> nachrichten = await _repository.VerlaufLadenAsync(tourId);
            if (_tourId != tourId)
                return; // inzwischen anderer Fahrer gewählt

            // Die Abfrage läuft vor dem Gelesen-Markieren: Hier steht noch, was neu war.
            foreach (ChatNachricht n in nachrichten)
                if (n.VomFahrer && !n.Gelesen)
                    _neu.Add(n.Id);

            string signatur = VerlaufSignatur(nachrichten) + "#" + _neu.Count;
            if (signatur != _verlaufSignatur)
            {
                bool ersterAufbau = _angezeigteNachrichten < 0;
                bool neue = nachrichten.Count > _angezeigteNachrichten;
                bool warUnten = VerlaufScroller.ScrollableHeight - VerlaufScroller.VerticalOffset < UntenToleranz;

                _verlaufSignatur = signatur;
                _angezeigteNachrichten = nachrichten.Count;
                VerlaufListe.ItemsSource = Zeilen(nachrichten);
                VerlaufLeer.Visibility = nachrichten.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                // Wer gerade weiter oben liest, wird nicht herausgerissen – stattdessen erscheint „↓ Neue Nachricht“.
                if (ersterAufbau || nachUnten || (neue && warUnten))
                    AnsEndeScrollen();
                else if (neue)
                    NeuButton.Visibility = Visibility.Visible;
            }

            // Die eben gelesenen Nachrichten zählen links nicht mehr als neu.
            if (_gespraeche.FirstOrDefault(g => g.TourId == tourId) is { Ungelesen: > 0 })
            {
                _gespraeche = _gespraeche.Select(g => g.TourId == tourId ? g with { Ungelesen = 0 } : g).ToList();
                ListeAnzeigen();
            }
        }

        // Fahrer- und Tagesgrenzen: Über der ersten Nachricht eines Tages steht „Heute“, „Gestern“ oder das Datum.
        private List<NachrichtZeile> Zeilen(IReadOnlyList<ChatNachricht> nachrichten)
        {
            var zeilen = new List<NachrichtZeile>(nachrichten.Count);
            DateTime? vorherigerTag = null;
            long? ersteNeue = _neu.Count > 0 ? _neu.Min() : null;
            foreach (ChatNachricht n in nachrichten)
            {
                zeilen.Add(new NachrichtZeile(n, _aufgeklappt.Contains(n.Id), neuerTag: vorherigerTag != n.Zeit.Date,
                    neu: _neu.Contains(n.Id), neuTrenner: n.Id == ersteNeue));
                vorherigerTag = n.Zeit.Date;
            }
            return zeilen;
        }

        private static string VerlaufSignatur(IReadOnlyList<ChatNachricht> nachrichten)
        {
            var text = new StringBuilder();
            foreach (ChatNachricht n in nachrichten)
                text.Append(n.Id).Append(':').Append(n.Status).Append(':').Append(n.Uebersetzung?.Length ?? -1)
                    .Append(':').Append(n.Uebertragung).Append(':').Append(n.Gelesen).Append('|');
            return text.ToString();
        }

        private void AnsEndeScrollen()
        {
            NeuButton.Visibility = Visibility.Collapsed;
            VerlaufScroller.UpdateLayout();
            VerlaufScroller.ChangeView(null, VerlaufScroller.ScrollableHeight, null, disableAnimation: true);
        }

        // ── Bedienung ────────────────────────────────────────────────────────────────────

        private async void Gespraech_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_auswahlIgnorieren || GespraechListe.SelectedItem is not GespraechZeile zeile || zeile.TourId == _tourId)
                return;

            _tourId = zeile.TourId;
            _angezeigteNachrichten = -1;
            _verlaufSignatur = "";
            _aufgeklappt.Clear();
            _neu.Clear();
            VerlaufListe.ItemsSource = null;
            VerlaufLeer.Visibility = Visibility.Collapsed;
            NeuButton.Visibility = Visibility.Collapsed;
            StatusText.Text = "";
            EingabeBox.Text = "";
            ListeAnzeigen();
            try
            {
                await VerlaufLadenAsync(zeile.TourId);
                EingabeBox.Focus(FocusState.Programmatic);
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

        private void Suche_TextChanged(object sender, TextChangedEventArgs e) => ListeAnzeigen();

        private void Tourdetails_Click(object sender, RoutedEventArgs e)
        {
            if (_tourId is long tourId)
                _kontext.Oeffnen("D07", tourId);
        }

        private void NeuButton_Click(object sender, RoutedEventArgs e) => AnsEndeScrollen();

        private void Verlauf_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (!e.IsIntermediate && VerlaufScroller.ScrollableHeight - VerlaufScroller.VerticalOffset < UntenToleranz)
                NeuButton.Visibility = Visibility.Collapsed;
        }

        private void Eingabe_TextChanged(object sender, TextChangedEventArgs e)
        {
            int laenge = EingabeBox.Text.Length;
            ZeichenText.Text = laenge == 0
                ? "Strg + Enter sendet"
                : $"{laenge} / {NachrichtenRepository.MaximaleLaenge} · Strg + Enter sendet";
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
                // Nur leeren, wenn inzwischen nichts Neues getippt wurde.
                if (EingabeBox.Text.Trim() == text)
                    EingabeBox.Text = "";

                // Wer antwortet, hat die neuen Nachrichten gesehen: Hervorhebung weg.
                _neu.Clear();

                // 2. Übersetzen lassen. Ein Fehler hier ist kein Datenverlust.
                bool uebersetzt = !brauchtUebersetzung || await UebersetzungDienst.UebersetzenAsync(id);
                StatusText.Text = uebersetzt
                    ? "Gesendet."
                    : "Gesendet. Die Übersetzung ist fehlgeschlagen – das Original ist gespeichert, bitte „Erneut übersetzen“.";
                await VerlaufLadenAsync(tourId, nachUnten: true);
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
                EingabeBox.Focus(FocusState.Programmatic);
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
            // Ohne Änderung wird der Verlauf nicht neu aufgebaut – dann muss der Knopf wieder gehen.
            if (!ok)
                knopf.IsEnabled = true;
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
                ? zeit.ToString("HH:mm", Deutsch)
                : zeit.ToString("dd.MM. HH:mm", Deutsch);

        // Kurz für die linke Liste: „06:21“, „Gestern“, „14.11.“
        internal static string KurzeZeit(DateTime zeit)
        {
            if (zeit.Date == DateTime.Today)
                return zeit.ToString("HH:mm", Deutsch);
            if (zeit.Date == DateTime.Today.AddDays(-1))
                return "Gestern";
            return zeit.ToString("dd.MM.", Deutsch);
        }

        // Tagestrenner im Verlauf: „Heute“, „Gestern“, „Montag, 16. November“
        internal static string TagesText(DateTime zeit)
        {
            if (zeit.Date == DateTime.Today)
                return "Heute";
            if (zeit.Date == DateTime.Today.AddDays(-1))
                return "Gestern";
            return zeit.ToString(zeit.Year == DateTime.Today.Year ? "dddd, d. MMMM" : "dddd, d. MMMM yyyy", Deutsch);
        }
    }

    // Eine Karte in der linken Liste: Kürzel, „Jan Kowalski“, „T-2401 · H-FC 401“, rechts Zeit und Anzahl neuer Nachrichten.
    public sealed class GespraechZeile
    {
        public GespraechZeile(Gespraech g, bool ausgewaehlt)
        {
            TourId = g.TourId;
            FahrerName = g.FahrerName;
            Kuerzel = KuerzelVon(g.FahrerName);
            TourUndFahrzeug = TourText(g);

            // Geöffneter Chat: Akzentrahmen, Tönung, Balken links. Innenabstand gleicht Balken und Rahmen aus.
            Hintergrund = ausgewaehlt ? D09Nachrichten.AkzentGetoent() : D09Nachrichten.Farbe("SurfaceBrush");
            bool neu = g.Ungelesen > 0;
            // Ungelesen: Rahmen in der Akzentfarbe (plus Zeile „● neue Nachricht“ und Zahl rechts).
            Rahmen = D09Nachrichten.Farbe(ausgewaehlt || neu ? "AccentBrush" : "BorderBrush");
            Rahmenstaerke = new Thickness(ausgewaehlt ? 2 : 1);
            BalkenSichtbar = ausgewaehlt ? Visibility.Visible : Visibility.Collapsed;
            Innenabstand = ausgewaehlt ? new Thickness(9, 11, 13, 11) : new Thickness(14, 12, 14, 12);

            NeuText = g.Ungelesen == 1 ? "● Neue Nachricht" : $"● {Math.Min(g.Ungelesen, 99)} neue Nachrichten";
            UngelesenText = g.Ungelesen > 99 ? "99+" : g.Ungelesen.ToString(CultureInfo.InvariantCulture);
            UngelesenSichtbar = neu ? Visibility.Visible : Visibility.Collapsed;
            Zeit = g.LetzteNachricht is DateTime zeit ? D09Nachrichten.KurzeZeit(zeit) : "";
            ZeitFarbe = D09Nachrichten.Farbe(neu ? "AccentBrush" : "MutedTextBrush");
            ZeitSchrift = neu ? FontWeights.SemiBold : FontWeights.Normal;
            ZeitErklaerung = neu
                ? (g.Ungelesen == 1 ? "1 neue Nachricht" : $"{g.Ungelesen} neue Nachrichten")
                : g.LetzteNachricht is DateTime letzte ? $"Letzte Nachricht {D09Nachrichten.Uhrzeit(letzte)}" : "Noch keine Nachrichten";
        }

        // „T-2401 · H-FC 401“
        public static string TourText(Gespraech g) =>
            g.Kennzeichen.Length > 0 ? $"{g.Tournummer} · {g.Kennzeichen}" : g.Tournummer;

        // „Jan Kowalski“ → „JK“
        public static string KuerzelVon(string name)
        {
            string[] teile = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (teile.Length == 0)
                return "?";
            string kuerzel = teile.Length == 1 ? teile[0][..1] : $"{teile[0][0]}{teile[^1][0]}";
            return kuerzel.ToUpper(CultureInfo.CurrentCulture);
        }

        public long TourId { get; }
        public Brush Hintergrund { get; }
        public Brush Rahmen { get; }
        public Thickness Rahmenstaerke { get; }
        public Visibility BalkenSichtbar { get; }
        public Thickness Innenabstand { get; }
        public string Kuerzel { get; }
        public string FahrerName { get; }
        public string TourUndFahrzeug { get; }
        public string UngelesenText { get; }
        public Visibility UngelesenSichtbar { get; }
        public string NeuText { get; }
        public string Zeit { get; }
        public Brush ZeitFarbe { get; }
        public Windows.UI.Text.FontWeight ZeitSchrift { get; }
        public string ZeitErklaerung { get; }
    }

    // Eine Sprechblase. Fahrer: Übersetzung oben, Original zum Aufklappen.
    // Disponent: eigener Text oben, Übersetzung (was der Fahrer liest) zum Aufklappen.
    public sealed class NachrichtZeile : INotifyPropertyChanged
    {
        private readonly string _zweittextName;
        private bool _offen;

        public NachrichtZeile(ChatNachricht n, bool aufgeklappt = false, bool neuerTag = false,
            bool neu = false, bool neuTrenner = false)
        {
            Id = n.Id;
            _offen = aufgeklappt;
            string zeit = n.Zeit.ToString("HH:mm", CultureInfo.GetCultureInfo("de-DE"));
            string sprache = Sprachen.Name(n.Originalsprache);
            bool uebersetzt = n.Status == UebersetzungsStatus.Uebersetzt && !string.IsNullOrEmpty(n.Uebersetzung);
            bool fehlgeschlagen = n.Status == UebersetzungsStatus.Fehlgeschlagen;

            Tagestrenner = neuerTag ? D09Nachrichten.TagesText(n.Zeit) : "";
            TagSichtbar = neuerTag ? Visibility.Visible : Visibility.Collapsed;

            // Fahrer links auf grauer Fläche, eigene rechts leicht in der Akzentfarbe der Firma.
            Ausrichtung = n.VomFahrer ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            Hintergrund = n.VomFahrer ? D09Nachrichten.Farbe("StatusBoxBrush") : D09Nachrichten.AkzentGetoent();
            Rahmen = n.VomFahrer ? D09Nachrichten.Farbe("BorderBrush") : new SolidColorBrush(Colors.Transparent);

            // Neue Nachricht des Fahrers: Strich links in der Akzentfarbe (wie die Statuszeile der Fahrer-App)
            // und darüber einmal die Linie „Neue Nachrichten“. Innenabstand gleicht den dickeren Rand aus.
            NeuTrennerSichtbar = neuTrenner ? Visibility.Visible : Visibility.Collapsed;
            if (neu)
                Rahmen = D09Nachrichten.Farbe("AccentBrush");
            RahmenDicke = neu ? new Thickness(4, 1, 1, 1) : new Thickness(1);
            Innenabstand = neu ? new Thickness(13, 10, 16, 10) : new Thickness(16, 10, 16, 10);

            // Name nur bei Nachrichten anderer Disponenten; wer der Fahrer ist, steht schon im Kopf.
            Absender = !n.VomFahrer && !n.VonMir ? n.Absender : "";
            AbsenderSichtbar = Absender.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

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
                Meta = n.Status switch
                {
                    UebersetzungsStatus.Uebersetzt => $"{zeit} · Ins {ziel} übersetzt",
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
        public string Tagestrenner { get; }
        public Visibility TagSichtbar { get; }
        public HorizontalAlignment Ausrichtung { get; }
        public Brush Hintergrund { get; }
        public Brush Rahmen { get; }
        public Thickness RahmenDicke { get; }
        public Thickness Innenabstand { get; }
        public Visibility NeuTrennerSichtbar { get; }
        public string Absender { get; }
        public Visibility AbsenderSichtbar { get; }
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
