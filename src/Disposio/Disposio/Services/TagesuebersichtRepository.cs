using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Daten für D01 „Tagesübersicht“ des Disponenten. Liest nur – bis auf <see cref="AbmeldungSpeichernAsync"/>,
/// das beim Abmelden den Zeitpunkt merkt. Wie in den anderen Repositories wird zuerst die Rolle Disponent
/// geprüft und danach nur die Firma aus dieser Prüfung verwendet, nie eine Id aus der Oberfläche.
/// „Heute“ und alle angezeigten Uhrzeiten gelten in deutscher Zeit (Europe/Berlin).
/// </summary>
public sealed class TagesuebersichtRepository
{
    private const FirmenRolle Rolle = FirmenRolle.Disponent;

    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    // Gemeinsamer Anfang aller drei Abfragen. $1 = Firma, $2 = Konto.
    // „seit“ = letzte Abmeldung des Kontos. Ohne Abmeldung gilt gestern 18:00; höchstens 3 Tage zurück,
    // damit nach einem Urlaub nicht hunderte Einträge erscheinen.
    private const string Fenster = """
        WITH h AS (
            SELECT (now() AT TIME ZONE 'Europe/Berlin')::date AS tag
        ), abm AS (
            SELECT (SELECT b.letzte_abmeldung FROM public.benutzer b WHERE b.benutzer_id = $2::bigint) AS zeit
        ), fenster AS (
            SELECT GREATEST(COALESCE(abm.zeit, ((h.tag - 1) + time '18:00') AT TIME ZONE 'Europe/Berlin'),
                            now() - interval '72 hours') AS seit,
                   COALESCE(abm.zeit >= now() - interval '72 hours', false) AS aus_abmeldung
            FROM h, abm
        )
        """;

    // Kennzahlen oben und der Zeitraum. Genau eine Zeile.
    private const string KennzahlenQuery = Fenster + """
        SELECT to_char(f.seit AT TIME ZONE 'Europe/Berlin', 'YYYY-MM-DD HH24:MI'),
               f.aus_abmeldung,
               (SELECT count(*) FROM public.tour t
                WHERE t.unternehmen_id = $1::bigint AND (t.startzeit AT TIME ZONE 'Europe/Berlin')::date = h.tag),
               (SELECT count(*) FROM public.auftrag a
                WHERE a.unternehmen_id = $1::bigint AND a.tour_id IS NULL
                  AND a.abholdatum <= h.tag AND a.lieferdatum >= h.tag),
               -- Freigabe offen = Vorschlag von Winston, über den noch niemand entschieden hat (D10).
               -- Status „offen“: veraltete Vorschläge (Verspätung erledigt) zählen nicht mit.
               (SELECT count(*) FROM public.winston_vorschlag v
                JOIN public.auftrag a ON a.auftrag_id = v.auftrag_id
                WHERE a.unternehmen_id = $1::bigint AND v.status = 'offen'
                  AND NOT EXISTS (SELECT 1 FROM public.freigabe fr WHERE fr.vorschlag_id = v.vorschlag_id)),
               (SELECT count(*) FROM public.schadensmeldung s
                JOIN public.fahrzeug fz ON fz.fahrzeug_id = s.fahrzeug_id
                WHERE fz.unternehmen_id = $1::bigint
                  AND lower(s.status) NOT SIMILAR TO '%(erledigt|abgeschlossen|geschlossen|repariert)%')
        FROM fenster f, h
        """;

