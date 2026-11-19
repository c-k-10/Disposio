using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Die Firmendaten, die Winston (KI-Co-Disponent) lesen darf. Nur SELECT – hier wird nichts geändert.
/// Wie in den anderen Repositories wird zuerst die Rolle Disponent geprüft und danach nur die Firma aus dieser
/// Prüfung verwendet. Jede Methode liefert fertiges JSON mit deutschen Feldnamen und Uhrzeiten in deutscher Zeit;
/// genau dieser Text geht als Werkzeug-Ergebnis an Claude (siehe <see cref="WinstonWerkzeuge"/>).
/// </summary>
public sealed class WinstonDaten
{
    private const FirmenRolle Rolle = FirmenRolle.Disponent;

    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    // Kennzahlen für „Was steht an?“. Gleiche Zählweise wie die Tagesübersicht (D01).
    private const string LageQuery = """
        WITH h AS (SELECT (now() AT TIME ZONE 'Europe/Berlin')::date AS tag)
        SELECT json_build_object(
            'jetzt', to_char(now() AT TIME ZONE 'Europe/Berlin', 'DD.MM.YYYY HH24:MI'),
            'touren_heute', (SELECT count(*) FROM public.tour t
                WHERE t.unternehmen_id = $1::bigint AND (t.startzeit AT TIME ZONE 'Europe/Berlin')::date = h.tag),
            'touren_unterwegs', (SELECT count(*) FROM public.tour t
                WHERE t.unternehmen_id = $1::bigint AND t.status IN ('Unterwegs', 'Pausiert')),
            'touren_ohne_fahrer_oder_fahrzeug', (SELECT count(*) FROM public.tour t
                WHERE t.unternehmen_id = $1::bigint AND (t.fahrer_id IS NULL OR t.fahrzeug_id IS NULL)
                  AND (t.endzeit AT TIME ZONE 'Europe/Berlin')::date >= h.tag
                  AND lower(t.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%'),
            'sendungen_unverplant', (SELECT count(*) FROM public.auftrag a
                WHERE a.unternehmen_id = $1::bigint AND a.tour_id IS NULL
                  AND a.abholdatum <= h.tag AND a.lieferdatum >= h.tag),
            'sendungen_angefragt', (SELECT count(*) FROM public.auftrag a
                WHERE a.unternehmen_id = $1::bigint AND a.status = 'Angefragt'),
            'verspaetete_stopps', (SELECT count(*) FROM public.tourstopp s
                JOIN public.tour t ON t.tour_id = s.tour_id
                WHERE t.unternehmen_id = $1::bigint AND s.status <> 'Erledigt' AND s.aktuelle_eta > s.zeitfenster_bis
                  AND lower(t.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%'),
            'offene_verspaetungsvorschlaege', (SELECT count(*) FROM public.winston_vorschlag v
                JOIN public.auftrag a ON a.auftrag_id = v.auftrag_id
                WHERE a.unternehmen_id = $1::bigint AND v.status = 'offen'),
            'offene_schadensmeldungen', (SELECT count(*) FROM public.schadensmeldung s
                JOIN public.fahrzeug fz ON fz.fahrzeug_id = s.fahrzeug_id
                WHERE fz.unternehmen_id = $1::bigint
                  AND lower(s.status) NOT SIMILAR TO '%(erledigt|abgeschlossen|geschlossen|repariert)%')
        )::text
        FROM h
        """;

