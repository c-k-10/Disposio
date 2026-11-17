using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace Disposio.Views
{
    // D02 lädt die Sendungen eines Tages. Das Datum lädt neu aus der Datenbank; Suche und Status filtern nur die
    // geladene Liste. Aufbau: oben „Noch zu verplanen“ (Karten mit Häkchen für D05/D13 und Feldern für Fahrer und
    // Fahrzeug), darunter „Verplant“ nach Tour gruppiert. Rechts die am Tag verfügbaren Fahrer und Fahrzeuge (#105).
    // - Auf eine offene Sendung ziehen: nur vormerken. Erst „Zuweisungen speichern“ legt für jede vollständig
    //   vorgemerkte Sendung eine Tour an (TourRepository.SendungDirektZuweisenAsync, Regeln wie D16).
    //   So kann der Disponent erst in Ruhe verteilen und dann alles auf einmal speichern.
    // - Auf den Kopf einer Tour ziehen bzw. ✕: Fahrer/Fahrzeug der Tour tauschen oder entfernen (RessourceAendernAsync).
    // - „⋯“ → „Aus Tour nehmen …“: Sendung wieder lösen (SendungAusTourNehmenAsync).
    // Nur Touren, die noch nicht losgefahren sind, lassen sich ändern.
    // - Pfeil neben „⋯“: Sendung aufklappen (alle Angaben + Kartenausschnitt mit der Strecke, TomTom-Routing).
    public sealed partial class D02Sendungen : UserControl
    {
        private const string AlleStatus = "Alle";
        private const string NurUnverplant = "Nur unverplante";

        private readonly FirmenAnsichtKontext _kontext;
        private readonly SendungRepository _repository = new();
        private readonly TourRepository _tourRepository = new();
        // Was gerade aus „Verfügbar am …“ gezogen wird.
        private VerfuegbarZeile? _gezogen;
        // Vorgemerkte Fahrer/Fahrzeuge je offener Sendung (Auftrags-Id) und Zustand je Tour (Tour-Id).
        private readonly Dictionary<long, Vormerkung> _vorgemerkt = new();
        private readonly Dictionary<long, Vormerkung> _tourZustand = new();
        private readonly HashSet<long> _ausgewaehlt = new();
        private IReadOnlyList<SendungUebersicht> _sendungen = Array.Empty<SendungUebersicht>();
        private DateOnly? _geladenerTag;
        private CancellationTokenSource? _laden;
        // Nach dem Speichern in D03 kommt eine Erfolgsmeldung als Startwert. Sie erscheint nur einmal.
        private string? _meldung;

        // Aufgeklappte Sendung (immer nur eine, damit nur ein Kartenausschnitt läuft) und geladene Details.
        private long? _aufgeklappt;
        private readonly Dictionary<long, SendungEntwurf> _details = new();
        private readonly Dictionary<long, string> _detailFehler = new();
        // Berechnete Strecken (JSON aus der Kartenseite), damit beim Neuaufbau nicht neu gerechnet wird.
        private readonly Dictionary<long, string> _strecken = new();
        private const string StreckeHost = "disposio-strecke.local";
        private static bool _streckeGeschrieben;

        public D02Sendungen(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;

            // Nach dem Speichern einer Sendung (D03) springt die Liste auf deren Abholtag.
            if (kontext.Parameter is SendungGespeichert gespeichert)
            {
                DatumAuswahl.Date = gespeichert.Tag.ToDateTime(TimeOnly.MinValue);
                _meldung = gespeichert.Meldung;
            }
            else
            {
                DatumAuswahl.Date = DateTimeOffset.Now.Date;
            }
            StatusListeAufbauen();

            Loaded += async (_, _) => await AnwendenAsync();
            Loaded += (_, _) => MitlaufenStarten();
            Unloaded += (_, _) => _laden?.Cancel();
            Unloaded += (_, _) => MitlaufenBeenden();
        }

        // ---- Spalte „Verfügbar am …“ läuft beim Scrollen mit ----
        // Die Seite scrollt im ScrollViewer der FirmenAnsicht. Die Spalte soll oben im sichtbaren Bereich bleiben,
        // aber nie unter das Ende der Sendungsliste rutschen. Ist sie höher als das Fenster, scrollt sie in sich selbst.
        //
        // Damit sie dabei nicht zittert, bewegt die Grafik-Engine (Composition) sie selbst – im selben Bild wie die
        // Seite. Ein Ausdruck koppelt ihre Verschiebung direkt an die Scrollposition:
        //     Ziel = Clamp(Abstand − (Start + Scroll.Translation.Y), 0, Max)
        // Mausklicks und Drag & Drop kennen diese Composition-Verschiebung aber nicht. Deshalb steht die Verschiebung
        // im Ruhezustand zusätzlich im RenderTransform (das kennt XAML), und der Ausdruck liefert nur den Unterschied
        // dazu („− Ruhe“). Sobald das Scrollen aufhört (ViewChanged ohne IsIntermediate), werden RenderTransform und
        // „Ruhe“ gleichzeitig nachgeführt – dann stimmen Bild und Klickfläche wieder überein.
        // Falls Composition nicht verfügbar ist, läuft die alte Variante (Nachziehen bei jedem Scrollschritt).

        private ScrollViewer? _seitenScroll;
        private const double MitlaufAbstand = 16;
        private Microsoft.UI.Composition.Visual? _verfuegbarVisual;
        private Microsoft.UI.Composition.CompositionPropertySet? _mitlaufWerte;

        private void MitlaufenStarten()
        {
            MitlaufenBeenden();
            DependencyObject? knoten = VisualTreeHelper.GetParent(this);
            while (knoten is not null and not ScrollViewer)
                knoten = VisualTreeHelper.GetParent(knoten);
            _seitenScroll = knoten as ScrollViewer;
            if (_seitenScroll is null)
                return;
            _seitenScroll.ViewChanging += Seite_ViewChanging;
            _seitenScroll.ViewChanged += Seite_ViewChanged;
            _seitenScroll.SizeChanged += Seite_SizeChanged;
            InhaltRaster.SizeChanged += Seite_SizeChanged;
            VerfuegbarKarte.SizeChanged += Seite_SizeChanged;
            KompositionStarten();
            VerfuegbarNachziehen(null);
        }

        private void MitlaufenBeenden()
        {
            if (_seitenScroll is not null)
            {
                _seitenScroll.ViewChanging -= Seite_ViewChanging;
                _seitenScroll.ViewChanged -= Seite_ViewChanged;
                _seitenScroll.SizeChanged -= Seite_SizeChanged;
                _seitenScroll = null;
            }
            InhaltRaster.SizeChanged -= Seite_SizeChanged;
            VerfuegbarKarte.SizeChanged -= Seite_SizeChanged;
            _verfuegbarVisual?.StopAnimation("Translation");
            _verfuegbarVisual = null;
            _mitlaufWerte = null;
        }

        private void KompositionStarten()
        {
            if (_seitenScroll is null)
                return;
            try
            {
                var scrollWerte = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
                    .GetScrollViewerManipulationPropertySet(_seitenScroll);
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(VerfuegbarKarte);
                var compositor = visual.Compositor;

                var werte = compositor.CreatePropertySet();
                werte.InsertScalar("Abstand", (float)MitlaufAbstand);
                werte.InsertScalar("Start", 0f);   // Oberkante des Rasters im Seiteninhalt
                werte.InsertScalar("Max", 0f);     // weiter darf die Spalte nicht nach unten
                werte.InsertScalar("Ruhe", 0f);    // Verschiebung, die gerade im RenderTransform steht

                var ausdruck = compositor.CreateExpressionAnimation(
                    "Vector3(0, Clamp(p.Abstand - (p.Start + s.Translation.Y), 0, p.Max) - p.Ruhe, 0)");
                ausdruck.SetReferenceParameter("s", scrollWerte);
                ausdruck.SetReferenceParameter("p", werte);

                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(VerfuegbarKarte, true);
                visual.StartAnimation("Translation", ausdruck);
                _verfuegbarVisual = visual;
                _mitlaufWerte = werte;
            }
            catch (Exception)
            {
                _verfuegbarVisual = null;
                _mitlaufWerte = null;   // alte Variante übernimmt
            }
        }

        private void Seite_ViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
        {
            // Mit Composition nichts tun: Die Grafik-Engine bewegt die Spalte schon selbst.
            if (_mitlaufWerte is null)
                VerfuegbarNachziehen(e.NextView.VerticalOffset);
        }

        private void Seite_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            // Mit Composition nur nachführen, wenn das Scrollen zu Ende ist (für Klicks und Drag & Drop).
            if (_mitlaufWerte is null || !e.IsIntermediate)
                VerfuegbarNachziehen(null);
        }

        private void Seite_SizeChanged(object sender, SizeChangedEventArgs e) => VerfuegbarNachziehen(null);

        /// <param name="naechsterOffset">Scrollposition, die gleich angezeigt wird (aus ViewChanging), sonst die aktuelle.</param>
        private void VerfuegbarNachziehen(double? naechsterOffset)
        {
            if (_seitenScroll is null || InhaltRaster.ActualHeight <= 0)
                return;

            // Nie höher als der sichtbare Bereich, sonst wären die unteren Einträge nicht erreichbar.
            double maxHoehe = Math.Max(300, _seitenScroll.ViewportHeight - 2 * MitlaufAbstand);
            if (Math.Abs(VerfuegbarKarte.MaxHeight - maxHoehe) > 0.5)
                VerfuegbarKarte.MaxHeight = maxHoehe;

            double offset = _seitenScroll.VerticalOffset;
            double startImInhalt;
            try
            {
                startImInhalt = InhaltRaster.TransformToVisual(_seitenScroll)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y + offset;
            }
            catch (Exception)
            {
                return;   // gerade nicht im selben Baum (z. B. beim Entladen)
            }
            double spielraum = Math.Max(0, InhaltRaster.ActualHeight - VerfuegbarKarte.ActualHeight);
            double ziel = Math.Clamp(MitlaufAbstand - (startImInhalt - (naechsterOffset ?? offset)), 0, spielraum);

            VerfuegbarVerschiebung.Y = ziel;
            if (_mitlaufWerte is not null)
            {
                _mitlaufWerte.InsertScalar("Start", (float)startImInhalt);
                _mitlaufWerte.InsertScalar("Max", (float)spielraum);
                _mitlaufWerte.InsertScalar("Ruhe", (float)ziel);
            }
        }

        private DateOnly GewaehlterTag =>
            DatumAuswahl.Date is DateTimeOffset datum ? DateOnly.FromDateTime(datum.Date) : DateOnly.FromDateTime(DateTime.Now);

        // ---- Knöpfe und Menüs ----

        private void SendungErfassen_Click(object sender, RoutedEventArgs e) => _kontext.Oeffnen("D03");

        private void Details_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: long id })
                _kontext.Oeffnen("D03", id);
        }

        // Öffnet D16: freie Fahrer und Fahrzeuge für genau diese Sendung in der großen Ansicht.
        private void Zuweisen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: long id })
                _kontext.Oeffnen("D16", id);
        }

        private async void FilterAnwenden_Click(object sender, RoutedEventArgs e) => await AnwendenAsync();

        private async void SuchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await AnwendenAsync();
            }
        }

        private void Auswahl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { Tag: long id } box)
                return;
            if (box.IsChecked == true)
                _ausgewaehlt.Add(id);
            else
                _ausgewaehlt.Remove(id);
            ListenZeile.MarkierenFuer(box, box.IsChecked == true);
            AuswahlAnzeigen();
        }

        // D13 ist noch ein Platzhalter. Die Ids werden trotzdem schon mitgegeben.
        private void Planungsvorschlag_Click(object sender, RoutedEventArgs e) =>
            _kontext.Oeffnen("D13", AuswahlIds());

        // D05 startet mit dem gewählten Tag und hakt die Sendungen dort schon an.
        private void ManuellVerplanen_Click(object sender, RoutedEventArgs e) =>
            _kontext.Oeffnen("D05", new TourPlanenStart(GewaehlterTag, AuswahlIds()));

        private IReadOnlyList<long> AuswahlIds() =>
            _sendungen.Where(s => _ausgewaehlt.Contains(s.Id)).Select(s => s.Id).ToArray();

        // ---- Laden und Filtern ----

        private async Task AnwendenAsync()
        {
            DateOnly tag = GewaehlterTag;
            if (_geladenerTag != tag)
                await LadenAsync(tag);
            else
                ListeFiltern();
        }

        private async Task LadenAsync(DateOnly tag)
        {
            if (_kontext.IstVorschau)
            {
                UnverplantText.Text = "Vorschau ohne Anmeldung";
                StatusText.Text = "In der Vorschau ohne Anmeldung werden keine Sendungen geladen.";
                return;
            }

            _laden?.Cancel();
            var abbruch = new CancellationTokenSource();
            _laden = abbruch;
            LadeAnzeige.IsActive = true;
            LadeAnzeige.Visibility = Visibility.Visible;
            StatusText.Text = "Sendungen werden geladen …";
            try
            {
                IReadOnlyList<SendungUebersicht> sendungen = await _repository.TagLadenAsync(tag, abbruch.Token);
                if (abbruch.IsCancellationRequested)
                    return;

                // Ein anderer Tag: Vormerkungen und Häkchen des alten Tages gelten nicht mehr.
                if (_geladenerTag != tag)
                {
                    _vorgemerkt.Clear();
                    _tourZustand.Clear();
                }
                _sendungen = sendungen;
                _geladenerTag = tag;
                _ausgewaehlt.Clear();
                StatusListeAufbauen();
                ListeFiltern();
                // Danach die Spalte „Verfügbar am …“ (fängt eigene Fehler ab).
                await VerfuegbarkeitLadenAsync(tag, abbruch.Token);
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
                // Keine technischen Details anzeigen. Die zuletzt geladene Liste bleibt sichtbar.
                StatusText.Text = "Die Sendungen konnten nicht geladen werden. Bitte prüfen Sie die Verbindung und versuchen Sie es erneut.";
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

        // Die Spalte „Verfügbar am …“. Ein Fehler hier stört die Sendungsliste nicht.
        private async Task VerfuegbarkeitLadenAsync(DateOnly tag, CancellationToken abbruch)
        {
            VerfuegbarTitel.Text = $"Verfügbar am {tag.ToString("dd.MM.", CultureInfo.GetCultureInfo("de-DE"))}";
            try
            {
                TagesVerfuegbarkeit daten = await _tourRepository.TagesVerfuegbarkeitLadenAsync(tag, abbruch);
                if (abbruch.IsCancellationRequested)
                    return;
                var fahrer = daten.Fahrer.Select(f => new VerfuegbarZeile(f)).ToList();
                var fahrzeuge = daten.Fahrzeuge.Select(f => new VerfuegbarZeile(f)).ToList();
                FahrerListe.ItemsSource = fahrer;
                FahrzeugListe.ItemsSource = fahrzeuge;
                FahrerTitel.Text = $"Fahrer ({fahrer.Count(z => z.GanzFrei)} frei von {fahrer.Count})";
                FahrzeugTitel.Text = $"Fahrzeuge ({fahrzeuge.Count(z => z.GanzFrei)} frei von {fahrzeuge.Count})";
                FahrerLeerText.Visibility = fahrer.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                FahrzeugLeerText.Visibility = fahrzeuge.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
                FahrerListe.ItemsSource = null;
                FahrzeugListe.ItemsSource = null;
                FahrerTitel.Text = "Fahrer und Fahrzeuge konnten nicht geladen werden. „Filter anwenden“ versucht es erneut.";
            }
        }

        private void ListeFiltern()
        {
            string suche = SuchBox.Text.Trim();
            string status = StatusAuswahl.SelectedItem as string ?? AlleStatus;

            List<SendungUebersicht> treffer = _sendungen
                .Where(s => status == AlleStatus
                            || (status == NurUnverplant && !s.IstVerplant)
                            || string.Equals(s.Status, status, StringComparison.CurrentCultureIgnoreCase))
                .Where(s => suche.Length == 0 || Suchtexte(s).Any(text =>
                    text?.Contains(suche, StringComparison.CurrentCultureIgnoreCase) == true))
                .ToList();

            // Oben: noch zu verplanen. Unten: verplant, nach Tour gruppiert.
            var offen = treffer.Where(s => !s.IstVerplant)
                .Select(s => new SendungKarte(s, _ausgewaehlt.Contains(s.Id), _vorgemerkt.GetValueOrDefault(s.Id), DetailsFuer(s)))
                .ToList();
            var touren = treffer.Where(s => s.IstVerplant && s.TourId is not null)
                .GroupBy(s => s.TourId!.Value)
                .Select(g => new TourGruppe(g.ToList(), _tourZustand.GetValueOrDefault(g.Key), DetailsFuer))
                .OrderBy(t => t.Titel, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            OffenListe.ItemsSource = offen;
            TourListe.ItemsSource = touren;
            VormerkungAnzeigen();

            int unverplant = _sendungen.Count(s => !s.IstVerplant);
            UnverplantText.Text = unverplant == 1 ? "1 unverplante Sendung" : $"{unverplant} unverplante Sendungen";
            // Offene Sendungen sollen nicht übersehen werden: Zahl in der Farbe für „offen“.
            UnverplantText.Foreground = unverplant > 0
                ? StatusFarbe.Fuer(StatusFarbe.Offen).Schrift
                : (Brush)Application.Current.Resources["TextBrush"];

            OffenTitel.Text = $"Noch zu verplanen ({offen.Count})";
            VerplantTitel.Text = $"Verplant ({treffer.Count(s => s.IstVerplant)} Sendungen in {touren.Count} Touren)";
            string anzahl = treffer.Count == _sendungen.Count
                ? (_sendungen.Count == 1 ? "1 Sendung" : $"{_sendungen.Count} Sendungen")
                : $"{treffer.Count} von {_sendungen.Count} Sendungen";
            StatusText.Text = _meldung is null ? anzahl : $"{_meldung} · {anzahl}";
            _meldung = null;

            OffenLeerText.Text = _sendungen.Count == 0
                ? "Für diesen Tag gibt es keine Sendungen. Mit „+ Sendung erfassen“ legen Sie eine an."
                : _sendungen.Any(s => !s.IstVerplant) ? "Keine offene Sendung passt zum Filter." : "Alle Sendungen dieses Tages sind verplant.";
            OffenLeerText.Visibility = offen.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            VerplantLeerText.Text = "Noch keine verplante Sendung an diesem Tag.";
            VerplantLeerText.Visibility = touren.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AuswahlAnzeigen();
        }

        private void AuswahlAnzeigen()
        {
            int anzahl = _ausgewaehlt.Count;
            VorschlagButton.IsEnabled = ManuellButton.IsEnabled = anzahl > 0;
            AuswahlText.Text = anzahl switch
            {
                0 => "Haken Sie Sendungen an, um sie zu einer Tour zusammenzustellen.",
                1 => "1 Sendung ausgewählt.",
                _ => $"{anzahl} Sendungen ausgewählt."
            };
        }

        // „Alle“, „Nur unverplante“ und jeder Status, der an diesem Tag vorkommt.
        private void StatusListeAufbauen()
        {
            string? vorher = StatusAuswahl.SelectedItem as string;
            var eintraege = new List<string> { AlleStatus, NurUnverplant };
            eintraege.AddRange(_sendungen.Select(s => s.Status).Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(text => text, StringComparer.CurrentCultureIgnoreCase));

            StatusAuswahl.ItemsSource = eintraege;
            StatusAuswahl.SelectedItem = vorher is not null && eintraege.Contains(vorher) ? vorher : AlleStatus;
        }

        private static IEnumerable<string?> Suchtexte(SendungUebersicht s) => new[]
        {
            s.Kundenreferenz, s.Auftragsnummer, s.Kunde, s.Abholadresse, s.Lieferadresse, s.Gutart, s.Tournummer,
            s.Fahrer, s.Kennzeichen
        };

        // ---- Aufklappen: Details und Strecke ----

        private SendungDetailsAnzeige DetailsFuer(SendungUebersicht s) =>
            new(_aufgeklappt == s.Id, _details.GetValueOrDefault(s.Id), _detailFehler.GetValueOrDefault(s.Id));

        // Pfeil: auf- bzw. zuklappen. Die Details werden beim ersten Aufklappen geladen.
        private void Aufklappen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: long id })
                return;
            _aufgeklappt = _aufgeklappt == id ? null : id;
            if (_aufgeklappt == id && !_details.ContainsKey(id))
            {
                _detailFehler.Remove(id);
                _ = DetailsLadenAsync(id);
            }
            ListeFiltern();
        }

        private async Task DetailsLadenAsync(long id)
        {
            try
            {
                SendungFormularDaten daten = await _repository.FormularLadenAsync(id);
                if (daten.Vorhanden is { } sendung)
                    _details[id] = sendung;
                else
                    _detailFehler[id] = "Diese Sendung gibt es nicht (mehr).";
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
                return;
            }
            catch (ArgumentException fehler)
            {
                _detailFehler[id] = fehler.Message;
            }
            catch (Exception)
            {
                _detailFehler[id] = "Die Details konnten nicht geladen werden. Bitte prüfen Sie die Verbindung.";
            }
            if (_aufgeklappt == id)
                ListeFiltern();
        }

        // Kartenausschnitt erst erzeugen, wenn die aufgeklappte Karte sichtbar wird. Leaflet + OpenStreetMap wie D08,
        // Adressen und Lkw-Route berechnet die Seite über TomTom (gleicher Schlüssel wie die Live-Karte).
        private async void Strecke_Loaded(object sender, RoutedEventArgs e)
        {
            // Loaded kommt auch bei zugeklappten (unsichtbaren) Karten: nur die aufgeklappte bekommt eine Karte.
            if (sender is not Border { Tag: long id } rahmen || rahmen.Child is not null || _aufgeklappt != id ||
                !_details.TryGetValue(id, out SendungEntwurf? sendung))
                return;

            var web = new WebView2();
            rahmen.Child = web;
            try
            {
                string ordner = Path.Combine(Path.GetTempPath(), "Disposio", "Strecke");
                if (!_streckeGeschrieben)
                {
                    Directory.CreateDirectory(ordner);
                    await File.WriteAllTextAsync(Path.Combine(ordner, "index.html"), StreckeHtml);
                    _streckeGeschrieben = true;
                }
                await web.EnsureCoreWebView2Async();
                web.CoreWebView2.SetVirtualHostNameToFolderMapping(StreckeHost, ordner, CoreWebView2HostResourceAccessKind.Deny);
                web.CoreWebView2.WebMessageReceived += async (_, args) =>
                {
                    try
                    {
                        string text = args.TryGetWebMessageAsString();
                        if (text.Contains("\"bereit\"", StringComparison.Ordinal))
                        {
                            await web.ExecuteScriptAsync(_strecken.TryGetValue(id, out string? strecke)
                                ? $"zeigeRoute({strecke})"
                                : $"berechne({JsonSerializer.Serialize(sendung.Abholadresse)}, {JsonSerializer.Serialize(sendung.Lieferadresse)})");
                        }
                        else if (text.StartsWith("{\"von\"", StringComparison.Ordinal))
                        {
                            _strecken[id] = text;
                        }
                    }
                    catch (Exception)
                    {
                        // Die Karte wurde gerade geschlossen.
                    }
                };

                string adresse = $"https://{StreckeHost}/index.html";
                if (TomTomKonfiguration.SchluesselLesen() is { } schluessel)
                    adresse += $"?key={Uri.EscapeDataString(schluessel)}";
                web.Source = new Uri(adresse);
            }
            catch (Exception)
            {
                rahmen.Child = new TextBlock
                {
                    Text = "Die Karte konnte nicht geöffnet werden. Bitte prüfen Sie, ob die Microsoft-Edge-WebView2-Runtime installiert ist.",
                    Style = (Style)Application.Current.Resources["HinweisText"],
                    Margin = new Thickness(14),
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
        }

        // Beim Zuklappen bzw. Neuaufbau den Browser schließen, damit er keinen Speicher belegt.
        private void Strecke_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Border { Child: WebView2 web } rahmen)
            {
                web.Close();
                rahmen.Child = null;
            }
        }

        // Kleine Kartenseite: A = Abholung (grün), B = Zustellung (orange), blaue Linie = Lkw-Route ohne Verkehr.
        // Ohne Route (z. B. Adresse nicht gefunden) gestrichelte Luftlinie. Unten links Entfernung und Fahrzeit.
        private const string StreckeHtml = """
            <!DOCTYPE html>
            <html lang="de">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css" />
                <style>
                    html, body, #karte { height: 100%; margin: 0; padding: 0; }
                    .info { position: absolute; left: 8px; bottom: 8px; z-index: 1000; background: rgba(255,255,255,.94);
                            border-radius: 6px; padding: 4px 9px; font: 12px/1.35 "Segoe UI", sans-serif; color: #1a1f2a;
                            box-shadow: 0 1px 3px rgba(0,0,0,.3); }
                    .punkt { width: 22px; height: 22px; border-radius: 50%; color: #fff; font: 600 12px/22px "Segoe UI", sans-serif;
                             text-align: center; border: 2px solid #fff; box-shadow: 0 1px 3px rgba(0,0,0,.4); }
                </style>
            </head>
            <body>
                <div id="karte"></div>
                <div id="info" class="info">Strecke wird berechnet …</div>
                <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
                <script>
                    const key = new URLSearchParams(location.search).get("key");
                    const info = document.getElementById("info");
                    const karte = L.map("karte", { zoomControl: false }).setView([49.45, 11.08], 9);
                    L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png",
                        { maxZoom: 18, attribution: "© OpenStreetMap" }).addTo(karte);
                    const ebene = L.layerGroup().addTo(karte);

                    function melden(daten) {
                        if (window.chrome && chrome.webview) chrome.webview.postMessage(JSON.stringify(daten));
                    }
                    function punkt(text, farbe) {
                        return L.divIcon({ className: "", html: `<div class="punkt" style="background:${farbe}">${text}</div>`,
                                           iconSize: [26, 26], iconAnchor: [13, 13] });
                    }
                    function dauer(minuten) {
                        const h = Math.floor(minuten / 60), m = Math.round(minuten % 60);
                        return h > 0 ? `${h} Std. ${m} Min.` : `${m} Min.`;
                    }

                    window.zeigeRoute = d => {
                        ebene.clearLayers();
                        const mitRoute = d.punkte && d.punkte.length > 1;
                        L.polyline(mitRoute ? d.punkte : [d.von, d.nach],
                            { color: "#2F7BEB", weight: 4, opacity: 0.85, dashArray: mitRoute ? null : "6 6" }).addTo(ebene);
                        L.marker(d.von, { icon: punkt("A", "#1F9D55"), title: "Abholung" }).addTo(ebene);
                        L.marker(d.nach, { icon: punkt("B", "#E08A00"), title: "Zustellung" }).addTo(ebene);
                        karte.fitBounds(L.latLngBounds([d.von, d.nach].concat(mitRoute ? d.punkte : [])), { padding: [26, 26] });
                        info.textContent = d.km != null
                            ? `${Math.round(d.km).toLocaleString("de-DE")} km · ca. ${dauer(d.min)} (Lkw, ohne Verkehr)`
                            : "Luftlinie – Route nicht verfügbar";
                    };

                    async function adresseSuchen(text) {
                        const antwort = await fetch(`https://api.tomtom.com/search/2/geocode/${encodeURIComponent(text)}.json`
                            + `?key=${key}&limit=1&language=de-DE`);
                        if (!antwort.ok) throw new Error("geocode");
                        const daten = await antwort.json();
                        const p = daten.results && daten.results[0] && daten.results[0].position;
                        if (!p) throw new Error("nicht gefunden");
                        return [p.lat, p.lon];
                    }

                    window.berechne = async (von, nach) => {
                        if (!key) { info.textContent = "Kein TomTom-Schlüssel – Strecke kann nicht berechnet werden."; return; }
                        let a, b;
                        try { [a, b] = await Promise.all([adresseSuchen(von), adresseSuchen(nach)]); }
                        catch { info.textContent = "Adresse auf der Karte nicht gefunden."; return; }
                        const d = { von: a, nach: b, punkte: [], km: null, min: null };
                        try {
                            const antwort = await fetch(`https://api.tomtom.com/routing/1/calculateRoute/${a[0]},${a[1]}:${b[0]},${b[1]}/json`
                                + `?key=${key}&travelMode=truck&traffic=false`);
                            if (antwort.ok) {
                                const route = (await antwort.json()).routes[0];
                                let punkte = route.legs.flatMap(leg => leg.points.map(p => [p.latitude, p.longitude]));
                                // Ausdünnen, damit die gespeicherte Strecke klein bleibt.
                                if (punkte.length > 400) {
                                    const schritt = Math.ceil(punkte.length / 400);
                                    punkte = punkte.filter((_, i) => i % schritt === 0 || i === punkte.length - 1);
                                }
                                d.punkte = punkte;
                                d.km = route.summary.lengthInMeters / 1000;
                                d.min = route.summary.travelTimeInSeconds / 60;
                            }
                        } catch { }
                        zeigeRoute(d);
                        melden(d);
                    };

                    melden({ bereit: true });
                </script>
            </body>
            </html>
            """;

        // ---- Drag & Drop aus „Verfügbar am …“ ----

        private void Verfuegbar_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            _gezogen = e.Items.FirstOrDefault() as VerfuegbarZeile;
            if (_gezogen is null)
            {
                e.Cancel = true;
                return;
            }
            // Der Text im Datenpaket ist nur für Windows. Gearbeitet wird mit _gezogen.
            e.Data.SetText(_gezogen.Titel);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        }

        private void Verfuegbar_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => _gezogen = null;

        private static void Anzeige(DragEventArgs e, string? grund, string text)
        {
            e.AcceptedOperation = grund is null ? DataPackageOperation.Copy : DataPackageOperation.None;
            if (e.DragUIOverride is { } anzeige)
            {
                anzeige.Caption = grund ?? text;
                anzeige.IsCaptionVisible = true;
                anzeige.IsGlyphVisible = false;
            }
        }

        // Ein Fahrzeug ohne Kühlaufbau passt nicht zu Frische.
        private static string? KuehlungPasstNicht(VerfuegbarZeile gezogen, IEnumerable<SendungUebersicht> sendungen) =>
            gezogen.Aufbau is { } aufbau && aufbau != FahrzeugWerte.Kuehlaufbau &&
            sendungen.Any(s => s.Gutart == SendungWerte.GutartFrische)
                ? "Kein Kühlaufbau – passt nicht zu Frische"
                : null;

        // Offene Sendung (Karte)
        private void Zeile_DragOver(object sender, DragEventArgs e)
        {
            if (sender is not Border { Tag: long id } zeile || _gezogen is not { } gezogen)
                return;
            SendungUebersicht? sendung = _sendungen.FirstOrDefault(s => s.Id == id);
            string? grund = sendung is null ? "Sendung nicht gefunden"
                : _vorgemerkt.TryGetValue(id, out Vormerkung? v) && v.Speichert ? "Wird gerade zugewiesen …"
                : KuehlungPasstNicht(gezogen, new[] { sendung });
            Anzeige(e, grund, $"{gezogen.Kurzname} für {sendung?.Referenz} vormerken");
            ListenZeile.SetMarkiert(zeile, grund is null || _ausgewaehlt.Contains(id));
        }

        private void Zeile_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border { Tag: long id } zeile)
                ListenZeile.SetMarkiert(zeile, _ausgewaehlt.Contains(id));
        }

        // Ablegen: Fahrer bzw. Fahrzeug bei der Sendung vormerken. Sind beide da, sofort zuweisen.
        private void Zeile_Drop(object sender, DragEventArgs e)
        {
            if (sender is not Border { Tag: long id } zeile || _gezogen is not { } gezogen)
                return;
            ListenZeile.SetMarkiert(zeile, _ausgewaehlt.Contains(id));
            _gezogen = null;
            if (_sendungen.FirstOrDefault(s => s.Id == id) is not { IstVerplant: false })
                return;

            Vormerkung alt = _vorgemerkt.GetValueOrDefault(id) ?? new Vormerkung(null, null, null, false);
            if (alt.Speichert)
                return;
            Vormerkung neu = gezogen.IstFahrer
                ? alt with { Fahrer = gezogen, Fehler = null }
                : alt with { Fahrzeug = gezogen, Fehler = null };
            _vorgemerkt[id] = neu;
            ListeFiltern();
        }

        // Kopf einer Tour: Fahrer bzw. Fahrzeug der Tour tauschen.
        private void Tour_DragOver(object sender, DragEventArgs e)
        {
            if (sender is not Border { Tag: long tourId } kopf || _gezogen is not { } gezogen)
                return;
            var sendungen = _sendungen.Where(s => s.TourId == tourId).ToList();
            string? grund = sendungen.Count == 0 ? "Tour nicht gefunden"
                : !sendungen[0].TourBearbeitbar ? "Tour ist schon unterwegs oder abgeschlossen"
                : _tourZustand.TryGetValue(tourId, out Vormerkung? z) && z.Speichert ? "Wird gerade gespeichert …"
                : KuehlungPasstNicht(gezogen, sendungen);
            Anzeige(e, grund, $"{gezogen.Kurzname} in Tour {sendungen.FirstOrDefault()?.Tournummer} eintragen");
            kopf.Background = grund is null ? StatusFarbe.Hervorhebung() : (Brush)Application.Current.Resources["InputBrush"];
        }

        private void Tour_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border kopf)
                kopf.Background = (Brush)Application.Current.Resources["InputBrush"];
        }

        private void Tour_Drop(object sender, DragEventArgs e)
        {
            if (sender is not Border { Tag: long tourId } kopf || _gezogen is not { } gezogen)
                return;
            kopf.Background = (Brush)Application.Current.Resources["InputBrush"];
            _gezogen = null;
            if (_sendungen.FirstOrDefault(s => s.TourId == tourId) is not { TourBearbeitbar: true } ||
                (_tourZustand.TryGetValue(tourId, out Vormerkung? z) && z.Speichert))
                return;
            _ = TourAendernAsync(tourId, async () =>
            {
                string tour = await _tourRepository.RessourceAendernAsync(tourId, gezogen.IstFahrer, gezogen.Id);
                return $"{gezogen.Kurzname} in Tour {tour} eingetragen";
            });
        }

        // ---- Zuweisen, entfernen, lösen ----

        // Leiste über den offenen Sendungen: wie viele Zuweisungen bereit bzw. noch unvollständig sind.
        private void VormerkungAnzeigen()
        {
            int bereit = _vorgemerkt.Values.Count(v => v.Fahrer is not null && v.Fahrzeug is not null);
            int halb = _vorgemerkt.Count - bereit;
            bool speichert = _vorgemerkt.Values.Any(v => v.Speichert);
            VormerkungLeiste.Visibility = _vorgemerkt.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var teile = new List<string>();
            if (bereit > 0)
                teile.Add(bereit == 1 ? "1 Zuweisung bereit zum Speichern" : $"{bereit} Zuweisungen bereit zum Speichern");
            if (halb > 0)
                teile.Add(halb == 1 ? "1 Sendung noch unvollständig" : $"{halb} Sendungen noch unvollständig");
            VormerkungText.Text = speichert ? "Zuweisungen werden gespeichert …" : string.Join(" · ", teile);
            SpeichernButton.Content = bereit > 1 ? $"{bereit} Zuweisungen speichern" : "Zuweisung speichern";
            SpeichernButton.IsEnabled = bereit > 0 && !speichert;
            VerwerfenButton.IsEnabled = !speichert;
        }

        private void Verwerfen_Click(object sender, RoutedEventArgs e)
        {
            if (_vorgemerkt.Values.Any(v => v.Speichert))
                return;
            _vorgemerkt.Clear();
            ListeFiltern();
        }

        // Legt für jede vollständig vorgemerkte Sendung eine Tour an (gleiche Regeln wie D16), nacheinander.
        // Was nicht klappt, bleibt vorgemerkt und zeigt den Grund rot an der Karte. Danach wird alles neu geladen.
        private async void ZuweisungenSpeichern_Click(object sender, RoutedEventArgs e)
        {
            var bereit = _vorgemerkt
                .Where(p => p.Value is { Fahrer: not null, Fahrzeug: not null, Speichert: false })
                .Select(p => p.Key)
                .ToList();
            if (bereit.Count == 0)
                return;

            foreach (long id in bereit)
                _vorgemerkt[id] = _vorgemerkt[id] with { Speichert = true, Fehler = null };
            ListeFiltern();

            var angelegt = new List<string>();
            int fehler = 0;
            foreach (long id in bereit)
            {
                Vormerkung v = _vorgemerkt[id];
                try
                {
                    string tour = await _tourRepository.SendungDirektZuweisenAsync(id, v.Fahrer!.Id, v.Fahrzeug!.Id);
                    _vorgemerkt.Remove(id);
                    angelegt.Add(tour);
                }
                catch (UnauthorizedAccessException)
                {
                    _kontext.ZugriffBeenden();
                    return;
                }
                catch (ArgumentException ausnahme)
                {
                    // Z. B. Fahrer im Zeitraum belegt (auch durch eine eben gespeicherte Zuweisung), Fahrzeug zu klein.
                    _vorgemerkt[id] = v with { Speichert = false, Fehler = ausnahme.Message };
                    fehler++;
                }
                catch (Exception)
                {
                    _vorgemerkt[id] = v with
                    {
                        Speichert = false,
                        Fehler = "Die Zuweisung konnte nicht gespeichert werden. Bitte prüfen Sie die Verbindung."
                    };
                    fehler++;
                }
            }

            string ergebnis = angelegt.Count switch
            {
                0 => "Keine Tour angelegt",
                1 => $"Tour {angelegt[0]} angelegt",
                _ => $"{angelegt.Count} Touren angelegt ({string.Join(", ", angelegt)})"
            };
            if (fehler > 0)
                ergebnis += fehler == 1 ? " – 1 Zuweisung nicht möglich, siehe rote Hinweise" : $" – {fehler} Zuweisungen nicht möglich, siehe rote Hinweise";
            _meldung = ergebnis;
            await LadenAsync(GewaehlterTag);
        }

        private void FahrerEntfernen_Click(object sender, RoutedEventArgs e) => Entfernen(sender, fahrer: true);

        private void FahrzeugEntfernen_Click(object sender, RoutedEventArgs e) => Entfernen(sender, fahrer: false);

        // ✕ an einem Feld. Schlüssel „S:<Auftrag>“ = Vormerkung einer offenen Sendung, „T:<Tour>“ = Tour.
        private void Entfernen(object sender, bool fahrer)
        {
            if (sender is not Button { Tag: string schluessel } || schluessel.Length < 3 ||
                !long.TryParse(schluessel[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                return;

            if (schluessel.StartsWith("T:", StringComparison.Ordinal))
            {
                // Aus der Tour entfernen. Die Tour bleibt, ist danach aber „Geplant“.
                if (_tourZustand.TryGetValue(id, out Vormerkung? z) && z.Speichert)
                    return;
                _ = TourAendernAsync(id, async () =>
                {
                    string tour = await _tourRepository.RessourceAendernAsync(id, fahrer, null);
                    return $"{(fahrer ? "Fahrer" : "Fahrzeug")} aus Tour {tour} entfernt – die Tour ist jetzt „Geplant“";
                });
                return;
            }

            if (!_vorgemerkt.TryGetValue(id, out Vormerkung? v) || v.Speichert)
                return;
            Vormerkung neu = fahrer ? v with { Fahrer = null, Fehler = null } : v with { Fahrzeug = null, Fehler = null };
            if (neu.Fahrer is null && neu.Fahrzeug is null)
                _vorgemerkt.Remove(id);
            else
                _vorgemerkt[id] = neu;
            ListeFiltern();
        }

        // „⋯“ → „Aus Tour nehmen …“ mit Rückfrage. Eine danach leere Tour wird aufgelöst.
        private async void Loesen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: long id } ||
                _sendungen.FirstOrDefault(s => s.Id == id) is not { IstVerplant: true, TourId: long tourId } sendung)
                return;

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Aus Tour nehmen?",
                Content = $"Sendung {sendung.Referenz} aus Tour {sendung.Tournummer} nehmen? Ihre Stopps werden entfernt. " +
                          "Ist die Tour danach leer, wird sie aufgelöst.",
                PrimaryButtonText = "Aus Tour nehmen",
                CloseButtonText = "Abbrechen",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            _ = TourAendernAsync(tourId, async () =>
            {
                var (tour, aufgeloest) = await _tourRepository.SendungAusTourNehmenAsync(id);
                return aufgeloest
                    ? $"{sendung.Referenz} aus Tour {tour} genommen – die leere Tour wurde aufgelöst"
                    : $"{sendung.Referenz} aus Tour {tour} genommen";
            });
        }

        // Führt eine Änderung an einer Tour aus: Kopf zeigt „Wird gespeichert …“, danach Liste und „Verfügbar am …“
        // neu laden. Fehler erscheinen rot im Kopf der Tour.
        private async Task TourAendernAsync(long tourId, Func<Task<string>> aktion)
        {
            _tourZustand[tourId] = new Vormerkung(null, null, null, true);
            ListeFiltern();
            try
            {
                string meldung = await aktion();
                _tourZustand.Remove(tourId);
                _meldung = meldung;
                await LadenAsync(GewaehlterTag);
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (ArgumentException fehler)
            {
                _tourZustand[tourId] = new Vormerkung(null, null, fehler.Message, false);
                ListeFiltern();
            }
            catch (Exception)
            {
                _tourZustand[tourId] = new Vormerkung(null, null,
                    "Die Änderung konnte nicht gespeichert werden. Bitte prüfen Sie die Verbindung.", false);
                ListeFiltern();
            }
        }
    }

    /// <summary>
    /// Farben für Status-Pillen: Hintergrund leicht getönt, Rand und Schrift kräftig. Funktioniert hell und dunkel.
    /// Offen (orange) = noch etwas zu tun, Info (blau) = läuft, Erfolg (grün) = erledigt, Fehler (rot), Neutral (grau).
    /// </summary>
    public static class StatusFarbe
    {
        public const string Offen = "offen";
        public const string Info = "info";
        public const string Erfolg = "erfolg";
        public const string Fehler = "fehler";
        public const string Neutral = "neutral";

        public static (Brush Hintergrund, Brush Rand, Brush Schrift) Fuer(string art)
        {
            Color farbe = art switch
            {
                Offen => Microsoft.UI.ColorHelper.FromArgb(255, 0xE0, 0x8A, 0x00),
                Info => Microsoft.UI.ColorHelper.FromArgb(255, 0x2F, 0x7B, 0xEB),
                Erfolg => Microsoft.UI.ColorHelper.FromArgb(255, 0x1F, 0x9D, 0x55),
                Fehler => Ressource("ErrorBrush"),
                _ => Ressource("MutedTextBrush")
            };
            return (new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x26, farbe.R, farbe.G, farbe.B)),
                    new SolidColorBrush(farbe),
                    new SolidColorBrush(farbe));
        }

        /// <summary>Welche Farbe passt zu einem Status-Text der Datenbank?</summary>
        public static string ArtVon(string? status)
        {
            string s = (status ?? "").ToLowerInvariant();
            if (s.Contains("zugestellt") || s.Contains("abgeschlossen") || s.Contains("erledigt") || s.Contains("geliefert"))
                return Erfolg;
            if (s.Contains("storniert") || s.Contains("beendet"))
                return Neutral;
            if (s.Contains("problem") || s.Contains("fehl") || s.Contains("verspät") || s.Contains("schaden") ||
                s.Contains("abgelehnt") || s.Contains("nicht"))
                return Fehler;
            if (s.Contains("unterwegs") || s.Contains("zugewiesen") || s.Contains("geladen") || s.Contains("abgeholt"))
                return Info;
            if (s.Contains("offen") || s.Contains("geplant"))
                return Offen;
            return Neutral;
        }

        /// <summary>Leichte Akzent-Tönung beim Darüberziehen.</summary>
        public static Brush Hervorhebung()
        {
            Color akzent = Ressource("AccentBrush");
            return new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x30, akzent.R, akzent.G, akzent.B));
        }

        private static Color Ressource(string schluessel) =>
            Application.Current.Resources[schluessel] is SolidColorBrush brush ? brush.Color : Microsoft.UI.Colors.Gray;
    }

    /// <summary>
    /// Die beiden Felder „Fahrer“ und „Fahrzeug“ samt Hinweis darunter. Leer: gestrichelt und grau,
    /// gesetzt: durchgezogen und leicht hinterlegt. Gemeinsam für offene Sendungen und Touren.
    /// </summary>
    public abstract class ZuweisungFelder
    {
        protected ZuweisungFelder(string schluessel, string? fahrer, string? fahrzeug, bool entfernbar, Vormerkung? zustand,
                                  string? hinweisOhneFehler)
        {
            Schluessel = schluessel;
            Brush R(string name) => (Brush)Application.Current.Resources[name];
            Brush text = R("TextBrush"), grau = R("MutedTextBrush"), rand = R("BorderBrush"), flaeche = R("InputBrush");
            bool speichert = zustand?.Speichert == true;

            FahrerText = fahrer ?? "+ Fahrer";
            FahrzeugText = fahrzeug ?? "+ Fahrzeug";
            FahrerFarbe = fahrer is null ? grau : text;
            FahrzeugFarbe = fahrzeug is null ? grau : text;
            FahrerRand = rand;
            FahrzeugRand = rand;
            FahrerStrich = fahrer is null ? new DoubleCollection { 4, 3 } : new DoubleCollection();
            FahrzeugStrich = fahrzeug is null ? new DoubleCollection { 4, 3 } : new DoubleCollection();
            FahrerFuellung = fahrer is null ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : flaeche;
            FahrzeugFuellung = fahrzeug is null ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : flaeche;
            FahrerEntfernenSichtbar = entfernbar && !speichert && fahrer is not null ? Visibility.Visible : Visibility.Collapsed;
            FahrzeugEntfernenSichtbar = entfernbar && !speichert && fahrzeug is not null ? Visibility.Visible : Visibility.Collapsed;

            Hinweis = speichert ? "Wird gespeichert …" : zustand?.Fehler ?? hinweisOhneFehler ?? "";
            HinweisFarbe = zustand?.Fehler is not null && !speichert ? R("ErrorBrush") : grau;
            HinweisSichtbar = Hinweis.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public string Schluessel { get; }
        public string FahrerText { get; }
        public string FahrzeugText { get; }
        public Brush FahrerFarbe { get; }
        public Brush FahrzeugFarbe { get; }
        public Brush FahrerRand { get; }
        public Brush FahrzeugRand { get; }
        public DoubleCollection FahrerStrich { get; }
        public DoubleCollection FahrzeugStrich { get; }
        public Brush FahrerFuellung { get; }
        public Brush FahrzeugFuellung { get; }
        public Visibility FahrerEntfernenSichtbar { get; }
        public Visibility FahrzeugEntfernenSichtbar { get; }
        public string Hinweis { get; }
        public Brush HinweisFarbe { get; }
        public Visibility HinweisSichtbar { get; }
    }

    /// <summary>Eine offene (unverplante) Sendung als zweizeilige Karte.</summary>
    public sealed class SendungKarte : ZuweisungFelder
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public SendungKarte(SendungUebersicht s, bool ausgewaehlt, Vormerkung? v, SendungDetailsAnzeige details)
            : base($"S:{s.Id.ToString(CultureInfo.InvariantCulture)}", v?.Fahrer?.Kurzname, v?.Fahrzeug?.Kurzname,
                   entfernbar: true, v,
                   v?.Fahrer is not null && v.Fahrzeug is not null ? "Bereit – oben „Zuweisungen speichern“"
                   : v?.Fahrer is not null && v.Fahrzeug is null ? "Noch ein Fahrzeug ziehen"
                   : v?.Fahrzeug is not null && v.Fahrer is null ? "Noch einen Fahrer ziehen"
                   : null)
        {
            Id = s.Id;
            Ausgewaehlt = ausgewaehlt;
            Details = details;
            Titel = string.IsNullOrWhiteSpace(s.Kunde) ? s.Referenz : $"{s.Referenz} · {s.Kunde}";
            Zeile1 = $"{Adressen.Ort(s.Abholadresse)} → {Adressen.Ort(s.Lieferadresse)} · Lieferung {Fenster(s)}";
            string plaetze = s.Anzahl == 1 ? "1 Platz" : $"{s.Anzahl} Plätze";
            Zeile2 = $"{s.Gutart} · {plaetze} / {s.Lademeter.ToString("0.0", Deutsch)} Ldm · Priorität {s.Prioritaet}";

            // Offene Sendungen fallen orange auf. Ein anderer Status der Sendung (z. B. „Storniert“) hat Vorrang.
            bool offen = string.IsNullOrWhiteSpace(s.Status) || string.Equals(s.Status, "Offen", StringComparison.OrdinalIgnoreCase);
            Status = offen ? "Nicht verplant" : s.Status;
            var farbe = StatusFarbe.Fuer(offen ? StatusFarbe.Offen : StatusFarbe.ArtVon(s.Status));
            StatusHintergrund = farbe.Hintergrund;
            StatusRand = farbe.Rand;
            StatusSchrift = farbe.Schrift;
        }

        internal static string Fenster(SendungUebersicht s) => s.Abholdatum == s.Lieferdatum
            ? $"{s.LieferfensterVon}–{s.LieferfensterBis}"
            : $"{s.Lieferdatum.ToString("dd.MM.", Deutsch)} {s.LieferfensterVon}–{s.LieferfensterBis}";

        public long Id { get; }
        public bool? Ausgewaehlt { get; }
        public SendungDetailsAnzeige Details { get; }
        // Angehakte Karten werden farbig hervorgehoben (ListenZeile.Markiert).
        public bool IstMarkiert => Ausgewaehlt == true;
        public string Titel { get; }
        public string Zeile1 { get; }
        public string Zeile2 { get; }
        public string Status { get; }
        public Brush StatusHintergrund { get; }
        public Brush StatusRand { get; }
        public Brush StatusSchrift { get; }
    }

    /// <summary>Eine Tour mit ihren Sendungen im Bereich „Verplant“. Fahrer und Fahrzeug gehören zur Tour.</summary>
    public sealed class TourGruppe : ZuweisungFelder
    {
        public TourGruppe(IReadOnlyList<SendungUebersicht> sendungen, Vormerkung? zustand,
                          Func<SendungUebersicht, SendungDetailsAnzeige> details)
            : base($"T:{sendungen[0].TourId!.Value.ToString(CultureInfo.InvariantCulture)}", sendungen[0].Fahrer,
                   sendungen[0].Kennzeichen, entfernbar: sendungen[0].TourBearbeitbar, zustand,
                   sendungen[0].TourBearbeitbar ? null : "Schon unterwegs – Änderungen nur über die Tourdetails.")
        {
            SendungUebersicht erste = sendungen[0];
            TourId = erste.TourId!.Value;
            Bearbeitbar = erste.TourBearbeitbar;
            Titel = $"Tour {erste.Tournummer}";
            Zeile1 = sendungen.Count == 1 ? "1 Sendung" : $"{sendungen.Count} Sendungen";

            // Geplant ohne Fahrer oder Fahrzeug: orange, damit die fehlende Zuweisung auffällt.
            string tourStatus = erste.TourStatus ?? "";
            bool fehlt = erste.Fahrer is null || erste.Kennzeichen is null;
            bool geplant = string.Equals(tourStatus, TourWerte.StatusGeplant, StringComparison.OrdinalIgnoreCase);
            Status = geplant && fehlt ? $"{tourStatus} · Zuweisung fehlt" : tourStatus;
            var farbe = StatusFarbe.Fuer(geplant ? StatusFarbe.Offen : StatusFarbe.ArtVon(tourStatus));
            StatusHintergrund = farbe.Hintergrund;
            StatusRand = farbe.Rand;
            StatusSchrift = farbe.Schrift;

            Sendungen = sendungen.Select(s => new VerplanteZeile(s, details(s))).ToList();
        }

        public long TourId { get; }
        public bool Bearbeitbar { get; }
        public string Titel { get; }
        public string Zeile1 { get; }
        public string Status { get; }
        public Brush StatusHintergrund { get; }
        public Brush StatusRand { get; }
        public Brush StatusSchrift { get; }
        public IReadOnlyList<VerplanteZeile> Sendungen { get; }
    }

    /// <summary>Eine Sendung innerhalb einer Tour. Status-Pille nur, wenn die Sendung selbst weiter ist als „Offen“.</summary>
    public sealed class VerplanteZeile
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public VerplanteZeile(SendungUebersicht s, SendungDetailsAnzeige details)
        {
            Id = s.Id;
            Details = details;
            Titel = string.IsNullOrWhiteSpace(s.Kunde) ? s.Referenz : $"{s.Referenz} · {s.Kunde}";
            string plaetze = s.Anzahl == 1 ? "1 Platz" : $"{s.Anzahl} Plätze";
            Zeile1 = $"{Adressen.Ort(s.Abholadresse)} → {Adressen.Ort(s.Lieferadresse)} · Lieferung {SendungKarte.Fenster(s)} · " +
                     $"{s.Gutart} · {plaetze} / {s.Lademeter.ToString("0.0", Deutsch)} Ldm";
            bool offen = string.IsNullOrWhiteSpace(s.Status) || string.Equals(s.Status, "Offen", StringComparison.OrdinalIgnoreCase);
            Status = s.Status;
            StatusSichtbar = offen ? Visibility.Collapsed : Visibility.Visible;
            var farbe = StatusFarbe.Fuer(StatusFarbe.ArtVon(s.Status));
            StatusHintergrund = farbe.Hintergrund;
            StatusRand = farbe.Rand;
            StatusSchrift = farbe.Schrift;
            LoesenSichtbar = s.TourBearbeitbar ? Visibility.Visible : Visibility.Collapsed;
        }

        public long Id { get; }
        public string Titel { get; }
        public string Zeile1 { get; }
        public string Status { get; }
        public Visibility StatusSichtbar { get; }
        public Brush StatusHintergrund { get; }
        public Brush StatusRand { get; }
        public Brush StatusSchrift { get; }
        public Visibility LoesenSichtbar { get; }
        public SendungDetailsAnzeige Details { get; }
    }

    /// <summary>Der aufklappbare Teil einer Sendung: Pfeil, Ladezustand und alle Angaben fertig formatiert.</summary>
    /// <summary>Eine farbige Pille (Merkmal, Avisierung, Priorität). Art = StatusFarbe.Offen/Info/…</summary>
    public sealed class DetailMerkmal
    {
        public DetailMerkmal(string text, string art)
        {
            Text = text;
            var farbe = StatusFarbe.Fuer(art);
            Hintergrund = farbe.Hintergrund;
            Rand = farbe.Rand;
            Schrift = farbe.Schrift;
        }

        public string Text { get; }
        public Brush Hintergrund { get; }
        public Brush Rand { get; }
        public Brush Schrift { get; }
    }

    public sealed class SendungDetailsAnzeige
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public SendungDetailsAnzeige(bool aufgeklappt, SendungEntwurf? d, string? fehler)
        {
            Sichtbar = aufgeklappt ? Visibility.Visible : Visibility.Collapsed;
            PfeilGlyph = aufgeklappt ? "\uE70E" : "\uE70D";   // Chevron hoch / runter
            PfeilTipp = aufgeklappt ? "Details zuklappen" : "Details und Strecke anzeigen";
            LadeText = fehler ?? "Details werden geladen …";
            LadeSichtbar = d is null ? Visibility.Visible : Visibility.Collapsed;
            InhaltSichtbar = d is null ? Visibility.Collapsed : Visibility.Visible;
            // Gleiche Farben wie die Punkte A und B im Kartenausschnitt.
            AbholFarbe = StatusFarbe.Fuer(StatusFarbe.Erfolg).Rand;
            ZustellFarbe = StatusFarbe.Fuer(StatusFarbe.Offen).Rand;
            Avisierung = new DetailMerkmal("Avisierung erforderlich", StatusFarbe.Offen);
            Prioritaet = new DetailMerkmal("Priorität –", StatusFarbe.Neutral);
            if (d is null)
                return;

            string Zeit(TimeOnly t) => t.ToString("HH:mm", Deutsch);
            AbholAdresse = d.Abholadresse;
            AbholZeit = $"{d.Abholdatum.ToString("ddd dd.MM.", Deutsch)} · {Zeit(d.AbholfensterVon)}–{Zeit(d.AbholfensterBis)}";
            ZustellAdresse = d.Lieferadresse;
            ZustellZeit = $"{d.Lieferdatum.ToString("ddd dd.MM.", Deutsch)} · {Zeit(d.LieferfensterVon)}–{Zeit(d.LieferfensterBis)}";

            decimal lademeter = Math.Round(d.Anzahl * d.LaengeM * d.BreiteM / 2.4m, 1);
            LadungZeile1 = $"{d.Gutart} · {d.Anzahl} × {d.Verpackungstyp} · {d.GewichtKg.ToString("N0", Deutsch)} kg · " +
                           $"{lademeter.ToString("0.0", Deutsch)} Lademeter";
            var zeile2 = new List<string>
            {
                $"je {d.LaengeM.ToString("0.##", Deutsch)} × {d.BreiteM.ToString("0.##", Deutsch)} × {d.HoeheM.ToString("0.##", Deutsch)} m (L × B × H)"
            };
            if (d.TemperaturMinC is decimal min && d.TemperaturMaxC is decimal max)
                zeile2.Add($"{min.ToString("0.#", Deutsch)} bis {max.ToString("0.#", Deutsch)} °C");
            LadungZeile2 = string.Join(" · ", zeile2);

            Merkmale = new (bool An, string Text, string Art)[]
            {
                (d.Gefahrgut, "Gefahrgut", StatusFarbe.Fehler),
                (d.Stapelbar, "stapelbar", StatusFarbe.Neutral),
                (d.Drehbar, "drehbar", StatusFarbe.Neutral),
                (d.Versicherung, "versichert", StatusFarbe.Info),
                (d.Zollrelevanz, "zollrelevant", StatusFarbe.Offen),
                (d.Ladehilfsmitteltausch, "Ladehilfsmitteltausch", StatusFarbe.Neutral)
            }.Where(m => m.An).Select(m => new DetailMerkmal(m.Text, m.Art)).ToList();
            MerkmaleSichtbar = Merkmale.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            VorOrt = string.Join(" · ", new[]
            {
                string.IsNullOrWhiteSpace(d.Ansprechpartner) ? null : d.Ansprechpartner,
                string.IsNullOrWhiteSpace(d.LadeEntladeequipment) ? null : d.LadeEntladeequipment
            }.Where(teil => teil is not null));
            if (VorOrt.Length == 0)
                VorOrt = "–";
            AvisierungSichtbar = d.AvisierungErforderlich ? Visibility.Visible : Visibility.Collapsed;

            Auftrag = string.Join(" · ", new[]
            {
                d.Auftragsnummer,
                string.IsNullOrWhiteSpace(d.Kundenreferenz) ? null : $"Ref. {d.Kundenreferenz}",
                $"Warenwert {d.Warenwert.ToString("N2", Deutsch)} €"
            }.Where(teil => teil is not null));
            string art = d.Prioritaet switch
            {
                "Express" => StatusFarbe.Fehler,
                "Hoch" => StatusFarbe.Offen,
                _ => StatusFarbe.Neutral
            };
            Prioritaet = new DetailMerkmal($"Priorität {d.Prioritaet}", art);
        }

        public Visibility Sichtbar { get; }
        public string PfeilGlyph { get; }
        public string PfeilTipp { get; }
        public string LadeText { get; }
        public Visibility LadeSichtbar { get; }
        public Visibility InhaltSichtbar { get; }

        public Brush AbholFarbe { get; }
        public Brush ZustellFarbe { get; }
        public string AbholAdresse { get; } = "";
        public string AbholZeit { get; } = "";
        public string ZustellAdresse { get; } = "";
        public string ZustellZeit { get; } = "";

        public string LadungZeile1 { get; } = "";
        public string LadungZeile2 { get; } = "";
        public IReadOnlyList<DetailMerkmal> Merkmale { get; } = Array.Empty<DetailMerkmal>();
        public Visibility MerkmaleSichtbar { get; } = Visibility.Collapsed;

        public string VorOrt { get; } = "";
        public DetailMerkmal Avisierung { get; }
        public Visibility AvisierungSichtbar { get; } = Visibility.Collapsed;

        public string Auftrag { get; } = "";
        public DetailMerkmal Prioritaet { get; }
    }

    /// <summary>
    /// Per Drag &amp; Drop bei einer offenen Sendung vorgemerkter Fahrer und/oder Fahrzeug – oder der Zustand einer Tour.
    /// Speichert = wird gerade gespeichert. Fehler = Grund, warum es nicht geklappt hat.
    /// </summary>
    public sealed record Vormerkung(VerfuegbarZeile? Fahrer, VerfuegbarZeile? Fahrzeug, string? Fehler, bool Speichert);

    // Ein Fahrer oder Fahrzeug in der Spalte „Verfügbar am …“. Belegung = offene Touren an diesem Tag.
    public sealed class VerfuegbarZeile
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public VerfuegbarZeile(TagesFahrer f)
        {
            IstFahrer = true;
            Id = f.Fahrer.FahrerId;
            Titel = f.Fahrer.Name;
            Kurzname = f.Fahrer.Name;
            Details = string.Join(" · ", new[] { $"Klasse {f.Fahrer.Fuehrerscheinklasse}", f.Fahrer.Abteilung }
                .Where(teil => !string.IsNullOrWhiteSpace(teil)));
            GanzFrei = string.IsNullOrWhiteSpace(f.Touren);
            Belegung = GanzFrei ? "Ganzer Tag frei" : $"Belegt: {f.Touren}";
            BelegungFarbe = Farbe(GanzFrei);
        }

        public VerfuegbarZeile(TagesFahrzeug f)
        {
            IstFahrer = false;
            Id = f.Fahrzeug.Id;
            Aufbau = f.Fahrzeug.Aufbau;
            Titel = $"{f.Fahrzeug.Kennzeichen} · {f.Fahrzeug.Fahrzeugtyp}";
            Kurzname = f.Fahrzeug.Kennzeichen;
            Details = string.Join(" · ", new[]
            {
                f.Fahrzeug.Aufbau,
                f.Fahrzeug.NutzbareLademeter > 0 ? $"{f.Fahrzeug.NutzbareLademeter.ToString("0.#", Deutsch)} Ldm" : null,
                f.Fahrzeug.Abteilung
            }.Where(teil => !string.IsNullOrWhiteSpace(teil)));
            GanzFrei = string.IsNullOrWhiteSpace(f.Touren);
            Belegung = GanzFrei ? "Ganzer Tag frei" : $"Belegt: {f.Touren}";
            BelegungFarbe = Farbe(GanzFrei);
        }

        // Ganz frei: grüner Text. Sonst die Touren des Tages in der normalen Hinweisfarbe.
        private static Brush Farbe(bool ganzFrei) => ganzFrei
            ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x2E, 0xA6, 0x6A))
            : (Brush)Application.Current.Resources["MutedTextBrush"];

        public bool IstFahrer { get; }
        public long Id { get; }
        // Nur bei Fahrzeugen: „Frische“ (Kühlaufbau) oder „Trockengut“.
        public string? Aufbau { get; }
        public string Titel { get; }
        // Für Meldungen und die Spalte „Fahrer / Fahrzeug“: Name bzw. Kennzeichen.
        public string Kurzname { get; }
        public string Details { get; }
        public bool GanzFrei { get; }
        public string Belegung { get; }
        public Brush BelegungFarbe { get; }
    }
}