    // „Seit Ihrer letzten Schicht“: art, Zeit, Uhrzeit bekannt?, Text 1, Text 2, Id. Älteste zuerst.
    private const string EreignisseQuery = Fenster + """
        SELECT x.art, to_char(x.zeit AT TIME ZONE 'Europe/Berlin', 'YYYY-MM-DD HH24:MI'), x.genau, x.a, x.b, x.id
        FROM (
            -- Tour beendet (Fahrer-App). Dazu: wie viele Stopps erledigt sind.
            SELECT 'tour_beendet' AS art, sm.zeitstempel AS zeit, true AS genau, t.tournummer AS a,
                   (SELECT count(*) FILTER (WHERE st.status = 'Erledigt') || '/' || count(*)
                    FROM public.tourstopp st WHERE st.tour_id = t.tour_id) AS b,
                   t.tour_id AS id
            FROM public.statusmeldung sm
            JOIN public.tour t ON t.tour_id = sm.tour_id, fenster f
            WHERE t.unternehmen_id = $1::bigint AND sm.meldungsart = 'Tour' AND sm.status = 'Tour beendet'
              AND sm.zeitstempel >= f.seit

            UNION ALL
            -- Zustellung gescheitert: Kunde nicht angetroffen.
            SELECT 'kunde_nicht_da', sm.zeitstempel, true, t.tournummer,
                   COALESCE(NULLIF(st.bezeichnung, ''), st.adresse, ''), t.tour_id
            FROM public.statusmeldung sm
            JOIN public.tour t ON t.tour_id = sm.tour_id
            LEFT JOIN public.tourstopp st ON st.stopp_id = sm.stopp_id, fenster f
            WHERE t.unternehmen_id = $1::bigint AND sm.meldungsart = 'Stopp' AND sm.status = 'Kunde nicht da'
              AND sm.zeitstempel >= f.seit

            UNION ALL
            -- Schaden gemeldet. Schadensmeldungen haben nur ein Datum, keine Uhrzeit.
            SELECT 'schaden', (s.datum::timestamp AT TIME ZONE 'Europe/Berlin'), false, fz.kennzeichen,
                   s.schadensart || ' · ' || s.beschreibung, s.schaden_id
            FROM public.schadensmeldung s
            JOIN public.fahrzeug fz ON fz.fahrzeug_id = s.fahrzeug_id, fenster f
            WHERE fz.unternehmen_id = $1::bigint AND s.datum >= (f.seit AT TIME ZONE 'Europe/Berlin')::date

            UNION ALL
            -- US-25: Jede neue Sendung einzeln, mit Auftragsnummer und Auftraggeber (in der App angelegt oder über
            -- die Webseite angefragt). Das Protokoll kennt nur den Text „Sendung <Nummer>“ (App) bzw.
            -- „Sendung <Nummer> über die Webseite“ (neon-function/sendung.ts), darüber wird der Auftrag gefunden.
            -- b = Auftraggeber | Webseite? (t/f) | aktueller Status, getrennt mit |
            SELECT 'neue_sendung', p.zeitpunkt, true, a.auftragsnummer::text,
                   COALESCE(NULLIF(k.firmenname, ''), '') || '|' || COALESCE(p.akteur_art = 'Webseite', false)::text || '|' || COALESCE(a.status, ''),
                   a.auftrag_id
            FROM public.aktionsprotokoll p
            JOIN public.auftrag a ON a.unternehmen_id = p.unternehmen_id
                 AND p.detail IN ('Sendung ' || a.auftragsnummer, 'Sendung ' || a.auftragsnummer || ' über die Webseite')
            LEFT JOIN public.kunde k ON k.kunde_id = a.kunde_id AND k.unternehmen_id = a.unternehmen_id, fenster f
            WHERE p.unternehmen_id = $1::bigint AND p.aktion IN ('Sendung angelegt', 'Sendung angefragt')
              AND p.zeitpunkt >= f.seit
        ) x
        ORDER BY x.zeit
        LIMIT 50
        """;