    // Laufende Touren (Unterwegs, Pausiert) und alle Touren von heute, mit nächstem offenen Stopp.
    private const string TourenQuery = """
        WITH h AS (SELECT (now() AT TIME ZONE 'Europe/Berlin')::date AS tag)
        SELECT COALESCE(json_agg(x.zeile ORDER BY x.startzeit, x.tournummer), '[]'::json)::text
        FROM (
            SELECT t.startzeit, t.tournummer, json_strip_nulls(json_build_object(
                'tournummer', t.tournummer,
                'status', t.status,
                'abteilung', a.bezeichnung,
                'fahrer', NULLIF(trim(concat_ws(' ', b.vorname, b.nachname)), ''),
                'fahrzeug', f.kennzeichen,
                'start', to_char(t.startzeit AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI'),
                'ende', to_char(t.endzeit AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI'),
                'strecke', t.startort || ' → ' || t.zielort,
                'offene_stopps', (SELECT count(*) FROM public.tourstopp s
                                  WHERE s.tour_id = t.tour_id AND s.status <> 'Erledigt'),
                'naechster_stopp', n.bezeichnung,
                'fenster_ende', to_char(n.zeitfenster_bis AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI'),
                'eta', to_char(n.aktuelle_eta AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI'),
                'verspaetung_min', CASE WHEN n.aktuelle_eta IS NULL THEN NULL
                                        ELSE GREATEST(0, ceil(extract(epoch FROM n.aktuelle_eta - n.zeitfenster_bis) / 60))::int END,
                'letzte_position', (SELECT to_char(max(g.erfasst_am) AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI')
                                    FROM public.gps_position g WHERE g.tour_id = t.tour_id)
            )) AS zeile
            FROM public.tour t
            CROSS JOIN h
            LEFT JOIN public.abteilung a ON a.abteilung_id = t.abteilung_id AND a.unternehmen_id = t.unternehmen_id
            LEFT JOIN public.fahrer fa ON fa.fahrer_id = t.fahrer_id
            LEFT JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id AND b.unternehmen_id = t.unternehmen_id
            LEFT JOIN public.fahrzeug f ON f.fahrzeug_id = t.fahrzeug_id AND f.unternehmen_id = t.unternehmen_id
            LEFT JOIN LATERAL (
                SELECT s.bezeichnung, s.zeitfenster_bis, s.aktuelle_eta
                FROM public.tourstopp s
                WHERE s.tour_id = t.tour_id AND s.status <> 'Erledigt'
                ORDER BY s.reihenfolge, s.stopp_id
                LIMIT 1
            ) n ON true
            WHERE t.unternehmen_id = $1::bigint
              AND (t.status IN ('Unterwegs', 'Pausiert')
                   OR ((t.startzeit AT TIME ZONE 'Europe/Berlin')::date <= h.tag
                       AND (t.endzeit AT TIME ZONE 'Europe/Berlin')::date >= h.tag))
            ORDER BY t.startzeit
            LIMIT 60
        ) x
        """;

