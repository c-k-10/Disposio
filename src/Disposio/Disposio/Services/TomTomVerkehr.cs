using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Verkehrslage auf der Reststrecke einer Tour für Winston: Staus, Baustellen und Sperrungen von TomTom.
/// Gleicher Schlüssel wie die Live-Karte (D08), siehe <see cref="TomTomKonfiguration"/>.
/// <para>So wird geprüft (höchstens 8 Abfragen je Tour, Ergebnis gilt 3 Minuten):</para>
/// <list type="number">
/// <item>Route MIT Verkehr (Routing API): Fahrzeit, Zeitverlust und die Störungen auf dieser Route.</item>
/// <item>Route OHNE Verkehr: die sonst übliche Strecke.</item>
/// <item>Störungen rund um beide Routen (Incident Details API, wie in D08): liefert die Texte
/// („A73 zwischen … gesperrt“) und zeigt Sperrungen auf der üblichen Strecke, die gerade umfahren werden.</item>
/// </list>
/// Die Adressen enthalten den Schlüssel und werden deshalb nie in Fehlermeldungen oder Logs geschrieben.
/// </summary>
internal static class TomTomVerkehr
{
    private const int MaxPunkte = 25;               // Position + Stopps je Abfrage
    private const int MaxBereiche = 6;              // Abfragen für Störungen je Tour
    private const double MaxFlaecheKm2 = 8000;      // TomTom erlaubt höchstens 10.000 km² je Abfrage
    private const double NaheMeter = 35;            // so nah an der Route gilt ein Punkt als „auf der Route“
    private const int KategorieSperrung = 8;
    private static readonly TimeSpan Gueltig = TimeSpan.FromMinutes(3);

    // Gleiche Felder wie die Live-Karte (D08).
    private const string Felder = "{incidents{type,geometry{type,coordinates},properties{id,iconCategory,"
        + "magnitudeOfDelay,events{description,code,iconCategory},from,to,delay,roadNumbers,length}}}";

    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://api.tomtom.com/"),
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly ConcurrentDictionary<string, (DateTime Zeit, RoutenVerkehr Wert)> Speicher = new();

    /// <param name="punkte">Aktuelle Position (oder letzter erledigter Stopp), danach die offenen Stopps in Reihenfolge.</param>
    /// <exception cref="VerkehrException">Schlüssel fehlt, zu wenige Koordinaten oder TomTom nicht erreichbar.</exception>
    public static async Task<RoutenVerkehr> LadenAsync(IReadOnlyList<RoutenPunkt> punkte, CancellationToken cancellationToken = default)
    {
        string schluessel = TomTomKonfiguration.SchluesselLesen()
            ?? throw new VerkehrException("Der TomTom-Schlüssel fehlt (Services/TomTomKonfiguration.cs).");
        if (punkte.Count < 2)
            throw new VerkehrException("Für diese Tour fehlen Koordinaten (Position oder offene Stopps). Die Verkehrslage lässt sich nicht prüfen.");

        var strecke = punkte.Take(MaxPunkte).ToArray();
        string weg = string.Join(':', strecke.Select(p => Zahl(p.Breite, "0.#####") + "," + Zahl(p.Laenge, "0.#####")));
        if (Speicher.TryGetValue(weg, out var alt) && DateTime.UtcNow - alt.Zeit < Gueltig)
            return alt.Wert;

        try
        {
            var liveTask = RouteAsync(weg, mitVerkehr: true, schluessel, cancellationToken);
            var ueblichTask = RouteAsync(weg, mitVerkehr: false, schluessel, cancellationToken);
            await Task.WhenAll(liveTask, ueblichTask).ConfigureAwait(false);
            Route live = await liveTask.ConfigureAwait(false);
            Route ueblich = await ueblichTask.ConfigureAwait(false);

            var bereiche = Bereiche(live.Punkte, ueblich.Punkte);
            bool vollstaendig = bereiche.Count <= MaxBereiche;
            var meldungen = (await Task.WhenAll(bereiche.Take(MaxBereiche)
                    .Select(b => StoerungenAsync(b, schluessel, cancellationToken))).ConfigureAwait(false))
                .SelectMany(liste => liste)
                .GroupBy(m => m.Id).Select(g => g.First()).ToArray();

            // Der Abgleich mit der Strecke ist reine Rechenarbeit und läuft nicht im Fenster-Thread.
            var stoerungen = await Task.Run(() => Zuordnen(live, ueblich, meldungen), cancellationToken).ConfigureAwait(false);

            var ergebnis = new RoutenVerkehr(
                Math.Round(live.Meter / 1000.0, 1), Minuten(live.Sekunden), Minuten(live.VerzoegerungSekunden),
                Math.Round(ueblich.Meter / 1000.0, 1), Minuten(ueblich.Sekunden), stoerungen, vollstaendig);
            Speicher[weg] = (DateTime.UtcNow, ergebnis);
            return ergebnis;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VerkehrException("TomTom hat nicht rechtzeitig geantwortet.");
        }
        catch (HttpRequestException)
        {
            // Die Meldung der Ausnahme kann die Adresse mit dem Schlüssel enthalten: nicht weitergeben.
            throw new VerkehrException("TomTom ist gerade nicht erreichbar (Internetverbindung prüfen).");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new VerkehrException("TomTom hat eine unerwartete Antwort geliefert.");
        }
    }