    // „Heute priorisieren“: art, vier Texte, Id. Sortiert nach Dringlichkeit (x.rang), dann Zeit.
    private const string HinweiseQuery = Fenster + """
        SELECT x.art, x.a, x.b, x.c, x.d, x.id
        FROM (
            -- 1. Lieferfenster wird verpasst: ETA nach Fensterende bei einem offenen Stopp.
            SELECT 1 AS rang, s.zeitfenster_bis AS zeit, 'spaet' AS art,
                   COALESCE(NULLIF(s.bezeichnung, ''), s.adresse, 'Stopp') AS a,
                   to_char(s.aktuelle_eta AT TIME ZONE 'Europe/Berlin', 'HH24:MI') AS b,
                   ceil(extract(epoch FROM s.aktuelle_eta - s.zeitfenster_bis) / 60)::int::text AS c,
                   t.tournummer AS d, t.tour_id AS id
            FROM public.tourstopp s
            JOIN public.tour t ON t.tour_id = s.tour_id, h
            WHERE t.unternehmen_id = $1::bigint AND s.status <> 'Erledigt'
              AND s.aktuelle_eta IS NOT NULL AND s.zeitfenster_bis IS NOT NULL AND s.aktuelle_eta > s.zeitfenster_bis
              AND lower(t.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%'
              AND ((s.zeitfenster_bis AT TIME ZONE 'Europe/Berlin')::date = h.tag OR t.status = 'Unterwegs')

            UNION ALL
            -- 2. Fahrzeug einer heutigen Tour hat einen offenen Schaden.
            SELECT 2, t.startzeit, 'schaden_tour', fz.kennzeichen, t.tournummer, s.schadensart, NULL, s.schaden_id
            FROM public.tour t
            JOIN public.fahrzeug fz ON fz.fahrzeug_id = t.fahrzeug_id
            JOIN public.schadensmeldung s ON s.fahrzeug_id = fz.fahrzeug_id, h
            WHERE t.unternehmen_id = $1::bigint AND (t.startzeit AT TIME ZONE 'Europe/Berlin')::date = h.tag
              AND lower(t.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%'
              AND lower(s.status) NOT SIMILAR TO '%(erledigt|abgeschlossen|geschlossen|repariert)%'

            UNION ALL
            -- 3. Heutige Tour ohne Fahrer oder ohne Fahrzeug.
            SELECT 3, t.startzeit, 'ohne_zuweisung', t.tournummer,
                   to_char(t.startzeit AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
                   t.startort || ' → ' || t.zielort,
                   CASE WHEN t.fahrer_id IS NULL AND t.fahrzeug_id IS NULL THEN 'Fahrer und Fahrzeug'
                        WHEN t.fahrer_id IS NULL THEN 'Fahrer' ELSE 'Fahrzeug' END,
                   t.tour_id
            FROM public.tour t, h
            WHERE t.unternehmen_id = $1::bigint AND (t.startzeit AT TIME ZONE 'Europe/Berlin')::date = h.tag
              AND (t.fahrer_id IS NULL OR t.fahrzeug_id IS NULL)
              AND lower(t.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%'

            UNION ALL
            -- 4. US-25: Über die Webseite angefragte Sendungen, einzeln mit Auftragsnummer und Auftraggeber.
            --    Der Hinweis bleibt, solange die Sendung den Status „Angefragt“ hat, also bis sie bearbeitet ist.
            SELECT 4, (a.abholdatum::timestamp AT TIME ZONE 'Europe/Berlin'), 'angefragt', a.auftragsnummer::text,
                   COALESCE(NULLIF(k.firmenname, ''), ''), to_char(a.abholdatum, 'YYYY-MM-DD'), NULL, a.auftrag_id
            FROM public.auftrag a
            LEFT JOIN public.kunde k ON k.kunde_id = a.kunde_id AND k.unternehmen_id = a.unternehmen_id, h
            WHERE a.unternehmen_id = $1::bigint AND a.status = 'Angefragt' AND a.lieferdatum >= h.tag

            UNION ALL
            -- 5. Unverplante Sendungen heute mit Stellplätzen, Lademetern und Gewicht.
            SELECT 5, NULL, 'unverplant', count(*)::text, sum(a.anzahl)::text,
                   round(sum(a.anzahl * a.laenge_m * a.breite_m / 2.4), 1)::text,
                   round(sum(a.gewicht_kg))::text, NULL
            FROM public.auftrag a, h
            WHERE a.unternehmen_id = $1::bigint AND a.tour_id IS NULL
              AND a.abholdatum <= h.tag AND a.lieferdatum >= h.tag
            HAVING count(*) > 0
        ) x
        ORDER BY x.rang, x.zeit NULLS LAST
        LIMIT 20
        """;