    // Eine Tour mit allen Stopps, Zeitfenstern, ETA und Sendungen. $2 = Tour-Id.
    private const string TourQuery = """
        SELECT json_strip_nulls(json_build_object(
            'tournummer', t.tournummer,
            'status', t.status,
            'abteilung', a.bezeichnung,
            'fahrer', NULLIF(trim(concat_ws(' ', b.vorname, b.nachname)), ''),
            'fahrersprache', b.sprache,
            'fahrzeug', f.kennzeichen,
            'fahrzeugtyp', f.fahrzeugtyp,
            'start', to_char(t.startzeit AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI'),
            'ende', to_char(t.endzeit AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI'),
            'strecke', t.startort || ' → ' || t.zielort,
            'letzte_position', (SELECT json_build_object(
                    'zeit', to_char(g.erfasst_am AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI'),
                    'alter_minuten', floor(extract(epoch FROM now() - g.erfasst_am) / 60)::bigint)
                FROM public.gps_position g WHERE g.tour_id = t.tour_id ORDER BY g.erfasst_am DESC LIMIT 1),
            'stopps', (SELECT json_agg(json_strip_nulls(json_build_object(
                    'nr', s.reihenfolge,
                    'art', s.art,
                    'ort', s.bezeichnung,
                    'adresse', s.adresse,
                    'status', s.status,
                    'fenster', to_char(s.zeitfenster_von AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI') || ' bis '
                               || to_char(s.zeitfenster_bis AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
                    'eta', to_char(s.aktuelle_eta AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI'),
                    'eta_berechnet', to_char(s.eta_berechnet_am AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI'),
                    'verspaetung_min', CASE WHEN s.status = 'Erledigt' OR s.aktuelle_eta IS NULL THEN NULL
                                            ELSE GREATEST(0, ceil(extract(epoch FROM s.aktuelle_eta - s.zeitfenster_bis) / 60))::int END,
                    'ankunft', to_char(s.ist_ankunft AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI'),
                    'abfahrt', to_char(s.ist_abfahrt AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI'),
                    'sendungen', (SELECT json_agg(json_build_object(
                            'nummer', x.auftragsnummer, 'kunde', k.firmenname, 'vorgang', sa.vorgang_art,
                            'gut', x.gutart, 'anzahl', x.anzahl, 'gewicht_kg', x.gewicht_kg))
                        FROM public.stopp_auftrag sa
                        JOIN public.auftrag x ON x.auftrag_id = sa.auftrag_id AND x.unternehmen_id = t.unternehmen_id
                        JOIN public.kunde k ON k.kunde_id = x.kunde_id
                        WHERE sa.stopp_id = s.stopp_id)
                )) ORDER BY s.reihenfolge, s.stopp_id)
                FROM public.tourstopp s WHERE s.tour_id = t.tour_id)
        ))::text
        FROM public.tour t
        LEFT JOIN public.abteilung a ON a.abteilung_id = t.abteilung_id AND a.unternehmen_id = t.unternehmen_id
        LEFT JOIN public.fahrer fa ON fa.fahrer_id = t.fahrer_id
        LEFT JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id AND b.unternehmen_id = t.unternehmen_id
        LEFT JOIN public.fahrzeug f ON f.fahrzeug_id = t.fahrzeug_id AND f.unternehmen_id = t.unternehmen_id
        WHERE t.unternehmen_id = $1::bigint AND t.tour_id = $2::bigint
        """;

    // Alle offenen Stopps laufender Touren, deren ETA nach dem Ende des Zeitfensters liegt. Größte Verspätung zuerst.
    private const string VerspaetungenQuery = """
        SELECT COALESCE(json_agg(x.zeile ORDER BY x.minuten DESC), '[]'::json)::text
        FROM (
            SELECT ceil(extract(epoch FROM s.aktuelle_eta - s.zeitfenster_bis) / 60)::int AS minuten,
                   json_strip_nulls(json_build_object(
                'tournummer', t.tournummer,
                'tourstatus', t.status,
                'fahrer', NULLIF(trim(concat_ws(' ', b.vorname, b.nachname)), ''),
                'stopp', s.bezeichnung,
                'art', s.art,
                'fenster_ende', to_char(s.zeitfenster_bis AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI'),
                'eta', to_char(s.aktuelle_eta AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI'),
                'verspaetung_min', ceil(extract(epoch FROM s.aktuelle_eta - s.zeitfenster_bis) / 60)::int,
                'eta_berechnet', to_char(s.eta_berechnet_am AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI'),
                'kunden', (SELECT string_agg(DISTINCT k.firmenname, ', ')
                           FROM public.stopp_auftrag sa
                           JOIN public.auftrag x ON x.auftrag_id = sa.auftrag_id AND x.unternehmen_id = t.unternehmen_id
                           JOIN public.kunde k ON k.kunde_id = x.kunde_id
                           WHERE sa.stopp_id = s.stopp_id),
                -- Klartext statt Statuswert, damit die KI „freigegeben“ nicht als „wartet auf Freigabe“ versteht.
                'kundeninformation', (SELECT CASE
                        WHEN bool_or(v.status = 'offen') THEN 'Vorschlag liegt in der Kundenkommunikation (D10) und wartet auf Freigabe'
                        WHEN bool_or(v.status = 'freigegeben') THEN 'Kunde und Fahrer wurden zu dieser Verspätung schon informiert'
                        WHEN bool_or(v.status = 'verworfen') THEN 'Ein Vorschlag dazu wurde vom Disponenten verworfen'
                        ELSE 'Noch kein Vorschlag (entsteht ab mehr als 15 Minuten Verspätung)' END
                    FROM public.winston_vorschlag v WHERE v.stopp_id = s.stopp_id)
            )) AS zeile
            FROM public.tourstopp s
            JOIN public.tour t ON t.tour_id = s.tour_id
            LEFT JOIN public.fahrer fa ON fa.fahrer_id = t.fahrer_id
            LEFT JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id AND b.unternehmen_id = t.unternehmen_id
            WHERE t.unternehmen_id = $1::bigint AND s.status <> 'Erledigt'
              AND s.aktuelle_eta IS NOT NULL AND s.aktuelle_eta > s.zeitfenster_bis
              AND lower(t.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%'
            ORDER BY 1 DESC
            LIMIT 40
        ) x
        """;