    // ── TomTom-Abfragen ──────────────────────────────────────────────────────────────

    private sealed record Punkt(double Breite, double Laenge);

    private sealed record Abschnitt(string Kategorie, int VerzoegerungSekunden, string? EreignisId);

    private sealed record Route(int Meter, int Sekunden, int VerzoegerungSekunden, Punkt[] Punkte, Abschnitt[] Abschnitte);

    private sealed record Meldung(string Id, int Kategorie, string Beschreibung, string? Strasse, string? Von, string? Nach,
        int VerzoegerungSekunden, int Meter, Punkt[] Punkte);

    private sealed record Bereich(double West, double Sued, double Ost, double Nord);

    // Wie in der API (Services/EtaRechner.cs): Lkw, schnellste Route. sectionType=traffic liefert die Störungen auf der Route.
    private static async Task<Route> RouteAsync(string weg, bool mitVerkehr, string schluessel, CancellationToken cancellationToken)
    {
        string adresse = $"routing/1/calculateRoute/{weg}/json?traffic={(mitVerkehr ? "true" : "false")}"
            + "&travelMode=truck&routeType=fastest" + (mitVerkehr ? "&sectionType=traffic" : "")
            + $"&key={Uri.EscapeDataString(schluessel)}";
        using JsonDocument json = await HolenAsync(adresse, cancellationToken).ConfigureAwait(false);
        JsonElement route = json.RootElement.GetProperty("routes")[0];
        JsonElement summe = route.GetProperty("summary");

        var punkte = new List<Punkt>();
        foreach (JsonElement leg in route.GetProperty("legs").EnumerateArray())
            foreach (JsonElement p in leg.GetProperty("points").EnumerateArray())
                punkte.Add(new Punkt(p.GetProperty("latitude").GetDouble(), p.GetProperty("longitude").GetDouble()));

        var abschnitte = new List<Abschnitt>();
        if (route.TryGetProperty("sections", out JsonElement sections))
        {
            foreach (JsonElement s in sections.EnumerateArray())
            {
                if (TextAus(s, "sectionType") != "TRAFFIC")
                    continue;
                abschnitte.Add(new Abschnitt(TextAus(s, "simpleCategory") ?? "OTHER", GanzAus(s, "delayInSeconds"), TextAus(s, "eventId")));
            }
        }

        return new Route(GanzAus(summe, "lengthInMeters"), GanzAus(summe, "travelTimeInSeconds"),
            GanzAus(summe, "trafficDelayInSeconds"), punkte.ToArray(), abschnitte.ToArray());
    }

    private static async Task<IReadOnlyList<Meldung>> StoerungenAsync(Bereich b, string schluessel, CancellationToken cancellationToken)
    {
        string bbox = string.Join(',', new[] { b.West, b.Sued, b.Ost, b.Nord }.Select(z => Zahl(z, "0.#####")));
        string adresse = "traffic/services/5/incidentDetails"
            + $"?key={Uri.EscapeDataString(schluessel)}&bbox={bbox}&fields={Uri.EscapeDataString(Felder)}"
            + "&language=de-DE&timeValidityFilter=present";
        using JsonDocument json = await HolenAsync(adresse, cancellationToken).ConfigureAwait(false);

        var liste = new List<Meldung>();
        if (!json.RootElement.TryGetProperty("incidents", out JsonElement incidents))
            return liste;

        foreach (JsonElement i in incidents.EnumerateArray())
        {
            if (!i.TryGetProperty("properties", out JsonElement p) || !i.TryGetProperty("geometry", out JsonElement g) ||
                !g.TryGetProperty("coordinates", out JsonElement koordinaten) || TextAus(p, "id") is not string id)
                continue;

            // TomTom liefert [Längengrad, Breitengrad]. Ein Punkt kommt als ein Paar, eine Strecke als Liste von Paaren.
            var punkte = new List<Punkt>();
            if (TextAus(g, "type") == "Point")
                punkte.Add(new Punkt(koordinaten[1].GetDouble(), koordinaten[0].GetDouble()));
            else
                foreach (JsonElement c in koordinaten.EnumerateArray())
                    punkte.Add(new Punkt(c[1].GetDouble(), c[0].GetDouble()));
            if (punkte.Count == 0)
                continue;

            string beschreibung = "";
            if (p.TryGetProperty("events", out JsonElement events) && events.ValueKind == JsonValueKind.Array)
                beschreibung = string.Join(", ", events.EnumerateArray().Select(e => TextAus(e, "description"))
                    .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct());
            string? strasse = p.TryGetProperty("roadNumbers", out JsonElement nummern) && nummern.ValueKind == JsonValueKind.Array
                ? string.Join("/", nummern.EnumerateArray().Select(n => n.GetString()).Where(n => !string.IsNullOrWhiteSpace(n)))
                : null;

            liste.Add(new Meldung(id, GanzAus(p, "iconCategory"), beschreibung,
                string.IsNullOrEmpty(strasse) ? null : strasse, TextAus(p, "from"), TextAus(p, "to"),
                GanzAus(p, "delay"), GanzAus(p, "length"), punkte.ToArray()));
        }
        return liste;
    }

