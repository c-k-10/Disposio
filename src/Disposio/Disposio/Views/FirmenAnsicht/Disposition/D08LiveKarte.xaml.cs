using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Disposio.Views
{
    // D08 zeigt eine OpenStreetMap-Karte (Bibliothek Leaflet) in einem WebView2.
    // Darüber liegen zwei Ebenen von TomTom, einzeln schaltbar:
    // - Verkehrsfluss: Straßen grün/orange/rot eingefärbt (fertige Kartenbilder)
    // - Störungen: eigene runde Symbole mit Popup, geladen über die Incident Details API (echte Daten)
    // Ohne TomTom-Schlüssel erscheint die Karte trotzdem, nur ohne Verkehr.
    // Links steht eine Suche nach Fahrer oder Kennzeichen. Positionen gibt es erst mit der Fahrer-App;
    // der Filter wird aber schon jetzt an die Karte übergeben (filterSetzen), damit die Marker ihn später nutzen.
    public sealed partial class D08LiveKarte : UserControl
    {
        // Die Karten-HTML wird über diesen lokalen https-Hostnamen geladen.
        private const string KartenHost = "disposio-karte.local";
        private const string AnzahlNachricht = "stoerungen-anzahl:";

        private readonly FirmenAnsichtKontext _kontext;
        private bool _gestartet;
        private bool _kartenBereit;
        private bool _hatSchluessel;

        // Fahrer und Fahrzeuge der Firma für die Suche. Gefiltert wird im Speicher, ohne neue Datenbankabfrage.
        private readonly KartenRepository _repository = new();
        private IReadOnlyList<KartenObjekt> _objekte = Array.Empty<KartenObjekt>();
        private IReadOnlyList<KartenObjekt> _treffer = Array.Empty<KartenObjekt>();
        private CancellationTokenSource? _laden;
        private bool _objekteGeladen;

        // ---- BEISPIEL-FAHRZEUG ----
        // Bis die Fahrer-App echte Standorte sendet, steht das erste Fahrzeug der Firma zur Ansicht an einer
        // festen Stelle in Nürnberg (Hafen). Zum Abschalten einfach auf false setzen.
        // Gibt es noch kein Fahrzeug (oder in der Vorschau), erscheint ein erfundenes Kennzeichen.
        private const bool BeispielFahrzeugZeigen = true;
        private const double BeispielBreite = 49.4106;
        private const double BeispielLaenge = 11.0487;
        private const string BeispielOrt = "Nürnberg Hafen";
        private bool _beispielGezeigt;

        private KartenObjekt? BeispielObjekt => BeispielFahrzeugZeigen
            ? _objekte.FirstOrDefault(objekt => objekt.Art == KartenObjektArt.Fahrzeug)
            : null;

        public D08LiveKarte(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            Loaded += async (_, _) =>
            {
                // Karte und Liste laden unabhängig voneinander.
                Task karte = StartenAsync();
                Task liste = ObjekteLadenAsync();
                await Task.WhenAll(karte, liste);
            };
            Unloaded += (_, _) =>
            {
                // Beim Verlassen der Ansicht Laden abbrechen und den Browser-Prozess der Karte freigeben.
                _laden?.Cancel();
                Karte.Close();
            };
        }

        // ---- Suche nach Fahrer oder Kennzeichen ----

        private async Task ObjekteLadenAsync()
        {
            if (_kontext.IstVorschau)
            {
                TrefferText.Text = "In der Vorschau ohne Anmeldung werden keine Fahrer und Fahrzeuge geladen.";
                SuchBox.IsEnabled = false;
                ArtAuswahl.IsEnabled = false;
                _objekteGeladen = true;
                await BeispielZeigenAsync();
                return;
            }

            var abbruch = new CancellationTokenSource();
            _laden = abbruch;
            TrefferText.Text = "Fahrer und Fahrzeuge werden geladen …";
            try
            {
                _objekte = await _repository.LadenAsync(abbruch.Token);
                if (abbruch.IsCancellationRequested)
                    return;
                _objekteGeladen = true;
                FilterAnwenden();
                await BeispielZeigenAsync();
            }
            catch (OperationCanceledException) when (abbruch.IsCancellationRequested)
            {
            }
            catch (UnauthorizedAccessException)
            {
                // Rolle entzogen oder Konto deaktiviert: Sitzung beenden, Anmeldung zeigen.
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                // Keine technischen Details anzeigen.
                TrefferText.Text = "Fahrer und Fahrzeuge konnten nicht geladen werden. Bitte prüfen Sie die Verbindung " +
                                   "und öffnen Sie die Ansicht erneut.";
            }
            finally
            {
                if (ReferenceEquals(_laden, abbruch))
                    _laden = null;
                abbruch.Dispose();
            }
        }

        private void Suche_TextChanged(object sender, TextChangedEventArgs e) => FilterAnwenden();

        private void ArtAuswahl_SelectionChanged(object sender, SelectionChangedEventArgs e) => FilterAnwenden();

        private void FilterAnwenden()
        {
            // SelectionChanged kann schon beim Aufbau der XAML-Datei ausgelöst werden.
            if (TrefferListe is null || TrefferText is null || SuchBox is null || ArtAuswahl is null || AuswahlHinweis is null)
                return;

            KartenObjektArt? art = ArtAuswahl.SelectedIndex switch
            {
                1 => KartenObjektArt.Fahrer,
                2 => KartenObjektArt.Fahrzeug,
                _ => null
            };
            string suche = SuchBox.Text;

            _treffer = _objekte.Where(objekt => (art is null || objekt.Art == art) && objekt.Passt(suche)).ToArray();
            string? beispiel = BeispielObjekt?.Schluessel;
            TrefferListe.ItemsSource = _treffer
                .Select(objekt => new KartenZeile(objekt, objekt.Schluessel == beispiel ? $"Beispielposition · {BeispielOrt}" : null))
                .ToList();
            AuswahlHinweis.Visibility = Visibility.Collapsed;

            TrefferText.Text = _objekte.Count == 0
                ? "Noch keine Fahrer oder Fahrzeuge angelegt."
                : _treffer.Count == 0
                    ? "Keine Treffer. Bitte Suche oder Auswahl ändern."
                    : $"{_treffer.Count} von {_objekte.Count} Einträgen";

            _ = FilterAnKarteAsync();
        }

        private async void TrefferListe_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TrefferListe.SelectedItem is not KartenZeile zeile)
            {
                AuswahlHinweis.Visibility = Visibility.Collapsed;
                return;
            }

            // Hat das Objekt einen Marker, zentriert die Karte darauf und öffnet sein Popup.
            bool gefunden = false;
            if (_kartenBereit)
            {
                try
                {
                    string antwort = await Karte.ExecuteScriptAsync($"objektZeigen({JsonSerializer.Serialize(zeile.Schluessel)})");
                    gefunden = antwort == "true";
                }
                catch (Exception)
                {
                    // Die Ansicht wurde gerade verlassen.
                }
            }

            AuswahlHinweis.Text = gefunden
                ? $"{zeile.Name} wird auf der Karte gezeigt (Beispielposition, keine echten Standortdaten)."
                : $"Für {zeile.Name} gibt es noch keine Position. Sie erscheint hier, sobald die Fahrer-App Standorte sendet.";
            AuswahlHinweis.Visibility = Visibility.Visible;
        }

        // Setzt das Beispiel-Fahrzeug auf die Karte, sobald Karte und Liste fertig sind.
        private async Task BeispielZeigenAsync()
        {
            if (!BeispielFahrzeugZeigen || _beispielGezeigt || !_kartenBereit || !_objekteGeladen)
                return;
            _beispielGezeigt = true;

            KartenObjekt? fahrzeug = BeispielObjekt;
            var daten = new
            {
                schluessel = fahrzeug?.Schluessel ?? "Fahrzeug:beispiel",
                titel = fahrzeug?.Name ?? "N-DI 2026",
                details = fahrzeug?.Details ?? "Erfundenes Fahrzeug zur Ansicht",
                ort = BeispielOrt,
                breite = BeispielBreite,
                laenge = BeispielLaenge
            };
            try
            {
                await Karte.ExecuteScriptAsync($"beispielFahrzeugSetzen({JsonSerializer.Serialize(daten)})");
                await FilterAnKarteAsync();
            }
            catch (Exception)
            {
                // Die Ansicht wurde gerade verlassen.
            }
        }

        // Übergibt die Schlüssel der Treffer (z. B. ["Fahrer:4","Fahrzeug:12"]) an die Karte.
        // Ohne Suche und mit „Fahrer und Fahrzeuge“ wird null übergeben: dann wird nichts ausgeblendet.
        private async Task FilterAnKarteAsync()
        {
            if (!_kartenBereit)
                return;

            bool ohneFilter = string.IsNullOrWhiteSpace(SuchBox.Text) && ArtAuswahl.SelectedIndex <= 0;
            string schluessel = ohneFilter ? "null" : JsonSerializer.Serialize(_treffer.Select(objekt => objekt.Schluessel));
            try
            {
                await Karte.ExecuteScriptAsync($"filterSetzen({schluessel})");
            }
            catch (Exception)
            {
                // Die Ansicht wurde gerade verlassen.
            }
        }

        // ---- Karte ----

        private async Task StartenAsync()
        {
            if (_gestartet)
                return;
            _gestartet = true;

            string? schluessel = TomTomKonfiguration.SchluesselLesen();
            _hatSchluessel = schluessel is not null;

            try
            {
                // Die HTML-Datei wird in einen eigenen Temp-Ordner geschrieben und über einen
                // virtuellen https-Host geladen. So hat die Seite eine echte Adresse, die die
                // Kartenserver als Herkunft (Referer) sehen.
                string ordner = Path.Combine(Path.GetTempPath(), "Disposio", "LiveKarte");
                Directory.CreateDirectory(ordner);
                await File.WriteAllTextAsync(Path.Combine(ordner, "index.html"), KartenHtml);

                await Karte.EnsureCoreWebView2Async();
                Karte.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    KartenHost, ordner, CoreWebView2HostResourceAccessKind.Deny);
                Karte.CoreWebView2.WebMessageReceived += Karte_WebMessageReceived;

                string adresse = $"https://{KartenHost}/index.html";
                if (schluessel is not null)
                    adresse += $"?key={Uri.EscapeDataString(schluessel)}";
                Karte.Source = new Uri(adresse);
            }
            catch (Exception)
            {
                // Keine technischen Details anzeigen.
                ZeigeStatus("Die Karte konnte nicht geöffnet werden. Bitte prüfen Sie, ob die " +
                            "Microsoft-Edge-WebView2-Runtime installiert ist.");
            }
        }

        // Die Karten-Seite meldet per postMessage, wie es ihr geht.
        private async void Karte_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            string nachricht;
            try
            {
                nachricht = args.TryGetWebMessageAsString();
            }
            catch (Exception)
            {
                return; // keine Textnachricht
            }

            if (nachricht.StartsWith(AnzahlNachricht, StringComparison.Ordinal))
            {
                // Die Seite hat die Störungen neu geladen und meldet, wie viele es im Umkreis gibt.
                // Format: „stoerungen-anzahl:gezeigt:gesamt“. Gezeigt = nach dem Filter der Störungsarten.
                string[] teile = nachricht[AnzahlNachricht.Length..].Split(':');
                if (teile.Length == 2 && int.TryParse(teile[0], out int gezeigt) && int.TryParse(teile[1], out int gesamt))
                {
                    string text = gezeigt == gesamt
                        ? (gesamt == 1 ? "1 Störung" : $"{gesamt} Störungen")
                        : $"{gezeigt} von {gesamt} Störungen";
                    ZeigeStatus($"Karte mit Verkehrslage geladen · {text} im Umkreis (Stand {DateTime.Now:HH:mm} Uhr).");
                }
                return;
            }

            switch (nachricht)
            {
                case "bereit":
                    _kartenBereit = true;
                    AktualisierenKnopf.IsEnabled = true;
                    await FilterAnKarteAsync();
                    await BeispielZeigenAsync();
                    if (_hatSchluessel)
                    {
                        ZeigeStatus("Karte mit Verkehrslage geladen. Fahrzeugpositionen folgen in einem späteren Schritt.");
                        FlussSchalter.IsEnabled = true;
                        StoerungenSchalter.IsEnabled = true;
                        ArtenKnopf.IsEnabled = StoerungenSchalter.IsOn;
                        await ArtenAnwendenAsync();
                        await EbenenAnwendenAsync();
                    }
                    else
                    {
                        ZeigeStatus("Karte geladen. Für die Verkehrslage bitte den TomTom-Schlüssel in " +
                                    "Services/TomTomKonfiguration.cs eintragen.");
                    }
                    break;
                case "verkehr-fehler":
                    ZeigeStatus("Der Verkehrsfluss konnte nicht geladen werden. Bitte TomTom-Schlüssel und " +
                                "Internetverbindung prüfen.");
                    break;
                case "stoerungen-fehler":
                    ZeigeStatus("Die Verkehrsstörungen konnten nicht geladen werden. Bitte TomTom-Schlüssel, " +
                                "Monatskontingent und Internetverbindung prüfen.");
                    break;
                case "lade-fehler":
                    ZeigeStatus("Die Karte konnte nicht geladen werden. Bitte prüfen Sie die Internetverbindung.");
                    break;
            }
        }

        // Beide Schalter nutzen denselben Handler: Es wird immer der Stand beider Schalter übertragen.
        private async void EbenenSchalter_Toggled(object sender, RoutedEventArgs e)
        {
            // Die Arten lassen sich nur wählen, wenn Störungen überhaupt angezeigt werden.
            if (_kartenBereit && _hatSchluessel)
                ArtenKnopf.IsEnabled = StoerungenSchalter.IsOn;
            await EbenenAnwendenAsync();
        }

        // ---- Aktualisieren-Knopf ----

        // Lädt sofort neu: Verkehrsfluss-Bilder, Störungen (ohne auf die 5 Minuten zu warten) und die Liste.
        // Die automatische Aktualisierung bleibt daneben unverändert.
        private async void Aktualisieren_Click(object sender, RoutedEventArgs e)
        {
            if (!_kartenBereit)
                return;

            AktualisierenKnopf.IsEnabled = false;
            AktualisierenText.Text = "Wird aktualisiert …";
            try
            {
                try
                {
                    await Karte.ExecuteScriptAsync("allesAktualisieren()");
                }
                catch (Exception)
                {
                    return; // Die Ansicht wurde gerade verlassen.
                }

                // Liste neu laden (nicht in der Vorschau und nicht, wenn sie gerade schon lädt).
                if (!_kontext.IstVorschau && _laden is null)
                    await ObjekteLadenAsync();

                // Kurz warten, damit ein Doppelklick nicht gleich die nächste Abfrage startet.
                await Task.Delay(1500);
            }
            finally
            {
                AktualisierenText.Text = "Aktualisieren";
                AktualisierenKnopf.IsEnabled = true;
            }
        }

        // ---- Filter für die Störungsarten ----

        private async void StoerungsArt_Click(object sender, RoutedEventArgs e) => await ArtenAnwendenAsync();

        private async void AlleArten_Click(object sender, RoutedEventArgs e)
        {
            foreach (ToggleMenuFlyoutItem art in ArtenEintraege())
                art.IsChecked = true;
            await ArtenAnwendenAsync();
        }

        private ToggleMenuFlyoutItem[] ArtenEintraege() =>
            new[] { ArtStau, ArtUnfall, ArtBaustelle, ArtSperrung, ArtSonstige };

        // Welche TomTom-Kategorien (iconCategory) zu welchem Menüpunkt gehören.
        private static readonly int[] KategorienStau = { 6 };
        private static readonly int[] KategorienUnfall = { 1 };
        private static readonly int[] KategorienBaustelle = { 7, 9 };
        private static readonly int[] KategorienSperrung = { 8 };
        private static readonly int[] KategorienSonstige = { 0, 2, 3, 4, 5, 10, 11, 14 };

        // Schickt die gewählten Kategorien an die Karte und beschriftet den Knopf.
        private async Task ArtenAnwendenAsync()
        {
            var kategorien = new List<int>();
            if (ArtStau.IsChecked) kategorien.AddRange(KategorienStau);
            if (ArtUnfall.IsChecked) kategorien.AddRange(KategorienUnfall);
            if (ArtBaustelle.IsChecked) kategorien.AddRange(KategorienBaustelle);
            if (ArtSperrung.IsChecked) kategorien.AddRange(KategorienSperrung);
            if (ArtSonstige.IsChecked) kategorien.AddRange(KategorienSonstige);

            ToggleMenuFlyoutItem[] gewaehlt = ArtenEintraege().Where(art => art.IsChecked).ToArray();
            ArtenKnopf.Content = gewaehlt.Length switch
            {
                5 => "Alle Arten",
                0 => "Keine Art",
                // Kurzname aus Tag, damit der Knopf schmal bleibt (z. B. „Nur Sonstige“).
                1 => $"Nur {gewaehlt[0].Tag as string ?? gewaehlt[0].Text}",
                _ => $"{gewaehlt.Length} von 5 Arten"
            };

            if (!_kartenBereit || !_hatSchluessel)
                return;
            try
            {
                await Karte.ExecuteScriptAsync($"stoerungsArtenSetzen({JsonSerializer.Serialize(kategorien)})");
            }
            catch (Exception)
            {
                // Die Ansicht wurde gerade verlassen.
            }
        }

        // Blendet Verkehrsfluss und Störungen in der Karte einzeln ein oder aus.
        private async Task EbenenAnwendenAsync()
        {
            // Toggled kann schon beim Aufbau der XAML-Datei ausgelöst werden, bevor die Karte bereit ist.
            if (!_kartenBereit || !_hatSchluessel)
                return;

            try
            {
                string fluss = FlussSchalter.IsOn ? "true" : "false";
                string stoerungen = StoerungenSchalter.IsOn ? "true" : "false";
                await Karte.ExecuteScriptAsync($"ebenenAnzeigen({fluss}, {stoerungen})");
            }
            catch (Exception)
            {
                // Die Ansicht wurde gerade verlassen; dann gibt es nichts mehr umzuschalten.
            }
        }

        private void ZeigeStatus(string text)
        {
            StatusText.Text = text;
            LadeAnzeige.IsActive = false;
            LadeAnzeige.Visibility = Visibility.Collapsed;
        }

        // Startansicht: Nürnberg, Zoom 11. Leaflet kommt in einer festen Version vom CDN unpkg.
        // Die Hinweise „© OpenStreetMap-Mitwirkende“ und „Verkehr © TomTom“ unten rechts sind Pflicht.
        // Der TomTom-Schlüssel kommt aus der Adresse (?key=…). Ohne Schlüssel gibt es keine Verkehrsebenen.
        //
        // Störungen sparsam laden (Gratis-Kontingent ca. 2.500 Abfragen im Monat):
        // - eine Abfrage liefert alle Störungen in einem Bereich etwas größer als der sichtbare Ausschnitt
        // - neu geladen wird nur, wenn man aus diesem Bereich herausschiebt oder die Daten älter als 5 Minuten sind
        // - zwischen zwei Abfragen liegen mindestens 15 Sekunden
        private const string KartenHtml = """
            <!DOCTYPE html>
            <html lang="de">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                <title>Live-Karte</title>
                <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css" />
                <style>
                    html, body, #karte { height: 100%; margin: 0; padding: 0; }
                    body { font-family: "Segoe UI", sans-serif; }

                    /* Runde Störungssymbole wie bei Google Maps */
                    .stoerung-symbol { background: none; border: none; }
                    .stoerung-symbol div {
                        width: 26px; height: 26px; border-radius: 50%;
                        border: 2px solid #fff; box-shadow: 0 1px 4px rgba(0, 0, 0, .45);
                        display: flex; align-items: center; justify-content: center;
                        cursor: pointer;
                    }
                    .stoerung-symbol svg { width: 16px; height: 16px; }

                    /* Fahrzeug-Marker: blauer Kreis mit Lkw und Kennzeichen daneben */
                    .fahrzeug-symbol { background: none; border: none; }
                    .fahrzeug-symbol .fz { display: flex; align-items: center; gap: 6px; white-space: nowrap; cursor: pointer; }
                    .fahrzeug-symbol .punkt {
                        width: 30px; height: 30px; border-radius: 50%; background: #1A73E8;
                        border: 3px solid #fff; box-shadow: 0 1px 5px rgba(0, 0, 0, .45);
                        display: flex; align-items: center; justify-content: center; flex: none;
                    }
                    .fahrzeug-symbol .punkt svg { width: 17px; height: 17px; }
                    .fahrzeug-symbol .schild {
                        background: #fff; color: #202124; font-size: 12px; font-weight: 600;
                        padding: 2px 7px; border-radius: 6px; box-shadow: 0 1px 4px rgba(0, 0, 0, .35);
                    }
                    .fahrzeug .hinweis { color: #B3261E; font-size: 12px; margin-top: 4px; }

                    /* Popup beim Anklicken */
                    .stoerung { font-size: 13px; line-height: 1.4; min-width: 200px; }
                    .stoerung .titel { font-size: 15px; font-weight: 600; margin-bottom: 2px; }
                    .stoerung .ort { color: #555; }
                    .stoerung .verzoegerung { font-weight: 600; margin-top: 4px; }
                </style>
            </head>
            <body>
                <div id="karte"></div>
                <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"
                        onerror="window.chrome?.webview?.postMessage('lade-fehler')"></script>
                <script>
                    const melden = text => window.chrome?.webview?.postMessage(text);

                    // ---- Arten von Störungen (iconCategory der TomTom Incident Details API) ----
                    const ARTEN = {
                        0: "Störung", 1: "Unfall", 2: "Nebel", 3: "Gefahrenstelle", 4: "Starkregen", 5: "Glätte",
                        6: "Stau", 7: "Fahrstreifen gesperrt", 8: "Straße gesperrt", 9: "Baustelle",
                        10: "Starker Wind", 11: "Überflutung", 14: "Pannenfahrzeug"
                    };

                    // Weiße Zeichen in den runden Symbolen (Ansichtsbereich 24 x 24)
                    const ZEICHEN = {
                        warnung: '<path d="M12 3 L22 20 H2 Z" fill="none" stroke="#fff" stroke-width="2.2" stroke-linejoin="round"/>'
                               + '<rect x="11" y="9" width="2" height="6" fill="#fff"/><rect x="11" y="16.5" width="2" height="2" fill="#fff"/>',
                        baustelle: '<path d="M9.5 3h5l4.5 16h-14z" fill="#fff"/><rect x="3" y="19" width="18" height="2.5" rx="1" fill="#fff"/>'
                               + '<path d="M8 9h8M6.6 14h10.8" stroke="FARBE" stroke-width="2.2"/>',
                        sperrung: '<rect x="4" y="10" width="16" height="4" rx="1" fill="#fff"/>',
                        stau: '<path d="M4 16v-3l2-5a2 2 0 0 1 1.9-1.4h8.2A2 2 0 0 1 18 8l2 5v3a1 1 0 0 1-1 1h-1a2 2 0 0 1-4 0h-4a2 2 0 0 1-4 0H5a1 1 0 0 1-1-1z" fill="#fff"/>'
                    };

                    function zeichenArt(kategorie) {
                        if (kategorie === 6) return "stau";
                        if (kategorie === 7 || kategorie === 9) return "baustelle";
                        if (kategorie === 8) return "sperrung";
                        return "warnung";
                    }

                    // Farbe nach Art; Staus nach Stärke der Verzögerung (1 gering … 3 groß)
                    function farbe(kategorie, staerke) {
                        if (kategorie === 1) return "#D93025";               // Unfall
                        if (kategorie === 8) return "#B3261E";               // Sperrung
                        if (kategorie === 7 || kategorie === 9) return "#E37400"; // Baustelle
                        if (kategorie === 6) return staerke >= 3 ? "#B3261E" : staerke === 2 ? "#E8710A" : "#F9AB00";
                        return "#5F6368";                                    // Wetter, Gefahr, sonstiges
                    }

                    function escapeHtml(text) {
                        return String(text ?? "").replace(/[&<>"']/g, z =>
                            ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[z]);
                    }

                    function popupInhalt(p) {
                        const titel = ARTEN[p.iconCategory] ?? "Störung";
                        const beschreibungen = [...new Set((p.events ?? []).map(e => e.description).filter(Boolean))];
                        const strassen = (p.roadNumbers ?? []).join(", ");
                        const strecke = p.from && p.to ? `${p.from} → ${p.to}` : (p.from || p.to || "");
                        const ort = [strassen, strecke].filter(Boolean).join(" · ");
                        let verzoegerung = "";
                        if (p.delay >= 60) verzoegerung = `+${Math.round(p.delay / 60)} Min Verzögerung`;
                        else if (p.iconCategory === 8) verzoegerung = "Gesperrt";
                        const laenge = p.length >= 100 ? ` · ${(p.length / 1000).toFixed(1).replace(".", ",")} km` : "";

                        return `<div class="stoerung">
                            <div class="titel">${escapeHtml(titel)}</div>
                            ${beschreibungen.length && beschreibungen.join(", ") !== titel ? `<div>${escapeHtml(beschreibungen.join(", "))}</div>` : ""}
                            ${ort ? `<div class="ort">${escapeHtml(ort)}</div>` : ""}
                            ${verzoegerung || laenge ? `<div class="verzoegerung">${escapeHtml(verzoegerung)}${escapeHtml(laenge)}</div>` : ""}
                        </div>`;
                    }

                    function symbol(kategorie, staerke) {
                        const f = farbe(kategorie, staerke);
                        const svg = ZEICHEN[zeichenArt(kategorie)].replace("FARBE", f);
                        return L.divIcon({
                            className: "stoerung-symbol",
                            html: `<div style="background:${f}"><svg viewBox="0 0 24 24">${svg}</svg></div>`,
                            iconSize: [30, 30],
                            iconAnchor: [15, 15],
                            popupAnchor: [0, -14]
                        });
                    }

                    if (typeof L === "undefined") {
                        melden("lade-fehler");
                    } else {
                        const karte = L.map("karte").setView([49.4521, 11.0767], 11); // Nürnberg
                        L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
                            maxZoom: 19,
                            attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>-Mitwirkende'
                        }).addTo(karte);
                        window.karte = karte; // für spätere Fahrzeug-Marker, z. B. L.marker([lat, lng]).addTo(karte)

                        const schluessel = new URLSearchParams(location.search).get("key");
                        const tomtomHinweis = 'Verkehr &copy; <a href="https://www.tomtom.com">TomTom</a>';
                        let fluss = null;
                        let stoerungen = null;

                        if (schluessel) {
                            const k = encodeURIComponent(schluessel);

                            // ---- Ebene 1: Verkehrsfluss als fertige Kartenbilder ----
                            fluss = L.tileLayer(
                                `https://api.tomtom.com/traffic/map/4/tile/flow/relative0/{z}/{x}/{y}.png?key=${k}&tileSize=256`,
                                { maxZoom: 19, zIndex: 2, attribution: tomtomHinweis });
                            let flussGemeldet = false;
                            fluss.on("tileerror", () => { if (!flussGemeldet) { flussGemeldet = true; melden("verkehr-fehler"); } });

                            // ---- Ebene 2: Störungen als echte Daten mit eigenen Symbolen ----
                            stoerungen = L.layerGroup([], { attribution: tomtomHinweis });

                            const MAX_FLAECHE_KM2 = 9000;          // TomTom erlaubt höchstens 10.000 km² je Abfrage
                            const MAX_ALTER_MS = 5 * 60 * 1000;     // Daten nach 5 Minuten erneuern
                            const MIN_ABSTAND_MS = 15 * 1000;       // höchstens alle 15 Sekunden eine Abfrage
                            const FELDER = "{incidents{type,geometry{type,coordinates},properties{id,iconCategory,"
                                + "magnitudeOfDelay,events{description,code,iconCategory},from,to,delay,roadNumbers,length}}}";

                            let geladenBereich = null;
                            let geladenZeit = 0;
                            let letzteAnfrage = 0;
                            let laedt = false;
                            let wartet = null;
                            let stoerungenSichtbar = false;
                            let fehlerGemeldet = false;
                            let letzteListe = [];      // zuletzt geladene Störungen, für den Filter ohne neue Abfrage
                            let erlaubteArten = null;  // null = alle Arten, sonst Set mit iconCategory-Werten

                            // Fläche eines Bereichs in km² (gut genug für Deutschland)
                            const flaeche = b => {
                                const kmLon = 111.32 * Math.cos(b.getCenter().lat * Math.PI / 180);
                                return (b.getEast() - b.getWest()) * kmLon * (b.getNorth() - b.getSouth()) * 110.57;
                            };

                            // Abfragebereich: sichtbarer Ausschnitt plus Rand, höchstens MAX_FLAECHE_KM2 groß
                            function abfrageBereich() {
                                const b = karte.getBounds().pad(0.5);
                                const f = Math.min(1, Math.sqrt(MAX_FLAECHE_KM2 / flaeche(b)));
                                const c = b.getCenter();
                                const halbLat = (b.getNorth() - b.getSouth()) / 2 * f;
                                const halbLon = (b.getEast() - b.getWest()) / 2 * f;
                                return L.latLngBounds([c.lat - halbLat, c.lng - halbLon], [c.lat + halbLat, c.lng + halbLon]);
                            }

                            function neuLadenNoetig() {
                                if (!geladenBereich || Date.now() - geladenZeit > MAX_ALTER_MS) return true;
                                const sichtbar = karte.getBounds();
                                // Ist schon der sichtbare Ausschnitt zu groß, reicht es, wenn die Mitte abgedeckt ist.
                                if (flaeche(sichtbar) > MAX_FLAECHE_KM2) return !geladenBereich.contains(sichtbar.getCenter());
                                return !geladenBereich.contains(sichtbar);
                            }

                            function zeichnen() {
                                stoerungen.clearLayers();
                                const liste = erlaubteArten
                                    ? letzteListe.filter(s => erlaubteArten.has(s.properties?.iconCategory ?? 0))
                                    : letzteListe;
                                for (const s of liste) {
                                    const p = s.properties ?? {};
                                    const g = s.geometry ?? {};
                                    if (!g.coordinates) continue;

                                    // TomTom liefert [Längengrad, Breitengrad], Leaflet braucht [Breite, Länge].
                                    const punkte = g.type === "Point"
                                        ? [[g.coordinates[1], g.coordinates[0]]]
                                        : g.coordinates.map(c => [c[1], c[0]]);
                                    const inhalt = popupInhalt(p);

                                    // Große Staus und Sperrungen zusätzlich als Linie entlang der Straße
                                    if (punkte.length > 1 && ((p.iconCategory === 6 && p.magnitudeOfDelay >= 3) || p.iconCategory === 8)) {
                                        L.polyline(punkte, {
                                            color: farbe(p.iconCategory, p.magnitudeOfDelay),
                                            weight: 6, opacity: 0.85,
                                            dashArray: p.iconCategory === 8 ? "8 6" : null
                                        }).bindPopup(inhalt).addTo(stoerungen);
                                    }

                                    // Symbol in der Mitte der betroffenen Strecke
                                    const mitte = punkte[Math.floor(punkte.length / 2)];
                                    L.marker(mitte, { icon: symbol(p.iconCategory, p.magnitudeOfDelay), keyboard: false })
                                        .bindPopup(inhalt).addTo(stoerungen);
                                }
                                melden(`stoerungen-anzahl:${liste.length}:${letzteListe.length}`);
                            }

                            async function stoerungenLaden() {
                                if (!stoerungenSichtbar || laedt || !neuLadenNoetig()) return;

                                // Nicht zu oft abfragen: sonst später noch einmal versuchen.
                                const rest = MIN_ABSTAND_MS - (Date.now() - letzteAnfrage);
                                if (rest > 0) {
                                    if (!wartet) wartet = setTimeout(() => { wartet = null; stoerungenLaden(); }, rest);
                                    return;
                                }

                                laedt = true;
                                letzteAnfrage = Date.now();
                                const bereich = abfrageBereich();
                                const bbox = [bereich.getWest(), bereich.getSouth(), bereich.getEast(), bereich.getNorth()]
                                    .map(z => z.toFixed(5)).join(",");
                                const url = "https://api.tomtom.com/traffic/services/5/incidentDetails"
                                    + `?key=${k}&bbox=${bbox}&fields=${encodeURIComponent(FELDER)}`
                                    + "&language=de-DE&timeValidityFilter=present";
                                try {
                                    const antwort = await fetch(url);
                                    if (!antwort.ok) throw new Error(antwort.status);
                                    const daten = await antwort.json();
                                    geladenBereich = bereich;
                                    geladenZeit = Date.now();
                                    fehlerGemeldet = false;
                                    letzteListe = daten.incidents ?? [];
                                    if (stoerungenSichtbar) zeichnen();
                                } catch (e) {
                                    // Falscher Schlüssel, Kontingent aufgebraucht oder kein Internet: nur einmal melden.
                                    if (!fehlerGemeldet) { fehlerGemeldet = true; melden("stoerungen-fehler"); }
                                } finally {
                                    laedt = false;
                                }
                            }

                            // Nach dem Verschieben/Zoomen und einmal pro Minute prüfen, ob neu geladen werden muss.
                            karte.on("moveend", stoerungenLaden);
                            setInterval(stoerungenLaden, 60 * 1000);

                            // Für den Aktualisieren-Knopf: Daten als veraltet markieren und neu laden.
                            // Die Mindestpause von 15 Sekunden zwischen zwei Abfragen gilt trotzdem.
                            window.stoerungenNeuLaden = () => {
                                geladenZeit = 0;
                                stoerungenLaden();
                            };

                            // Wird von C# über das Menü „Störungsarten“ aufgerufen, z. B. [6, 7, 9] = Stau und Baustellen.
                            window.stoerungsArtenSetzen = arten => {
                                erlaubteArten = arten ? new Set(arten) : null;
                                if (stoerungenSichtbar && geladenBereich) zeichnen();
                            };

                            window.stoerungenSichtbarSetzen = an => {
                                stoerungenSichtbar = an;
                                if (an) {
                                    stoerungen.addTo(karte);
                                    if (geladenBereich) zeichnen(); // Filter könnte sich geändert haben, während die Ebene aus war
                                    stoerungenLaden();
                                } else {
                                    karte.removeLayer(stoerungen);
                                }
                            };
                        }

                        // Filter aus der Suche links. null = alles zeigen, sonst eine Liste von Schlüsseln wie "Fahrzeug:12".
                        // Noch gibt es keine Fahrer- und Fahrzeugmarker (die Positionen kommen aus der Fahrer-App).
                        // Später: Jeder Marker bekommt seinen Schlüssel, und hier werden die nicht passenden ausgeblendet.
                        // Fahrer- und Fahrzeugmarker, je mit Schlüssel. Ausgeblendete Marker bleiben hier gespeichert.
                        const objekte = new Map();          // Schlüssel → Marker
                        const objektEbene = L.layerGroup().addTo(karte);
                        window.kartenFilter = null;

                        const filterAnwenden = () => {
                            for (const [schluessel, marker] of objekte) {
                                const sichtbar = !window.kartenFilter || window.kartenFilter.has(schluessel);
                                if (sichtbar) objektEbene.addLayer(marker); else objektEbene.removeLayer(marker);
                            }
                        };

                        window.filterSetzen = schluessel => {
                            window.kartenFilter = schluessel ? new Set(schluessel) : null;
                            filterAnwenden();
                        };

                        // Zentriert auf einen Marker und öffnet sein Popup. Rückgabe: ob es den Marker gibt.
                        window.objektZeigen = schluessel => {
                            const marker = objekte.get(schluessel);
                            if (!marker || !objektEbene.hasLayer(marker)) return false;
                            karte.setView(marker.getLatLng(), Math.max(karte.getZoom(), 14));
                            marker.openPopup();
                            return true;
                        };

                        // BEISPIEL: ein Fahrzeug an fester Stelle, bis die Fahrer-App echte Standorte liefert.
                        const LKW = '<path d="M2 6.5h11.5v9H2z" fill="#fff"/><path d="M14.5 9h3.8l3.2 3.4v3.1h-7z" fill="#fff"/>'
                                  + '<circle cx="6" cy="17.5" r="2.2" fill="#fff" stroke="#1A73E8" stroke-width="1.2"/>'
                                  + '<circle cx="17.5" cy="17.5" r="2.2" fill="#fff" stroke="#1A73E8" stroke-width="1.2"/>';
                        window.beispielFahrzeugSetzen = d => {
                            const symbol = L.divIcon({
                                className: "fahrzeug-symbol",
                                html: `<div class="fz"><div class="punkt"><svg viewBox="0 0 24 24">${LKW}</svg></div>`
                                    + `<span class="schild">${escapeHtml(d.titel)}</span></div>`,
                                iconSize: null,
                                iconAnchor: [18, 18],
                                popupAnchor: [0, -16]
                            });
                            const zeit = new Date().toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" });
                            const marker = L.marker([d.breite, d.laenge], { icon: symbol, zIndexOffset: 1000, keyboard: false })
                                .bindPopup(`<div class="stoerung fahrzeug">
                                    <div class="titel">${escapeHtml(d.titel)}</div>
                                    <div>${escapeHtml(d.details)}</div>
                                    <div class="ort">${escapeHtml(d.ort)} · Stand ${zeit} Uhr</div>
                                    <div class="hinweis">Beispielposition – echte Standorte kommen mit der Fahrer-App.</div>
                                </div>`);
                            objekte.set(d.schluessel, marker);
                            filterAnwenden();
                        };

                        // Wird von C# über den Knopf „Aktualisieren“ aufgerufen.
                        window.allesAktualisieren = () => {
                            if (fluss && karte.hasLayer(fluss)) fluss.redraw(); // Verkehrsfluss-Bilder neu holen
                            if (window.stoerungenNeuLaden) window.stoerungenNeuLaden();
                        };

                        // Wird von C# über die Schalter „Verkehrsfluss“ und „Störungen“ aufgerufen.
                        window.ebenenAnzeigen = (flussAn, stoerungenAn) => {
                            if (fluss) { if (flussAn) fluss.addTo(karte); else karte.removeLayer(fluss); }
                            if (stoerungen) window.stoerungenSichtbarSetzen(stoerungenAn);
                        };

                        melden("bereit");
                    }
                </script>
            </body>
            </html>
            """;
    }

    // Eine Zeile in der Trefferliste. Die XAML-Vorlage bindet mit {x:Bind Name} daran.
    public sealed class KartenZeile
    {
        public KartenZeile(KartenObjekt objekt, string? positionText = null)
        {
            Schluessel = objekt.Schluessel;
            Name = objekt.Name;
            Details = objekt.Details;
            PositionText = positionText ?? "Noch keine Position";
            // Segoe-Fluent-Symbole: Person (E77B) für Fahrer, Auto (E804) für Fahrzeuge.
            Symbol = objekt.Art == KartenObjektArt.Fahrer ? "\uE77B" : "\uE804";
        }

        public string Schluessel { get; }
        public string Name { get; }
        public string Details { get; }
        public string PositionText { get; }
        public string Symbol { get; }
    }
}