    private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

    public async Task<Tagesuebersicht> LadenAsync(CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var parameter = new string?[]
        {
            Text(zugriff.UnternehmenId),
            BenutzerSitzung.AktuellerBenutzer is { } benutzer ? Text(benutzer.Id) : null
        };

        // Die drei Abfragen sind unabhängig und laufen gleichzeitig.
        var kennzahlenTask = _client.Value.QueryAsync(KennzahlenQuery, parameter, cancellationToken);
        var ereignisseTask = _client.Value.QueryAsync(EreignisseQuery, parameter, cancellationToken);
        var hinweiseTask = _client.Value.QueryAsync(HinweiseQuery, parameter, cancellationToken);
        await Task.WhenAll(kennzahlenTask, ereignisseTask, hinweiseTask);

        string?[] k = (await kennzahlenTask)[0];
        var ereignisse = new List<SchichtEreignis>();
        foreach (var row in await ereignisseTask)
        {
            if (EreignisAus(row) is { } ereignis)
                ereignisse.Add(ereignis);
        }

        var hinweise = new List<TagesHinweis>();
        foreach (var row in await hinweiseTask)
        {
            if (HinweisAus(row) is { } hinweis)
                hinweise.Add(hinweis);
        }

        return new Tagesuebersicht(
            Zeit(Required(k, 0)),
            Required(k, 1) == "t",
            Zahl(k, 2), Zahl(k, 3), Zahl(k, 4), Zahl(k, 5),
            ereignisse, hinweise);
    }