    // Sendungen der Firma. $2 = true: nur Sendungen ohne Tour. Sonst alle, die noch nicht lange zurückliegen.
    private const string SendungenQuery = """
        WITH h AS (SELECT (now() AT TIME ZONE 'Europe/Berlin')::date AS tag)
        SELECT COALESCE(json_agg(x.zeile ORDER BY x.lieferdatum, x.auftragsnummer), '[]'::json)::text
        FROM (
            SELECT a.lieferdatum, a.auftragsnummer, json_strip_nulls(json_build_object(
                'nummer', a.auftragsnummer,
                'kunde', k.firmenname,
                'status', a.status,
                'prioritaet', a.prioritaet,
                'tour', t.tournummer,
                'abholung', a.abholadresse || ' · ' || to_char(a.abholdatum, 'DD.MM.') || ' '
                            || to_char(a.abholfenster_von, 'HH24:MI') || '–' || to_char(a.abholfenster_bis, 'HH24:MI'),
                'lieferung', a.lieferadresse || ' · ' || to_char(a.lieferdatum, 'DD.MM.') || ' '
                             || to_char(a.lieferfenster_von, 'HH24:MI') || '–' || to_char(a.lieferfenster_bis, 'HH24:MI'),
                'gut', a.gutart,
                'ladung', a.anzahl || ' × ' || a.verpackungstyp,
                'gewicht_kg', a.gewicht_kg,
                'lademeter', round(a.anzahl * a.laenge_m * a.breite_m / 2.4, 1),
                'gefahrgut', CASE WHEN a.gefahrgut THEN true END,
                'temperatur', CASE WHEN a.temperatur_min_c IS NOT NULL OR a.temperatur_max_c IS NOT NULL
                                   THEN concat_ws(' bis ', a.temperatur_min_c, a.temperatur_max_c) || ' °C' END
            )) AS zeile
            FROM public.auftrag a
            CROSS JOIN h
            JOIN public.kunde k ON k.kunde_id = a.kunde_id
            LEFT JOIN public.tour t ON t.tour_id = a.tour_id AND t.unternehmen_id = a.unternehmen_id
            WHERE a.unternehmen_id = $1::bigint
              AND CASE WHEN $2::boolean THEN a.tour_id IS NULL ELSE a.tour_id IS NULL OR a.lieferdatum >= h.tag - 2 END
            ORDER BY a.lieferdatum, a.auftragsnummer
            LIMIT 60
        ) x
        """;