    private static async Task<JsonDocument> HolenAsync(string adresse, CancellationToken cancellationToken)
    {
        using HttpResponseMessage antwort = await Http.GetAsync(adresse, cancellationToken).ConfigureAwait(false);
        if (antwort.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new VerkehrException("TomTom lehnt die Abfrage ab (Schlüssel ungültig oder Tageskontingent aufgebraucht).");
        if (!antwort.IsSuccessStatusCode)
            throw new VerkehrException($"TomTom antwortet mit Fehler {(int)antwort.StatusCode}.");
        await using var inhalt = await antwort.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(inhalt, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ── Auswertung ───────────────────────────────────────────────────────────────────

    // Störungen der aktuellen Route (mit Text aus den Meldungen) und Sperrungen auf der üblichen Route.
    private static IReadOnlyList<Verkehrsstoerung> Zuordnen(Route live, Route ueblich, Meldung[] meldungen)
    {
        var ergebnis = new List<Verkehrsstoerung>();
        var benutzt = new HashSet<string>();

        foreach (Abschnitt a in live.Abschnitte)
        {
            // Die Kennung des Abschnitts steht am Ende der Kennung der passenden Meldung.
            Meldung? m = a.EreignisId is null ? null
                : meldungen.FirstOrDefault(x => x.Id.EndsWith(a.EreignisId, StringComparison.Ordinal));
            if (m is not null && !benutzt.Add(m.Id))
                continue;
            int sekunden = Math.Max(a.VerzoegerungSekunden, 0);
            ergebnis.Add(m is null
                ? new Verkehrsstoerung(ArtAusAbschnitt(a.Kategorie), "", null, null, null, Minuten(sekunden), 0, true)
                : new Verkehrsstoerung(ArtAusKategorie(m.Kategorie), m.Beschreibung, m.Strasse, m.Von, m.Nach,
                    Minuten(sekunden), m.Meter, true));
        }

        // Sperrungen, die auf der üblichen Strecke liegen, aber nicht auf der aktuellen: Die Route fährt gerade um sie
        // herum. Eine Sperrung, die scheinbar auf der aktuellen Route liegt, betrifft sie nicht (z. B. nur eine
        // Abbiegespur) – sonst würde TomTom dort nicht entlangführen. Sie wird deshalb nicht gemeldet.
        foreach (Meldung m in meldungen.Where(x => x.Kategorie == KategorieSperrung && !benutzt.Contains(x.Id)))
        {
            if (LiegtAufRoute(m.Punkte, ueblich.Punkte) && !LiegtAufRoute(m.Punkte, live.Punkte))
                ergebnis.Add(new Verkehrsstoerung("Sperrung", m.Beschreibung, m.Strasse, m.Von, m.Nach, 0, m.Meter, false));
        }

        // TomTom meldet eine lange Störung oft in mehreren Teilstücken: gleiche Angaben zusammenfassen.
        return ergebnis
            .GroupBy(s => (s.Art, s.Beschreibung, s.Strasse, s.Von, s.Nach, s.AufAktuellerRoute))
            .Select(g => g.First() with
            {
                VerzoegerungMinuten = g.Sum(s => s.VerzoegerungMinuten),
                LaengeMeter = g.Sum(s => s.LaengeMeter)
            })
            .OrderByDescending(s => s.VerzoegerungMinuten)
            .ThenByDescending(s => s.Art == "Sperrung")
            .ThenByDescending(s => s.LaengeMeter)
            .Take(10)
            .ToArray();
    }

    // Liegt die Störung auf der Route und in Fahrtrichtung? Eine Querstraße berührt die Route nur an einem Ende,
    // die Gegenfahrbahn läuft in der Reihenfolge der Routenpunkte rückwärts.
    private static bool LiegtAufRoute(Punkt[] stoerung, Punkt[] route)
    {
        if (route.Length < 2)
            return false;

        int nah = 0;
        double erste = -1, letzte = -1;
        foreach (Punkt p in stoerung)
        {
            double stelle = StelleAufRoute(p, route);
            if (stelle < 0)
                continue;
            nah++;
            if (erste < 0) erste = stelle;
            letzte = stelle;
        }

        int noetig = stoerung.Length == 1 ? 1 : Math.Max(2, (int)Math.Ceiling(stoerung.Length * 0.6));
        return nah >= noetig && letzte >= erste;
    }

    // Stelle auf der Route (Nummer des Teilstücks + Anteil), an der der Punkt höchstens NaheMeter entfernt liegt; sonst -1.
    private static double StelleAufRoute(Punkt p, Punkt[] route)
    {
        // In der Nähe eines Punkts reicht die ebene Rechnung in Metern.
        double meterJeGradLaenge = 111_320 * Math.Cos(p.Breite * Math.PI / 180);
        const double meterJeGradBreite = 110_540;
        double beste = NaheMeter * NaheMeter;
        double stelle = -1;

        for (int i = 0; i < route.Length - 1; i++)
        {
            double ax = (route[i].Laenge - p.Laenge) * meterJeGradLaenge, ay = (route[i].Breite - p.Breite) * meterJeGradBreite;
            double bx = (route[i + 1].Laenge - p.Laenge) * meterJeGradLaenge, by = (route[i + 1].Breite - p.Breite) * meterJeGradBreite;
            double dx = bx - ax, dy = by - ay;
            double laenge2 = dx * dx + dy * dy;
            double t = laenge2 <= 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / laenge2, 0, 1);
            double x = ax + t * dx, y = ay + t * dy;
            double abstand2 = x * x + y * y;
            if (abstand2 <= beste)
            {
                beste = abstand2;
                stelle = i + t;
            }
        }
        return stelle;
    }

    // Rechtecke um die Route, je höchstens MaxFlaecheKm2 groß. Die übliche Route ergänzt nur, was noch fehlt.
    private static List<Bereich> Bereiche(Punkt[] live, Punkt[] ueblich)
    {
        const double rand = 0.003; // etwa 300 m
        var bereiche = new List<Bereich>();

        void Sammeln(IEnumerable<Punkt> punkte)
        {
            Bereich? offen = null;
            foreach (Punkt p in punkte)
            {
                if (bereiche.Any(b => Enthaelt(b, p)))
                    continue;
                Bereich neu = offen is null
                    ? new Bereich(p.Laenge - rand, p.Breite - rand, p.Laenge + rand, p.Breite + rand)
                    : new Bereich(Math.Min(offen.West, p.Laenge - rand), Math.Min(offen.Sued, p.Breite - rand),
                                  Math.Max(offen.Ost, p.Laenge + rand), Math.Max(offen.Nord, p.Breite + rand));
                if (offen is not null && Flaeche(neu) > MaxFlaecheKm2)
                {
                    bereiche.Add(offen);
                    offen = new Bereich(p.Laenge - rand, p.Breite - rand, p.Laenge + rand, p.Breite + rand);
                }
                else
                {
                    offen = neu;
                }
            }
            if (offen is not null)
                bereiche.Add(offen);
        }

        Sammeln(live);
        Sammeln(ueblich);
        return bereiche;
    }

    private static bool Enthaelt(Bereich b, Punkt p) =>
        p.Laenge >= b.West && p.Laenge <= b.Ost && p.Breite >= b.Sued && p.Breite <= b.Nord;

    private static double Flaeche(Bereich b)
    {
        double kmLaenge = 111.32 * Math.Cos((b.Sued + b.Nord) / 2 * Math.PI / 180);
        return (b.Ost - b.West) * kmLaenge * (b.Nord - b.Sued) * 110.54;
    }

    // Gleiche Einteilung wie das Menü „Störungsarten“ der Live-Karte (iconCategory von TomTom).
    private static string ArtAusKategorie(int kategorie) => kategorie switch
    {
        1 => "Unfall",
        6 => "Stau",
        7 => "Fahrstreifen gesperrt",
        8 => "Sperrung",
        9 => "Baustelle",
        14 => "Liegengebliebenes Fahrzeug",
        _ => "Störung"
    };

    private static string ArtAusAbschnitt(string kategorie) => kategorie switch
    {
        "JAM" => "Stau",
        "ROAD_WORK" => "Baustelle",
        "ROAD_CLOSURE" => "Sperrung",
        _ => "Störung"
    };

    private static int Minuten(int sekunden) => (int)Math.Ceiling(sekunden / 60.0);

    private static string Zahl(double wert, string format) => wert.ToString(format, CultureInfo.InvariantCulture);

    private static string? TextAus(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement wert) && wert.ValueKind == JsonValueKind.String ? wert.GetString() : null;

    private static int GanzAus(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement wert) && wert.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(wert.GetDouble())
            : 0;
}