    /// <summary>
    /// Merkt den Zeitpunkt der Abmeldung (benutzer.letzte_abmeldung, Migration 011). Fehler werden verschluckt:
    /// Eine fehlgeschlagene Speicherung darf das Abmelden nie verhindern; D01 nimmt dann gestern 18:00.
    /// </summary>
    public static async Task AbmeldungSpeichernAsync(long benutzerId)
    {
        try
        {
            using var abbruch = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await new NeonSqlClient().QueryAsync(
                "UPDATE public.benutzer SET letzte_abmeldung = now() WHERE benutzer_id = $1::bigint",
                new string?[] { Text(benutzerId) }, abbruch.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    // ── Zeilen in Anzeige-Texte übersetzen ─────────────────────────────────────────────

    private static SchichtEreignis? EreignisAus(string?[] row)
    {
        DateTime zeit = Zeit(Required(row, 1));
        bool genau = Required(row, 2) == "t";
        string a = row[3] ?? "";
        string b = row[4] ?? "";
        long? id = row[5] is string text ? long.Parse(text, CultureInfo.InvariantCulture) : null;

        return Required(row, 0) switch
        {
            "tour_beendet" => new SchichtEreignis(zeit, genau, $"Tour {a} abgeschlossen",
                StoppText(b), "Tourdetails", "D07", id),
            "kunde_nicht_da" => new SchichtEreignis(zeit, genau, $"Zustellung gescheitert · Tour {a}",
                b.Length > 0 ? $"{b}: Kunde nicht angetroffen." : "Kunde nicht angetroffen.", "Tourdetails", "D07", id),
            "schaden" => new SchichtEreignis(zeit, genau, "Schaden gemeldet",
                $"{a} · {b}", "Meldung", "D15", id),
            "neue_sendung" => NeueSendung(zeit, genau, a, b, id),
            _ => null
        };
    }

    private static TagesHinweis? HinweisAus(string?[] row)
    {
        string a = row[1] ?? "", b = row[2] ?? "", c = row[3] ?? "", d = row[4] ?? "";
        long? id = row[5] is string text ? long.Parse(text, CultureInfo.InvariantCulture) : null;

        return Required(row, 0) switch
        {
            "spaet" => new TagesHinweis($"Lieferfenster {a} überschritten",
                // Wie im Wireframe: „Nachricht prüfen“ öffnet die Kundenkommunikation (D10). Winston macht erst ab mehr als
                // 15 Minuten einen Vorschlag; bei kleineren Verspätungen führt der Knopf zur Tour.
                $"ETA {b} · {c} Min. nach Fensterende · Tour {d}",
                int.TryParse(c, out int minuten) && minuten > KundenkommunikationRepository.SchwelleMinuten ? "Nachricht prüfen" : "Tour prüfen",
                minuten > KundenkommunikationRepository.SchwelleMinuten ? "D10" : "D07", id),
            "schaden_tour" => new TagesHinweis($"Offener Schaden an {a}",
                $"Fahrzeug fährt heute Tour {b} · {c}", "Meldung prüfen", "D15", id),
            "ohne_zuweisung" => new TagesHinweis($"Tour {a} ohne {d}",
                $"Start {b} · {c}", "Zuweisen", "D06", id),
            "angefragt" => new TagesHinweis(
                $"Neue Web-Anfrage {a} · {Auftraggeber(b)}",
                $"Status „Angefragt“ · Abholung am {DateOnly.ParseExact(c, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd.MM.", Deutsch)}",
                "Auftrag öffnen", "D03", id),
            "unverplant" => new TagesHinweis(
                a == "1" ? "1 Sendung unverplant" : $"{a} Sendungen unverplant",
                $"{b} Stellplätze · {Dezimal(c)} Ldm · {Tausender(d)} kg", "Verplanung prüfen", "D02", null),
            _ => null
        };
    }

    // US-25: Eine neue Sendung in „Seit Ihrer letzten Schicht“. Der Knopf öffnet die Sendung direkt in D03.
    // details = "Auftraggeber|true/false (Webseite)|Status"
    private static SchichtEreignis NeueSendung(DateTime zeit, bool genau, string auftragsnummer, string details, long? id)
    {
        string[] teile = details.Split('|');
        string kunde = Auftraggeber(teile[0]);
        bool webseite = teile.Length > 1 && teile[1] == "true";
        string status = teile.Length > 2 ? teile[2] : "";
        string herkunft = webseite ? "über die Webseite angefragt" : "in der App erfasst";
        return new SchichtEreignis(zeit, genau, $"Neuer Auftrag {auftragsnummer} · {kunde}",
            status.Length > 0 ? $"{herkunft} · Status „{status}“" : herkunft,
            "Auftrag öffnen", "D03", id);
    }

    private static string Auftraggeber(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Auftraggeber unbekannt" : name;

    // "4/6" -> "4 von 6 Stopps erledigt."
    private static string StoppText(string anteil)
    {
        string[] teile = anteil.Split('/');
        if (teile.Length != 2 || teile[1] == "0")
            return "Tour beendet.";
        return teile[0] == teile[1]
            ? $"Alle {teile[1]} Stopps erledigt."
            : $"{teile[0]} von {teile[1]} Stopps erledigt – bitte prüfen.";
    }

    private static string Dezimal(string wert) =>
        decimal.TryParse(wert, NumberStyles.Number, CultureInfo.InvariantCulture, out var zahl) ? zahl.ToString("0.0", Deutsch) : wert;

    private static string Tausender(string wert) =>
        decimal.TryParse(wert, NumberStyles.Number, CultureInfo.InvariantCulture, out var zahl) ? zahl.ToString("N0", Deutsch) : wert;

    private static DateTime Zeit(string text) =>
        DateTime.ParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static int Zahl(string?[] row, int index) => int.Parse(Required(row, index), CultureInfo.InvariantCulture);

    private static string Text(long wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}