    // Fahrzeuge und Fahrer der Firma, jeweils mit der Tour, auf der sie gerade unterwegs sind.
    private const string FuhrparkQuery = """
        SELECT json_build_object(
            'fahrzeuge', (SELECT COALESCE(json_agg(json_strip_nulls(json_build_object(
                    'kennzeichen', f.kennzeichen, 'typ', f.fahrzeugtyp, 'aufbau', f.aufbau, 'status', f.status,
                    'nutzlast_kg', f.nutzlast_kg, 'lademeter', f.nutzbare_lademeter, 'stellplaetze', f.ep_stellplaetze,
                    'solltemperatur_c', f.solltemperatur_c, 'abteilung', a.bezeichnung,
                    'tuev_bis', to_char(f.tuev_datum, 'DD.MM.YYYY'),
                    'unterwegs_auf', (SELECT string_agg(t.tournummer, ', ') FROM public.tour t
                                      WHERE t.fahrzeug_id = f.fahrzeug_id AND t.status IN ('Unterwegs', 'Pausiert'))
                )) ORDER BY f.kennzeichen), '[]'::json)
                FROM public.fahrzeug f
                LEFT JOIN public.abteilung a ON a.abteilung_id = f.abteilung_id AND a.unternehmen_id = f.unternehmen_id
                WHERE f.unternehmen_id = $1::bigint AND f.aktiv),
            'fahrer', (SELECT COALESCE(json_agg(json_strip_nulls(json_build_object(
                    'name', trim(concat_ws(' ', b.vorname, b.nachname)), 'sprache', b.sprache,
                    'verfuegbarkeit', fa.verfuegbarkeit, 'fuehrerscheinklasse', fa.fuehrerscheinklasse,
                    'unterwegs_auf', (SELECT string_agg(t.tournummer, ', ') FROM public.tour t
                                      WHERE t.fahrer_id = fa.fahrer_id AND t.status IN ('Unterwegs', 'Pausiert'))
                )) ORDER BY b.nachname, b.vorname), '[]'::json)
                FROM public.fahrer fa
                JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
                WHERE b.unternehmen_id = $1::bigint AND b.aktiv)
        )::text
        """;

    // Schadensmeldungen (offene und die der letzten 14 Tage) und Tankvorgänge der letzten 14 Tage.
    private const string MeldungenQuery = """
        SELECT json_build_object(
            'schadensmeldungen', (SELECT COALESCE(json_agg(json_strip_nulls(json_build_object(
                    'datum', to_char(s.datum, 'DD.MM.YYYY'), 'art', s.schadensart, 'beschreibung', s.beschreibung,
                    'status', s.status, 'kosten_eur', s.kosten, 'notiz', s.bearbeitungsnotiz,
                    'fahrzeug', fz.kennzeichen, 'fahrzeugstatus', fz.status,
                    'fahrer', trim(concat_ws(' ', b.vorname, b.nachname))
                )) ORDER BY s.datum DESC), '[]'::json)
                FROM public.schadensmeldung s
                JOIN public.fahrzeug fz ON fz.fahrzeug_id = s.fahrzeug_id
                LEFT JOIN public.fahrer fa ON fa.fahrer_id = s.fahrer_id
                LEFT JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id AND b.unternehmen_id = fz.unternehmen_id
                WHERE fz.unternehmen_id = $1::bigint
                  AND (s.datum >= current_date - 14
                       OR lower(s.status) NOT SIMILAR TO '%(erledigt|abgeschlossen|geschlossen|repariert)%')),
            'tankvorgaenge', (SELECT COALESCE(json_agg(json_strip_nulls(json_build_object(
                    'datum', to_char(tv.datum, 'DD.MM.YYYY'), 'liter', tv.liter, 'kosten_eur', tv.kosten,
                    'tankstelle', tv.tankstelle, 'status', tv.bearbeitungsstatus, 'fahrzeug', fz.kennzeichen,
                    'fahrer', trim(concat_ws(' ', b.vorname, b.nachname))
                )) ORDER BY tv.datum DESC), '[]'::json)
                FROM public.tankvorgang tv
                JOIN public.fahrzeug fz ON fz.fahrzeug_id = tv.fahrzeug_id
                LEFT JOIN public.fahrer fa ON fa.fahrer_id = tv.fahrer_id
                LEFT JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id AND b.unternehmen_id = fz.unternehmen_id
                WHERE fz.unternehmen_id = $1::bigint AND tv.datum >= current_date - 14)
        )::text
        """;

