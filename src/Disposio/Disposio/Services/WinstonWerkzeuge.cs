using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.Models.Beta.Messages;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Die Werkzeuge, mit denen Winston (Claude) die echten Firmendaten liest: Touren, Stopps, Sendungen, Fuhrpark,
/// Meldungen, Fahrernachrichten und die Verkehrslage von TomTom. Alle Werkzeuge lesen nur – Winston ändert und
/// versendet nichts. Claude entscheidet selbst, welche Werkzeuge er für eine Frage braucht.
/// </summary>
public sealed class WinstonWerkzeuge
{
    // Umlaute und „→“ lesbar lassen statt als ü zu schreiben (spart Zeichen und damit Kosten).
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly WinstonDaten _daten = new();

    public static readonly IReadOnlyList<BetaToolUnion> Definitionen = new List<BetaToolUnion>
    {
        Werkzeug("lage_ueberblick",
            "Kennzahlen der Firma für jetzt: Touren heute und unterwegs, unverplante und angefragte Sendungen, " +
            "verspätete Stopps, offene Verspätungsvorschläge und offene Schadensmeldungen. Guter Einstieg für „Was steht an?“."),
        Werkzeug("touren_liste",
            "Alle laufenden Touren (Unterwegs, Pausiert) und alle Touren von heute: Status, Fahrer, Fahrzeug, nächster " +
            "offener Stopp mit Zeitfenster-Ende, ETA und Verspätung in Minuten, Zeit der letzten GPS-Position."),
        Werkzeug("tour_details",
            "Eine Tour im Detail: alle Stopps mit Zeitfenster, Status, ETA, Verspätung, Ist-Zeiten und den Sendungen je Stopp, " +
            "dazu Fahrer mit Sprache, Fahrzeug und Alter der letzten Position.",
            ("tournummer", "Tournummer aus touren_liste, z. B. T-9001")),
        Werkzeug("verspaetungen",
            "Alle offenen Stopps laufender Touren, deren ETA nach dem Ende des Zeitfensters liegt, größte Verspätung zuerst. " +
            "Mit Kunden und der Angabe, ob der Kunde schon informiert wurde oder ein Vorschlag in der Kundenkommunikation (D10) wartet."),
        Werkzeug("verkehr_auf_route",
            "Aktuelle Verkehrslage von TomTom auf der Reststrecke einer Tour (letzte Position → offene Stopps): Fahrzeit, " +
            "Zeitverlust durch Verkehr, Staus, Baustellen und Unfälle auf der Route sowie Sperrungen auf der sonst üblichen " +
            "Strecke, die gerade umfahren werden. Nutze es für Fragen nach Sperrungen, Staus oder dem Grund einer Verspätung.",
            ("tournummer", "Tournummer aus touren_liste, z. B. T-9001")),
        Werkzeug("sendungen_liste",
            "Sendungen der Firma mit Kunde, Status, Abhol- und Lieferfenster, Ladung und zugeordneter Tour.",
            ("nur_unverplant", "true = nur Sendungen ohne Tour, false = alle aktuellen Sendungen")),
        Werkzeug("fuhrpark",
            "Fahrzeuge (Status, Kapazität, TÜV) und Fahrer (Sprache, Verfügbarkeit), jeweils mit der Tour, auf der sie gerade unterwegs sind."),
        Werkzeug("meldungen",
            "Schadensmeldungen (offene und die der letzten 14 Tage) und Tankvorgänge der letzten 14 Tage aus der Fahrer-App."),
        Werkzeug("fahrer_nachrichten",
            "Die letzten 20 Chat-Nachrichten zwischen Disposition und Fahrer einer Tour, mit Übersetzung.",
            ("tournummer", "Tournummer aus touren_liste, z. B. T-9001")),
    };

    private static BetaToolUnion Werkzeug(string name, string beschreibung, params (string Name, string Beschreibung)[] angaben) =>
        new BetaTool
        {
            Name = name,
            Description = beschreibung,
            InputSchema = new()
            {
                Properties = angaben.ToDictionary(a => a.Name, a => JsonSerializer.SerializeToElement(new
                {
                    type = a.Name == "nur_unverplant" ? "boolean" : "string",
                    description = a.Beschreibung
                })),
                Required = angaben.Select(a => a.Name).ToList(),
            },
        };

