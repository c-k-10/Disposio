using System;
using System.IO;
using System.Threading.Tasks;
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
    public sealed partial class D08LiveKarte : UserControl
    {
        // Die Karten-HTML wird über diesen lokalen https-Hostnamen geladen.
        private const string KartenHost = "disposio-karte.local";
        private const string AnzahlNachricht = "stoerungen-anzahl:";

        private readonly FirmenAnsichtKontext _kontext;
        private bool _gestartet;
        private bool _kartenBereit;
        private bool _hatSchluessel;

        public D08LiveKarte(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;
            Loaded += async (_, _) => await StartenAsync();
            // Beim Verlassen der Ansicht den Browser-Prozess der Karte freigeben.
            Unloaded += (_, _) => Karte.Close();
        }

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
                if (int.TryParse(nachricht.AsSpan(AnzahlNachricht.Length), out int anzahl))
                {
                    string text = anzahl == 1 ? "1 Störung" : $"{anzahl} Störungen";
                    ZeigeStatus($"Karte mit Verkehrslage geladen · {text} im Umkreis (Stand {DateTime.Now:HH:mm} Uhr).");
                }
                return;
            }

            switch (nachricht)
            {
                case "bereit":
                    _kartenBereit = true;
                    if (_hatSchluessel)
                    {
                        ZeigeStatus("Karte mit Verkehrslage geladen. Fahrzeugpositionen folgen in einem späteren Schritt.");
                        FlussSchalter.IsEnabled = true;
                        StoerungenSchalter.IsEnabled = true;
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
        private async void EbenenSchalter_Toggled(object sender, RoutedEventArgs e) => await EbenenAnwendenAsync();

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

                            function zeichnen(liste) {
                                stoerungen.clearLayers();
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
                                melden("stoerungen-anzahl:" + liste.length);
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
                                    if (stoerungenSichtbar) zeichnen(daten.incidents ?? []);
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

                            window.stoerungenSichtbarSetzen = an => {
                                stoerungenSichtbar = an;
                                if (an) {
                                    stoerungen.addTo(karte);
                                    stoerungenLaden();
                                } else {
                                    karte.removeLayer(stoerungen);
                                }
                            };
                        }

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
}