    // Die letzten 20 Chat-Nachrichten einer Tour. Anders als D09 markiert das nichts als gelesen. $2 = Tour-Id.
    private const string NachrichtenQuery = """
        SELECT COALESCE(json_agg(x.zeile ORDER BY x.zeitstempel), '[]'::json)::text
        FROM (
            SELECT n.zeitstempel, json_strip_nulls(json_build_object(
                'zeit', to_char(n.zeitstempel AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI'),
                'von', CASE WHEN n.benutzer_id = c.fahrer_benutzer_id THEN 'Fahrer' ELSE 'Disposition' END,
                'text', n.nachrichtentext,
                'sprache', n.originalsprache,
                'uebersetzung', n.uebersetzungstext,
                'gelesen', n.gelesen
            )) AS zeile
            FROM public.chat c
            JOIN public.tour t ON t.tour_id = c.tour_id
            JOIN public.nachricht n ON n.chat_id = c.chat_id
            WHERE t.unternehmen_id = $1::bigint AND t.tour_id = $2::bigint
            ORDER BY n.zeitstempel DESC
            LIMIT 20
        ) x
        """;

    // Reststrecke für die Verkehrslage: neueste Position (ohne Position: letzter erledigter Stopp), danach die
    // offenen Stopps mit Koordinaten in Reihenfolge. $2 = Tour-Id.
    private const string RoutenpunkteQuery = """
        WITH pos AS (
            SELECT g.breitengrad, g.laengengrad,
                   'Letzte Position (' || to_char(g.erfasst_am AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI') || ')' AS bez
            FROM public.gps_position g
            WHERE g.tour_id = $2::bigint
            ORDER BY g.erfasst_am DESC
            LIMIT 1
        ), letzter AS (
            SELECT s.breitengrad, s.laengengrad, 'Letzter erledigter Stopp: ' || s.bezeichnung AS bez
            FROM public.tourstopp s
            WHERE s.tour_id = $2::bigint AND s.status = 'Erledigt'
              AND s.breitengrad IS NOT NULL AND s.laengengrad IS NOT NULL
            ORDER BY s.reihenfolge DESC
            LIMIT 1
        )
        SELECT x.breitengrad, x.laengengrad, x.bez
        FROM (
            SELECT 0 AS rang, 0 AS nr, breitengrad, laengengrad, bez FROM pos
            UNION ALL
            SELECT 0, 0, breitengrad, laengengrad, bez FROM letzter WHERE NOT EXISTS (SELECT 1 FROM pos)
            UNION ALL
            SELECT 1, s.reihenfolge, s.breitengrad, s.laengengrad, s.bezeichnung
            FROM public.tourstopp s
            JOIN public.tour t ON t.tour_id = s.tour_id
            WHERE t.unternehmen_id = $1::bigint AND s.tour_id = $2::bigint AND s.status <> 'Erledigt'
              AND s.breitengrad IS NOT NULL AND s.laengengrad IS NOT NULL
        ) x
        ORDER BY x.rang, x.nr
        """;

    public Task<string> LageAsync(CancellationToken cancellationToken = default) =>
        JsonAsync(LageQuery, null, cancellationToken);

    public Task<string> TourenAsync(CancellationToken cancellationToken = default) =>
        JsonAsync(TourenQuery, null, cancellationToken);

    /// <exception cref="ArgumentException">Die Tour gibt es in dieser Firma nicht.</exception>
    public async Task<string> TourAsync(string tournummer, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        long tourId = await TourIdAsync(zugriff, tournummer, cancellationToken);
        return await JsonAsync(zugriff, TourQuery, Text(tourId), cancellationToken);
    }