    /// <summary>
    /// Führt ein Werkzeug aus. Fehler gehen als Text an Claude zurück, damit Winston dem Disponenten sagen kann,
    /// was fehlt. Nur eine beendete Sitzung (UnauthorizedAccessException) wird weitergereicht.
    /// </summary>
    public async Task<(string Ergebnis, bool IstFehler)> AusfuehrenAsync(string name,
        IReadOnlyDictionary<string, JsonElement> eingabe, CancellationToken cancellationToken)
    {
        try
        {
            string ergebnis = name switch
            {
                "lage_ueberblick" => await _daten.LageAsync(cancellationToken),
                "touren_liste" => await _daten.TourenAsync(cancellationToken),
                "tour_details" => await _daten.TourAsync(TextAus(eingabe, "tournummer"), cancellationToken),
                "verspaetungen" => await _daten.VerspaetungenAsync(cancellationToken),
                "verkehr_auf_route" => await VerkehrAsync(TextAus(eingabe, "tournummer"), cancellationToken),
                "sendungen_liste" => await _daten.SendungenAsync(JaAus(eingabe, "nur_unverplant"), cancellationToken),
                "fuhrpark" => await _daten.FuhrparkAsync(cancellationToken),
                "meldungen" => await _daten.MeldungenAsync(cancellationToken),
                "fahrer_nachrichten" => await _daten.NachrichtenAsync(TextAus(eingabe, "tournummer"), cancellationToken),
                _ => throw new ArgumentException($"Das Werkzeug {name} gibt es nicht."),
            };
            return (ergebnis, false);
        }
        catch (Exception ex) when (ex is ArgumentException or VerkehrException)
        {
            // Diese Texte sind für Menschen geschrieben und enthalten keine Zugangsdaten.
            return (ex.Message, true);
        }
        catch (InvalidOperationException) when (BenutzerSitzung.IstFirmenVorschau)
        {
            return ("In der Vorschau ohne Anmeldung gibt es keine Firmendaten.", true);
        }
        catch (Exception ex) when (ex is NeonSqlException or TimeoutException or HttpRequestException or InvalidOperationException)
        {
            return ("Die Daten konnten gerade nicht geladen werden (Datenbank nicht erreichbar).", true);
        }
    }

    /// <summary>Verkehrslage auf der Reststrecke einer Tour als JSON. Auch der Verspätungsvorschlag nutzt das.</summary>
    /// <exception cref="ArgumentException">Die Tour gibt es nicht.</exception>
    /// <exception cref="VerkehrException">Keine Koordinaten oder TomTom nicht erreichbar.</exception>
    public async Task<string> VerkehrAsync(string tournummer, CancellationToken cancellationToken)
    {
        IReadOnlyList<RoutenPunkt> punkte = await _daten.RoutenpunkteAsync(tournummer, cancellationToken);
        RoutenVerkehr v = await TomTomVerkehr.LadenAsync(punkte, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            quelle = "TomTom, Stand jetzt",
            reststrecke = punkte.Select(p => p.Bezeichnung),
            kilometer = v.Kilometer,
            fahrzeit_min = v.FahrzeitMinuten,
            zeitverlust_durch_verkehr_min = v.VerzoegerungMinuten,
            uebliche_route = new { kilometer = v.UeblicheKilometer, fahrzeit_ohne_verkehr_min = v.UeblicheFahrzeitMinuten },
            stoerungen = v.Stoerungen.Select(s => new
            {
                art = s.Art,
                beschreibung = s.Beschreibung.Length > 0 ? s.Beschreibung : null,
                strasse = s.Strasse,
                von = s.Von,
                nach = s.Nach,
                zeitverlust_min = s.VerzoegerungMinuten,
                laenge_m = s.LaengeMeter > 0 ? s.LaengeMeter : (int?)null,
                lage = s.AufAktuellerRoute
                    ? "auf der aktuell schnellsten Route"
                    : "auf der sonst üblichen Route, wird gerade umfahren"
            }),
            hinweis = v.Stoerungen.Count == 0
                ? "Keine Störungen auf der Reststrecke gemeldet."
                : null,
            vollstaendig = v.Vollstaendig ? (bool?)null : false
        }, new JsonSerializerOptions(Json) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    private static string TextAus(IReadOnlyDictionary<string, JsonElement> eingabe, string name) =>
        eingabe.TryGetValue(name, out JsonElement wert) && wert.ValueKind == JsonValueKind.String &&
        wert.GetString() is { Length: > 0 } text
            ? text.Trim()
            : throw new ArgumentException($"Die Angabe „{name}“ fehlt.");

    private static bool JaAus(IReadOnlyDictionary<string, JsonElement> eingabe, string name) =>
        eingabe.TryGetValue(name, out JsonElement wert) &&
        (wert.ValueKind == JsonValueKind.True ||
         (wert.ValueKind == JsonValueKind.String && string.Equals(wert.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
}