    public Task<string> VerspaetungenAsync(CancellationToken cancellationToken = default) =>
        JsonAsync(VerspaetungenQuery, null, cancellationToken);

    public Task<string> SendungenAsync(bool nurUnverplant, CancellationToken cancellationToken = default) =>
        JsonAsync(SendungenQuery, nurUnverplant ? "true" : "false", cancellationToken);

    public Task<string> FuhrparkAsync(CancellationToken cancellationToken = default) =>
        JsonAsync(FuhrparkQuery, null, cancellationToken);

    public Task<string> MeldungenAsync(CancellationToken cancellationToken = default) =>
        JsonAsync(MeldungenQuery, null, cancellationToken);

    /// <exception cref="ArgumentException">Die Tour gibt es in dieser Firma nicht.</exception>
    public async Task<string> NachrichtenAsync(string tournummer, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        long tourId = await TourIdAsync(zugriff, tournummer, cancellationToken);
        return await JsonAsync(zugriff, NachrichtenQuery, Text(tourId), cancellationToken);
    }

    /// <summary>Die Punkte der Reststrecke einer Tour für <see cref="TomTomVerkehr"/>.</summary>
    /// <exception cref="ArgumentException">Die Tour gibt es in dieser Firma nicht.</exception>
    public async Task<IReadOnlyList<RoutenPunkt>> RoutenpunkteAsync(string tournummer, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        long tourId = await TourIdAsync(zugriff, tournummer, cancellationToken);
        var rows = await _client.Value.QueryAsync(RoutenpunkteQuery,
            new string?[] { Text(zugriff.UnternehmenId), Text(tourId) }, cancellationToken);
        return rows.Select(r => new RoutenPunkt(
            double.Parse(Req(r, 0), CultureInfo.InvariantCulture),
            double.Parse(Req(r, 1), CultureInfo.InvariantCulture),
            Req(r, 2))).ToArray();
    }

    // Tournummern sind je Firma eindeutig gemeint; Groß-/Kleinschreibung und Leerzeichen am Rand zählen nicht.
    private async Task<long> TourIdAsync(FirmenZugriff zugriff, string tournummer, CancellationToken cancellationToken)
    {
        string nummer = (tournummer ?? "").Trim();
        if (nummer.Length is 0 or > 100)
            throw new ArgumentException("Die Tournummer fehlt.");
        var rows = await _client.Value.QueryAsync("""
            SELECT t.tour_id FROM public.tour t
            WHERE t.unternehmen_id = $1::bigint AND lower(btrim(t.tournummer)) = lower($2)
            ORDER BY t.startzeit DESC
            LIMIT 1
            """, new string?[] { Text(zugriff.UnternehmenId), nummer }, cancellationToken);
        if (rows.Count != 1)
            throw new ArgumentException($"Die Tour „{nummer}“ gibt es nicht. Die gültigen Tournummern stehen in touren_liste.");
        return long.Parse(Req(rows[0], 0), CultureInfo.InvariantCulture);
    }

    private async Task<string> JsonAsync(string query, string? zweiter, CancellationToken cancellationToken)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        return await JsonAsync(zugriff, query, zweiter, cancellationToken);
    }

    // Jede Abfrage liefert genau eine Zeile mit einer Spalte: das fertige JSON.
    private async Task<string> JsonAsync(FirmenZugriff zugriff, string query, string? zweiter, CancellationToken cancellationToken)
    {
        string?[] parameter = zweiter is null
            ? new string?[] { Text(zugriff.UnternehmenId) }
            : new string?[] { Text(zugriff.UnternehmenId), zweiter };
        var rows = await _client.Value.QueryAsync(query, parameter, cancellationToken);
        if (rows.Count == 0 || rows[0][0] is not string json)
            throw new ArgumentException("Dazu gibt es keine Daten.");
        return json;
    }

    private static string Text(long wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Req(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}
