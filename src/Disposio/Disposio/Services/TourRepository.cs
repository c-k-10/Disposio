using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Touren für die Ansichten des Disponenten (D04, D05, D06, D07, D16 …). Wie in den anderen Repositories wird zuerst die Rolle
/// Disponent geprüft und danach nur die Firma aus dieser Prüfung verwendet, nie eine Id aus der Oberfläche.
/// Fahrer: tour.fahrer_id zeigt auf public.fahrer, der Name steht im verknüpften public.benutzer.
/// </summary>
public sealed class TourRepository
{
    private const FirmenRolle Rolle = FirmenRolle.Disponent;
    private const string Zeitzone = "Europe/Berlin";

    // Erst beim ersten Zugriff verbinden, wie im UnternehmenRepository.
    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    // ── D04: Tourenübersicht ─────────────────────────────────────────────────────────

    /// <summary>
    /// Alle Touren der Firma, die an diesem Tag (deutsche Zeit) laufen: Start am oder vor dem Tag,
    /// Ende am oder nach dem Tag. Sortiert nach Startzeit.
    /// </summary>
    public async Task<IReadOnlyList<TourUebersicht>> TagLadenAsync(DateOnly tag, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);

        // Fahrer, Fahrzeug und Abteilung nur, wenn sie zur selben Firma gehören.
        // Nächster Stopp = erster Stopp (nach Reihenfolge) ohne Ist-Ankunft.
        // Uhrzeiten formatiert die Datenbank, damit Zeitzone und Sommerzeit überall gleich behandelt werden.
        const string query = """
            SELECT t.tour_id, t.tournummer, a.bezeichnung, t.status,
                   NULLIF(trim(concat_ws(' ', b.vorname, b.nachname)), ''), f.kennzeichen,
                   n.bezeichnung,
                   to_char(n.zeitfenster_von AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
                   to_char(n.zeitfenster_bis AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
                   to_char(COALESCE(n.aktuelle_eta, n.geplante_ankunft) AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
                   COALESCE(COALESCE(n.aktuelle_eta, n.geplante_ankunft) > n.zeitfenster_bis, false),
                   t.fahrer_id IS NOT NULL, t.fahrzeug_id IS NOT NULL,
                   t.startort, t.zielort
            FROM public.tour t
            LEFT JOIN public.abteilung a ON a.abteilung_id = t.abteilung_id AND a.unternehmen_id = t.unternehmen_id
            LEFT JOIN public.fahrer fa ON fa.fahrer_id = t.fahrer_id
            LEFT JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id AND b.unternehmen_id = t.unternehmen_id
            LEFT JOIN public.fahrzeug f ON f.fahrzeug_id = t.fahrzeug_id AND f.unternehmen_id = t.unternehmen_id
            LEFT JOIN LATERAL (
                SELECT s.bezeichnung, s.zeitfenster_von, s.zeitfenster_bis, s.aktuelle_eta, s.geplante_ankunft
                FROM public.tourstopp s
                WHERE s.tour_id = t.tour_id AND s.ist_ankunft IS NULL
                ORDER BY s.reihenfolge, s.stopp_id
                LIMIT 1
            ) n ON true
            WHERE t.unternehmen_id = $1::bigint
              AND (t.startzeit AT TIME ZONE 'Europe/Berlin')::date <= $2::date
              AND (t.endzeit AT TIME ZONE 'Europe/Berlin')::date >= $2::date
            ORDER BY t.startzeit, t.tournummer, t.tour_id
            """;
        var rows = await _client.Value.QueryAsync(query, new string?[]
        {
            Text(zugriff.UnternehmenId),
            tag.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        }, cancellationToken);

        var touren = new List<TourUebersicht>();
        foreach (var row in rows)
        {
            touren.Add(new TourUebersicht(
                long.Parse(Required(row, 0), CultureInfo.InvariantCulture),
                Required(row, 1),
                row[2],
                Required(row, 3),
                row[4],
                row[5],
                row[6],
                row[7],
                row[8],
                row[9],
                // Neon liefert Wahrheitswerte als Text: t oder f.
                row[10] == "t",
                row[11] == "t",
                row[12] == "t",
                Required(row, 13),
                Required(row, 14)));
        }

        return touren;
    }

    // ── D05: Tour planen ─────────────────────────────────────────────────────────────

    /// <summary>Abteilungen, Fahrer, Fahrzeuge, offene Sendungen und ein Vorschlag für die Tournummer.</summary>
    public async Task<TourPlanungsdaten> PlanungsdatenLadenAsync(CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var firma = new string?[] { Text(zugriff.UnternehmenId) };

        var abteilungen = (await _client.Value.QueryAsync(
                // Nur aktive Abteilungen (K05). Voraussetzung: 006_abteilung_aktiv.sql.
                "SELECT abteilung_id, bezeichnung FROM public.abteilung WHERE unternehmen_id = $1::bigint AND aktiv ORDER BY lower(bezeichnung)",
                firma, cancellationToken))
            .Select(row => new AbteilungAuswahl(Id(row, 0), Required(row, 1)))
            .ToArray();

        // Nur aktive Konten derselben Firma.
        var fahrer = (await _client.Value.QueryAsync("""
                SELECT fa.fahrer_id, trim(concat_ws(' ', b.vorname, b.nachname)), fa.verfuegbarkeit
                FROM public.fahrer fa
                JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
                WHERE b.unternehmen_id = $1::bigint AND b.aktiv = true
                ORDER BY lower(b.nachname), lower(b.vorname), fa.fahrer_id
                """, firma, cancellationToken))
            .Select(row => new FahrerAuswahl(Id(row, 0), Required(row, 1), row[2] ?? ""))
            .ToArray();

        var fahrzeuge = (await _client.Value.QueryAsync("""
                SELECT fahrzeug_id, kennzeichen, fahrzeugtyp, status
                FROM public.fahrzeug WHERE unternehmen_id = $1::bigint
                ORDER BY kennzeichen, fahrzeug_id
                """, firma, cancellationToken))
            .Select(row => new FahrzeugAuswahl(Id(row, 0), Required(row, 1), Required(row, 2), Required(row, 3)))
            .ToArray();

        // Offen = noch keiner Tour zugeordnet. Sortiert nach Abholung.
        var sendungen = (await _client.Value.QueryAsync("""
                SELECT a.auftrag_id, a.auftragsnummer, COALESCE(k.firmenname, ''), a.abholadresse, a.lieferadresse,
                       to_char(a.abholdatum, 'YYYY-MM-DD'), to_char(a.abholfenster_von, 'HH24:MI'), to_char(a.abholfenster_bis, 'HH24:MI'),
                       to_char(a.lieferdatum, 'YYYY-MM-DD'), to_char(a.lieferfenster_von, 'HH24:MI'), to_char(a.lieferfenster_bis, 'HH24:MI'),
                       a.prioritaet
                FROM public.auftrag a
                LEFT JOIN public.kunde k ON k.kunde_id = a.kunde_id AND k.unternehmen_id = a.unternehmen_id
                WHERE a.unternehmen_id = $1::bigint AND a.tour_id IS NULL
                ORDER BY a.abholdatum, a.abholfenster_von, a.auftragsnummer
                """, firma, cancellationToken))
            .Select(row => new OffeneSendung(
                Id(row, 0), Required(row, 1), Required(row, 2), Required(row, 3), Required(row, 4),
                Datum(row, 5), Uhrzeit(row, 6), Uhrzeit(row, 7),
                Datum(row, 8), Uhrzeit(row, 9), Uhrzeit(row, 10),
                Required(row, 11)))
            .ToArray();

        // Vorschlag: höchste Nummer der Form „T-2401“ plus 1. Ohne solche Touren beginnt es bei T-2401.
        var nummer = await _client.Value.QueryAsync("""
                SELECT COALESCE(max(substring(tournummer FROM '^T-([0-9]{1,9})$')::bigint), 2400) + 1
                FROM public.tour WHERE unternehmen_id = $1::bigint
                """, firma, cancellationToken);
        string vorschlag = "T-" + Required(nummer[0], 0);

        // Adresse der eigenen Firma („Straße, PLZ Ort“) als Vorschlag für Start- und Zielort in D05.
        string? firmenadresse = null;
        try
        {
            var adresse = await _client.Value.QueryAsync("""
                    SELECT NULLIF(concat_ws(', ', NULLIF(btrim(adresse), ''),
                           NULLIF(btrim(concat_ws(' ', NULLIF(btrim(plz), ''), NULLIF(btrim(ort), ''))), '')), '')
                    FROM public.unternehmen WHERE unternehmen_id = $1::bigint
                    """, firma, cancellationToken);
            firmenadresse = adresse.Count == 1 ? adresse[0][0] : null;
        }
        catch (NeonSqlException)
        {
            // Nur ein Vorschlag: Ohne Firmenadresse bleiben Start- und Zielort einfach leer.
        }

        return new TourPlanungsdaten(abteilungen, fahrer, fahrzeuge, sendungen, vorschlag) { Firmenadresse = firmenadresse };
    }

    /// <summary>
    /// Legt Tour, Stopps, Verknüpfungen Stopp–Sendung und einen Protokolleintrag in einem einzigen Befehl an:
    /// entweder alles oder nichts. Die Sendungen werden der neuen Tour zugeordnet.
    /// </summary>
    /// <returns>Die Id der neuen Tour.</returns>
    /// <exception cref="ArgumentException">Ungültige Eingaben oder Daten gehören nicht (mehr) zur Firma.</exception>
    /// <exception cref="TournummerVergebenException">Die Tournummer gibt es in der Firma schon.</exception>
    public async Task<long> AnlegenAsync(TourEntwurf entwurf, CancellationToken cancellationToken = default)
    {
        // Erst die Eingaben (ohne Datenbank), dann die Rechte (mit Datenbank) prüfen.
        TourEntwurf eingaben = Pruefen(entwurf);
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);

        long[] auftraege = eingaben.Stopps.Where(s => s.AuftragId is not null)
            .Select(s => s.AuftragId!.Value).Distinct().ToArray();
        string status = eingaben.FahrerId is not null && eingaben.FahrzeugId is not null
            ? TourWerte.StatusZugewiesen
            : TourWerte.StatusGeplant;

        // Stopps als JSON-Liste. Zeiten als deutsche Ortszeit ohne Zone; die Datenbank rechnet sie um.
        string stoppsJson = JsonSerializer.Serialize(eingaben.Stopps.Select((s, index) => new
        {
            reihenfolge = index + 1,
            art = s.Art,
            bezeichnung = s.Bezeichnung,
            adresse = s.Adresse,
            von = Lokal(s.Von),
            bis = Lokal(s.Bis),
            auftrag_id = s.AuftragId,
            vorgang = s.Vorgang
        }));

        // „pruefung“ liefert nur dann eine Zeile, wenn Abteilung, Fahrer, Fahrzeug und alle Sendungen zur Firma gehören
        // und die Sendungen noch frei sind. Ohne diese Zeile wird nichts gespeichert.
        const string query = """
            WITH eingabe AS (
                SELECT * FROM jsonb_to_recordset($11::jsonb)
                    AS s(reihenfolge integer, art text, bezeichnung text, adresse text,
                         von timestamp, bis timestamp, auftrag_id bigint, vorgang text)
            ), pruefung AS (
                SELECT 1 AS ok
                WHERE EXISTS (SELECT 1 FROM public.abteilung a
                              WHERE a.abteilung_id = $3::bigint AND a.unternehmen_id = $1::bigint AND a.aktiv)
                  AND ($8::bigint IS NULL OR EXISTS (
                        SELECT 1 FROM public.fahrer fa JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
                        WHERE fa.fahrer_id = $8::bigint AND b.unternehmen_id = $1::bigint AND b.aktiv = true))
                  AND ($9::bigint IS NULL OR EXISTS (
                        SELECT 1 FROM public.fahrzeug f WHERE f.fahrzeug_id = $9::bigint AND f.unternehmen_id = $1::bigint))
                  -- Fahrer und Fahrzeug dürfen im Zeitraum der neuen Tour keine andere offene Tour haben.
                  AND NOT EXISTS (
                        SELECT 1 FROM public.tour x
                        WHERE x.unternehmen_id = $1::bigint
                          AND (x.fahrer_id = $8::bigint OR x.fahrzeug_id = $9::bigint)
                          AND x.startzeit < $5::timestamp AT TIME ZONE 'Europe/Berlin'
                          AND x.endzeit > $4::timestamp AT TIME ZONE 'Europe/Berlin'
                          AND lower(x.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%')
                  AND (SELECT count(*) FROM public.auftrag x
                       WHERE x.auftrag_id = ANY($12::bigint[]) AND x.unternehmen_id = $1::bigint AND x.tour_id IS NULL)
                      = cardinality($12::bigint[])
            ), neu AS (
                INSERT INTO public.tour (tournummer, startzeit, endzeit, startort, zielort, status,
                                         unternehmen_id, abteilung_id, disponent_benutzer_id, fahrer_id, fahrzeug_id)
                SELECT $2, $4::timestamp AT TIME ZONE 'Europe/Berlin', $5::timestamp AT TIME ZONE 'Europe/Berlin',
                       $6, $7, $10, $1::bigint, $3::bigint, $13::bigint, $8::bigint, $9::bigint
                FROM pruefung
                RETURNING tour_id, tournummer, unternehmen_id, abteilung_id
            ), stopps AS (
                INSERT INTO public.tourstopp (tour_id, reihenfolge, art, bezeichnung, adresse,
                                              zeitfenster_von, zeitfenster_bis, status)
                SELECT n.tour_id, e.reihenfolge, e.art, e.bezeichnung, e.adresse,
                       e.von AT TIME ZONE 'Europe/Berlin', e.bis AT TIME ZONE 'Europe/Berlin', $14
                FROM neu n CROSS JOIN eingabe e
                RETURNING stopp_id, reihenfolge
            ), verknuepfung AS (
                INSERT INTO public.stopp_auftrag (stopp_id, auftrag_id, vorgang_art)
                SELECT s.stopp_id, e.auftrag_id, e.vorgang
                FROM stopps s JOIN eingabe e ON e.reihenfolge = s.reihenfolge
                WHERE e.auftrag_id IS NOT NULL
            ), sendungen AS (
                UPDATE public.auftrag x SET tour_id = n.tour_id
                FROM neu n
                WHERE x.auftrag_id = ANY($12::bigint[]) AND x.unternehmen_id = $1::bigint AND x.tour_id IS NULL
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
                SELECT CASE WHEN $13::bigint IS NULL THEN 'Entwicklungsansicht' ELSE 'Benutzer' END,
                       'Tour angelegt', 'Gespeichert', 'Tour ' || n.tournummer,
                       n.unternehmen_id, n.abteilung_id, $13::bigint
                FROM neu n
            )
            SELECT tour_id FROM neu
            """;
        var parameters = new string?[]
        {
            Text(zugriff.UnternehmenId),                              // $1
            eingaben.Tournummer,                                       // $2
            Text(eingaben.AbteilungId),                                // $3
            Lokal(eingaben.Start),                                     // $4
            Lokal(eingaben.Ende),                                      // $5
            eingaben.Startort,                                         // $6
            eingaben.Zielort,                                          // $7
            eingaben.FahrerId is long fahrerId ? Text(fahrerId) : null,           // $8
            eingaben.FahrzeugId is long fahrzeugId ? Text(fahrzeugId) : null,     // $9
            status,                                                    // $10
            stoppsJson,                                                // $11
            "{" + string.Join(",", auftraege.Select(Text)) + "}",      // $12
            // Wer plant: das angemeldete Konto (beim Admin-Zugang der MegakrasserAdmin selbst).
            BenutzerSitzung.AktuellerBenutzer is { } benutzer ? Text(benutzer.Id) : null,  // $13
            TourWerte.StoppStatusOffen                                 // $14
        };

        try
        {
            var rows = await _client.Value.QueryAsync(query, parameters, cancellationToken);
            if (rows.Count == 1)
                return Id(rows[0], 0);

            // Keine Zeile: Die Prüfung oben ist fehlgeschlagen.
            throw new ArgumentException("Die Tour konnte nicht gespeichert werden: Eine Sendung wurde inzwischen verplant, " +
                                        "Fahrer oder Fahrzeug haben in diesem Zeitraum schon eine andere Tour, oder Abteilung, " +
                                        "Fahrer bzw. Fahrzeug gibt es nicht mehr. Bitte laden Sie das Formular neu.");
        }
        catch (NeonSqlException exception) when (exception.Code == "23505" &&
            exception.ConstraintName == "tour_unternehmen_tournummer_uq")
        {
            // Die Datenbank prüft eindeutige Tournummern je Firma, auch bei zwei gleichzeitigen Speichervorgängen.
            throw new TournummerVergebenException(exception);
        }
    }

    // ── D16: Sendung zuweisen ────────────────────────────────────────────────────────

    /// <summary>
    /// Die Sendung, alle Fahrer und Fahrzeuge, die im Zeitraum der Sendung frei sind, aktive Abteilungen und
    /// ein Vorschlag für die Tournummer. Frei = keine andere offene Tour, die sich mit Abholung bis Zustellung
    /// überschneidet. Fahrer nicht im Urlaub/krank, Fahrzeug nicht in Wartung oder gesperrt.
    /// </summary>
    /// <exception cref="ArgumentException">Die Sendung gibt es in dieser Firma nicht.</exception>
    public async Task<ZuweisungDaten> ZuweisungLadenAsync(long auftragId, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var ids = new string?[] { Text(zugriff.UnternehmenId), Text(auftragId) };

        var rows = await _client.Value.QueryAsync("""
            SELECT a.auftrag_id, a.auftragsnummer, COALESCE(NULLIF(a.kundenreferenz, ''), a.auftragsnummer), COALESCE(k.firmenname, ''),
                   a.abholadresse, to_char(a.abholdatum, 'YYYY-MM-DD'), to_char(a.abholfenster_von, 'HH24:MI'), to_char(a.abholfenster_bis, 'HH24:MI'),
                   a.lieferadresse, to_char(a.lieferdatum, 'YYYY-MM-DD'), to_char(a.lieferfenster_von, 'HH24:MI'), to_char(a.lieferfenster_bis, 'HH24:MI'),
                   a.gutart, a.verpackungstyp, a.anzahl, a.gewicht_kg, round(a.anzahl * a.laenge_m * a.breite_m / 2.4, 1),
                   a.temperatur_min_c, a.temperatur_max_c, a.gefahrgut, a.prioritaet, t.tournummer
            FROM public.auftrag a
            LEFT JOIN public.kunde k ON k.kunde_id = a.kunde_id AND k.unternehmen_id = a.unternehmen_id
            LEFT JOIN public.tour t ON t.tour_id = a.tour_id AND t.unternehmen_id = a.unternehmen_id
            WHERE a.unternehmen_id = $1::bigint AND a.auftrag_id = $2::bigint
            """, ids, cancellationToken);
        if (rows.Count != 1)
            throw new ArgumentException("Diese Sendung gibt es nicht (mehr).");
        var r = rows[0];
        var sendung = new ZuweisungSendung(
            Id(r, 0), Required(r, 1), Required(r, 2), Required(r, 3),
            Required(r, 4), Datum(r, 5), Uhrzeit(r, 6), Uhrzeit(r, 7),
            Required(r, 8), Datum(r, 9), Uhrzeit(r, 10), Uhrzeit(r, 11),
            Required(r, 12), Required(r, 13), int.Parse(Required(r, 14), CultureInfo.InvariantCulture),
            Zahl(r, 15), Zahl(r, 16),
            r[17] is string min ? decimal.Parse(min, CultureInfo.InvariantCulture) : null,
            r[18] is string max ? decimal.Parse(max, CultureInfo.InvariantCulture) : null,
            Required(r, 19) == "t", Required(r, 20), r[21]);

        // Zeitraum der Sendung und „belegt“ = andere offene Tour, die sich damit überschneidet.
        const string zeitraum = """
            WITH z AS (
                SELECT (a.abholdatum + a.abholfenster_von) AT TIME ZONE 'Europe/Berlin' AS von,
                       (a.lieferdatum + a.lieferfenster_bis) AT TIME ZONE 'Europe/Berlin' AS bis
                FROM public.auftrag a WHERE a.auftrag_id = $2::bigint AND a.unternehmen_id = $1::bigint
            ), belegt AS (
                SELECT t.fahrer_id, t.fahrzeug_id FROM public.tour t, z
                WHERE t.unternehmen_id = $1::bigint AND t.startzeit < z.bis AND t.endzeit > z.von
                  AND lower(t.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%'
            )
            """;

        var fahrer = (await _client.Value.QueryAsync(zeitraum + "\n" + """
            SELECT fa.fahrer_id, trim(concat_ws(' ', b.vorname, b.nachname)), fa.fuehrerscheinklasse, fa.verfuegbarkeit, ab.bezeichnung
            FROM public.fahrer fa
            JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
            LEFT JOIN public.abteilung ab ON ab.abteilung_id = b.abteilung_id AND ab.unternehmen_id = b.unternehmen_id
            WHERE b.unternehmen_id = $1::bigint AND b.aktiv = true
              AND lower(fa.verfuegbarkeit) NOT SIMILAR TO '%(urlaub|krank|abwesend|gesperrt|nicht verfügbar)%'
              AND NOT EXISTS (SELECT 1 FROM belegt x WHERE x.fahrer_id = fa.fahrer_id)
            ORDER BY lower(b.nachname), lower(b.vorname), fa.fahrer_id
            """, ids, cancellationToken))
            .Select(row => new FreierFahrer(Id(row, 0), Required(row, 1), Required(row, 2), Required(row, 3), row[4]))
            .ToArray();

        var fahrzeuge = (await _client.Value.QueryAsync(zeitraum + "\n" + """
            SELECT f.fahrzeug_id, f.kennzeichen, f.fahrzeugtyp, f.aufbau, f.nutzlast_kg, f.nutzbare_lademeter, f.ep_stellplaetze,
                   f.status, ab.bezeichnung
            FROM public.fahrzeug f
            LEFT JOIN public.abteilung ab ON ab.abteilung_id = f.abteilung_id AND ab.unternehmen_id = f.unternehmen_id
            WHERE f.unternehmen_id = $1::bigint
              AND lower(f.status) NOT SIMILAR TO '%(wartung|gesperrt|ausgemustert)%'
              AND NOT EXISTS (SELECT 1 FROM belegt x WHERE x.fahrzeug_id = f.fahrzeug_id)
            ORDER BY f.kennzeichen, f.fahrzeug_id
            """, ids, cancellationToken))
            .Select(row => new FreiesFahrzeug(Id(row, 0), Required(row, 1), Required(row, 2), Required(row, 3),
                Zahl(row, 4), Zahl(row, 5), int.Parse(Required(row, 6), CultureInfo.InvariantCulture), Required(row, 7), row[8]))
            .ToArray();

        TourPlanungsdaten planung = await PlanungsdatenLadenAsync(cancellationToken);
        return new ZuweisungDaten(sendung, fahrer, fahrzeuge, planung.Abteilungen, planung.TournummerVorschlag);
    }

    // ── D02: Verfügbar am Tag ────────────────────────────────────────────────────────

    /// <summary>
    /// Für die Spalte „Verfügbar am …“ in D02: alle einsetzbaren Fahrer und Fahrzeuge (Fahrer nicht im Urlaub/krank,
    /// Fahrzeug aktiv und nicht in Wartung/gesperrt) mit ihren offenen Touren an diesem Tag. Ganz freie stehen oben.
    /// Ob sie zu einer bestimmten Sendung passen, prüft danach D16 genau (Zeitfenster, Kühlung, Kapazität).
    /// </summary>
    public async Task<TagesVerfuegbarkeit> TagesVerfuegbarkeitLadenAsync(DateOnly tag, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var parameter = new string?[] { Text(zugriff.UnternehmenId), tag.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };

        // Offene Touren, die sich mit dem Tag (deutsche Zeit) überschneiden, als Text „06:30–15:00 T-2401“.
        const string touren = """
            WITH tag AS (
                SELECT ($2::date + time '00:00') AT TIME ZONE 'Europe/Berlin' AS von,
                       ($2::date + 1 + time '00:00') AT TIME ZONE 'Europe/Berlin' AS bis
            ), touren AS (
                SELECT t.fahrer_id, t.fahrzeug_id, t.startzeit,
                       to_char(t.startzeit AT TIME ZONE 'Europe/Berlin', 'HH24:MI') || '–' ||
                       to_char(t.endzeit AT TIME ZONE 'Europe/Berlin', 'HH24:MI') || ' ' || t.tournummer AS text
                FROM public.tour t, tag
                WHERE t.unternehmen_id = $1::bigint AND t.startzeit < tag.bis AND t.endzeit > tag.von
                  AND lower(t.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%'
            )
            """;

        var fahrerTask = _client.Value.QueryAsync(touren + "\n" + """
            SELECT fa.fahrer_id, trim(concat_ws(' ', b.vorname, b.nachname)), fa.fuehrerscheinklasse, fa.verfuegbarkeit, ab.bezeichnung,
                   (SELECT string_agg(x.text, ', ' ORDER BY x.startzeit) FROM touren x WHERE x.fahrer_id = fa.fahrer_id)
            FROM public.fahrer fa
            JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
            LEFT JOIN public.abteilung ab ON ab.abteilung_id = b.abteilung_id AND ab.unternehmen_id = b.unternehmen_id
            WHERE b.unternehmen_id = $1::bigint AND b.aktiv = true
              AND lower(fa.verfuegbarkeit) NOT SIMILAR TO '%(urlaub|krank|abwesend|gesperrt|nicht verfügbar)%'
            ORDER BY (SELECT count(*) FROM touren x WHERE x.fahrer_id = fa.fahrer_id), lower(b.nachname), lower(b.vorname), fa.fahrer_id
            """, parameter, cancellationToken);

        var fahrzeugTask = _client.Value.QueryAsync(touren + "\n" + """
            SELECT f.fahrzeug_id, f.kennzeichen, f.fahrzeugtyp, f.aufbau, f.nutzlast_kg, f.nutzbare_lademeter, f.ep_stellplaetze,
                   f.status, ab.bezeichnung,
                   (SELECT string_agg(x.text, ', ' ORDER BY x.startzeit) FROM touren x WHERE x.fahrzeug_id = f.fahrzeug_id)
            FROM public.fahrzeug f
            LEFT JOIN public.abteilung ab ON ab.abteilung_id = f.abteilung_id AND ab.unternehmen_id = f.unternehmen_id
            WHERE f.unternehmen_id = $1::bigint AND f.aktiv
              AND lower(f.status) NOT SIMILAR TO '%(wartung|gesperrt|ausgemustert)%'
            ORDER BY (SELECT count(*) FROM touren x WHERE x.fahrzeug_id = f.fahrzeug_id), f.kennzeichen, f.fahrzeug_id
            """, parameter, cancellationToken);

        await Task.WhenAll(fahrerTask, fahrzeugTask);

        static decimal ZahlOderNull(string? text) =>
            text is null ? 0 : decimal.Parse(text, CultureInfo.InvariantCulture);

        var fahrer = (await fahrerTask)
            .Select(row => new TagesFahrer(
                new FreierFahrer(Id(row, 0), Required(row, 1), row[2] ?? "", row[3] ?? "", row[4]), row[5]))
            .ToArray();
        var fahrzeuge = (await fahrzeugTask)
            .Select(row => new TagesFahrzeug(
                new FreiesFahrzeug(Id(row, 0), Required(row, 1), row[2] ?? "", row[3] ?? "",
                    ZahlOderNull(row[4]), ZahlOderNull(row[5]),
                    row[6] is string plaetze ? int.Parse(plaetze, CultureInfo.InvariantCulture) : 0,
                    row[7] ?? "", row[8]),
                row[9]))
            .ToArray();
        return new TagesVerfuegbarkeit(fahrer, fahrzeuge);
    }

    /// <summary>
    /// D02 Drag &amp; Drop: Sendung direkt einem Fahrer und Fahrzeug zuweisen, ohne D16 zu öffnen. Prüft dieselben Regeln
    /// wie D16 (frei im Zeitraum der Sendung, Kühlung, Kapazität, Führerscheinklasse) und legt wie D16 eine neue Tour mit
    /// Abholung und Zustellung an. Bereich wie die Gutart (z. B. „Frische“), sonst die erste aktive Abteilung.
    /// </summary>
    /// <returns>Die Tournummer der neuen Tour.</returns>
    /// <exception cref="ArgumentException">Eine Regel ist verletzt. Die Meldung ist für die Oberfläche gedacht.</exception>
    public async Task<string> SendungDirektZuweisenAsync(long auftragId, long fahrerId, long fahrzeugId,
        CancellationToken cancellationToken = default)
    {
        ZuweisungDaten daten = await ZuweisungLadenAsync(auftragId, cancellationToken);
        ZuweisungSendung s = daten.Sendung;
        if (s.Tournummer is { } tour)
            throw new ArgumentException($"Die Sendung ist inzwischen in Tour {tour} verplant.");

        FreierFahrer fahrer = daten.Fahrer.FirstOrDefault(f => f.FahrerId == fahrerId)
            ?? throw new ArgumentException("Der Fahrer ist im Zeitraum dieser Sendung nicht frei.");
        FreiesFahrzeug fahrzeug = daten.Fahrzeuge.FirstOrDefault(f => f.Id == fahrzeugId)
            ?? throw new ArgumentException("Das Fahrzeug ist im Zeitraum dieser Sendung nicht frei.");
        if (ZuweisungsRegeln.FahrzeugPasstNicht(fahrzeug, s.Gutart == SendungWerte.GutartFrische, s.GewichtKg, s.Lademeter,
                s.Verpackungstyp == SendungWerte.Europalette ? s.Anzahl : 0) is { } grund)
            throw new ArgumentException($"{fahrzeug.Kennzeichen} passt nicht: {grund}.");
        if (ZuweisungsRegeln.KlassePasstNicht(fahrer, fahrzeug) is { } klasse)
            throw new ArgumentException(klasse);

        AbteilungAuswahl abteilung = daten.Abteilungen
            .FirstOrDefault(a => string.Equals(a.Bezeichnung, s.Gutart, StringComparison.OrdinalIgnoreCase))
            ?? daten.Abteilungen.FirstOrDefault()
            ?? throw new ArgumentException("Ihre Firma hat noch keine aktive Abteilung. Eine Tour braucht einen Bereich.");

        // Wie in D16: zwei Stopps mit den Zeitfenstern der Sendung. Tourende muss nach dem Start liegen.
        var stopps = new List<TourStoppEntwurf>
        {
            new(TourWerte.ArtAbholung, Adressen.Ort(s.Abholadresse), s.Abholadresse,
                s.Abholdatum.ToDateTime(s.AbholfensterVon), s.Abholdatum.ToDateTime(s.AbholfensterBis), s.Id, TourWerte.ArtAbholung),
            new(TourWerte.ArtZustellung, Adressen.Ort(s.Lieferadresse), s.Lieferadresse,
                s.Lieferdatum.ToDateTime(s.LieferfensterVon), s.Lieferdatum.ToDateTime(s.LieferfensterBis), s.Id, TourWerte.ArtZustellung)
        };
        DateTime ende = s.Bis > s.Von ? s.Bis : s.Von.AddMinutes(1);

        // Hat gerade jemand anderes dieselbe Tournummer vergeben, einmal mit einem neuen Vorschlag wiederholen.
        string nummer = daten.TournummerVorschlag;
        for (int versuch = 0; ; versuch++)
        {
            try
            {
                await AnlegenAsync(new TourEntwurf(nummer, abteilung.Id, s.Von, ende,
                    Adressen.Ort(s.Abholadresse), Adressen.Ort(s.Lieferadresse), fahrer.FahrerId, fahrzeug.Id, stopps),
                    cancellationToken);
                return nummer;
            }
            catch (TournummerVergebenException) when (versuch == 0)
            {
                nummer = (await PlanungsdatenLadenAsync(cancellationToken)).TournummerVorschlag;
            }
        }
    }

    // ── D02: Zuweisung ändern oder auflösen ──────────────────────────────────────────

    /// <summary>
    /// D02: Fahrer oder Fahrzeug einer Tour setzen (id) oder entfernen (null). Nur bei „Geplant“/„Zugewiesen“ und noch
    /// nicht losgefahren. Beim Setzen dieselben Regeln wie D06 (frei im Tourzeitraum, Kühlung, Kapazität, Klasse).
    /// Der Status folgt der Zuordnung: beide gesetzt = „Zugewiesen“, sonst „Geplant“.
    /// </summary>
    /// <returns>Die Tournummer.</returns>
    /// <exception cref="ArgumentException">Eine Regel ist verletzt oder die Tour lässt sich nicht mehr ändern.</exception>
    public async Task<string> RessourceAendernAsync(long tourId, bool fahrer, long? id, CancellationToken cancellationToken = default)
    {
        if (id is long neu)
        {
            TourZuweisungDaten d = await TourZuweisungLadenAsync(tourId, cancellationToken);
            TourZuweisungKopf tour = d.Tour;
            if (fahrer)
            {
                FreierFahrer f = d.Fahrer.FirstOrDefault(x => x.FahrerId == neu)
                    ?? throw new ArgumentException($"Der Fahrer ist im Zeitraum von Tour {tour.Tournummer} nicht frei.");
                FreiesFahrzeug? fz = tour.FahrzeugId is long fzId ? d.Fahrzeuge.FirstOrDefault(x => x.Id == fzId) : null;
                if (fz is not null && ZuweisungsRegeln.KlassePasstNicht(f, fz) is { } klasse)
                    throw new ArgumentException(klasse);
            }
            else
            {
                FreiesFahrzeug fz = d.Fahrzeuge.FirstOrDefault(x => x.Id == neu)
                    ?? throw new ArgumentException($"Das Fahrzeug ist im Zeitraum von Tour {tour.Tournummer} nicht frei.");
                if (ZuweisungsRegeln.FahrzeugPasstNicht(fz, tour.Gekuehlt, tour.GewichtKg, tour.Lademeter, tour.Europaletten) is { } grund)
                    throw new ArgumentException($"{fz.Kennzeichen} passt nicht: {grund}.");
                FreierFahrer? f = tour.FahrerId is long fId ? d.Fahrer.FirstOrDefault(x => x.FahrerId == fId) : null;
                if (f is not null && ZuweisungsRegeln.KlassePasstNicht(f, fz) is { } klasse)
                    throw new ArgumentException(klasse);
            }
        }

        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        // $1 Firma, $2 Tour, $3 'Fahrer'/'Fahrzeug', $4 Fahrer-Id, $5 Fahrzeug-Id, $6 Geplant, $7 Zugewiesen, $8 Konto.
        // Überschneidung mit anderen offenen Touren wird hier noch einmal geprüft (gleichzeitige Änderungen).
        const string query = """
            WITH ziel AS (
                SELECT t.tour_id FROM public.tour t
                WHERE t.tour_id = $2::bigint AND t.unternehmen_id = $1::bigint AND t.status IN ($6, $7)
                  AND NOT EXISTS (SELECT 1 FROM public.tourstopp s
                                  WHERE s.tour_id = t.tour_id AND (s.ist_ankunft IS NOT NULL OR s.ist_abfahrt IS NOT NULL))
                  AND ($4::bigint IS NULL OR (
                        EXISTS (SELECT 1 FROM public.fahrer fa JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
                                WHERE fa.fahrer_id = $4::bigint AND b.unternehmen_id = $1::bigint AND b.aktiv = true)
                        AND NOT EXISTS (SELECT 1 FROM public.tour x
                                        WHERE x.unternehmen_id = $1::bigint AND x.tour_id <> t.tour_id AND x.fahrer_id = $4::bigint
                                          AND x.startzeit < t.endzeit AND x.endzeit > t.startzeit
                                          AND lower(x.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%')))
                  AND ($5::bigint IS NULL OR (
                        EXISTS (SELECT 1 FROM public.fahrzeug f WHERE f.fahrzeug_id = $5::bigint AND f.unternehmen_id = $1::bigint)
                        AND NOT EXISTS (SELECT 1 FROM public.tour x
                                        WHERE x.unternehmen_id = $1::bigint AND x.tour_id <> t.tour_id AND x.fahrzeug_id = $5::bigint
                                          AND x.startzeit < t.endzeit AND x.endzeit > t.startzeit
                                          AND lower(x.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%')))
            ), geaendert AS (
                UPDATE public.tour t
                SET fahrer_id = CASE WHEN $3 = 'Fahrer' THEN $4::bigint ELSE t.fahrer_id END,
                    fahrzeug_id = CASE WHEN $3 = 'Fahrzeug' THEN $5::bigint ELSE t.fahrzeug_id END,
                    status = CASE WHEN (CASE WHEN $3 = 'Fahrer' THEN $4::bigint ELSE t.fahrer_id END) IS NOT NULL
                                   AND (CASE WHEN $3 = 'Fahrzeug' THEN $5::bigint ELSE t.fahrzeug_id END) IS NOT NULL
                                  THEN $7 ELSE $6 END
                FROM ziel z WHERE t.tour_id = z.tour_id
                RETURNING t.tour_id, t.tournummer, t.unternehmen_id, t.abteilung_id
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
                SELECT CASE WHEN $8::bigint IS NULL THEN 'Entwicklungsansicht' ELSE 'Benutzer' END,
                       'Tour zugewiesen', 'Gespeichert',
                       'Tour ' || g.tournummer || ': ' || $3 || CASE WHEN COALESCE($4, $5) IS NULL THEN ' entfernt' ELSE ' gesetzt' END,
                       g.unternehmen_id, g.abteilung_id, $8::bigint
                FROM geaendert g
            )
            SELECT tournummer FROM geaendert
            """;
        var rows = await _client.Value.QueryAsync(query, new string?[]
        {
            Text(zugriff.UnternehmenId), Text(tourId), fahrer ? "Fahrer" : "Fahrzeug",
            fahrer && id is long f1 ? Text(f1) : null,
            !fahrer && id is long f2 ? Text(f2) : null,
            TourWerte.StatusGeplant, TourWerte.StatusZugewiesen,
            BenutzerSitzung.AktuellerBenutzer is { } benutzer ? Text(benutzer.Id) : null
        }, cancellationToken);
        if (rows.Count != 1)
            throw new ArgumentException("Die Tour lässt sich nicht mehr ändern: Sie ist schon unterwegs oder abgeschlossen, " +
                                        "oder Fahrer bzw. Fahrzeug haben inzwischen eine andere Tour. Bitte laden Sie die Liste neu.");
        return Required(rows[0], 0);
    }

    /// <summary>
    /// D02 „Lösen“: Sendung aus ihrer Tour nehmen. Ihre Stopps werden entfernt (kombinierte Stopps anderer Sendungen
    /// bleiben), die übrigen Stopps neu durchnummeriert. Bleibt die Tour leer, wird sie gelöscht – oder, falls andere
    /// Daten auf sie verweisen, auf „Storniert“ gesetzt. Nur bei „Geplant“/„Zugewiesen“ und noch nicht losgefahren.
    /// </summary>
    /// <returns>Tournummer und ob die Tour danach aufgelöst ist.</returns>
    public async Task<(string Tournummer, bool TourAufgeloest)> SendungAusTourNehmenAsync(long auftragId,
        CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        string firma = Text(zugriff.UnternehmenId);

        // $1 Firma, $2 Sendung, $3 Konto, $4 Geplant, $5 Zugewiesen. Eine Zeile: Tour, Nummer, andere Sendungen, übrige Stopps.
        const string loesen = """
            WITH a AS (
                SELECT x.auftrag_id, x.auftragsnummer, t.tour_id, t.tournummer, t.abteilung_id
                FROM public.auftrag x
                JOIN public.tour t ON t.tour_id = x.tour_id AND t.unternehmen_id = x.unternehmen_id
                WHERE x.auftrag_id = $2::bigint AND x.unternehmen_id = $1::bigint
                  AND t.status IN ($4, $5)
                  AND NOT EXISTS (SELECT 1 FROM public.tourstopp s
                                  WHERE s.tour_id = t.tour_id AND (s.ist_ankunft IS NOT NULL OR s.ist_abfahrt IS NOT NULL))
                  AND NOT EXISTS (SELECT 1 FROM public.statusmeldung m WHERE m.tour_id = t.tour_id)
            ), eigene AS (
                SELECT sa.stopp_id FROM public.stopp_auftrag sa
                JOIN public.tourstopp s ON s.stopp_id = sa.stopp_id
                JOIN a ON a.auftrag_id = sa.auftrag_id AND s.tour_id = a.tour_id
            ), links AS (
                DELETE FROM public.stopp_auftrag sa USING a
                WHERE sa.auftrag_id = a.auftrag_id AND sa.stopp_id IN (SELECT stopp_id FROM eigene)
                RETURNING sa.stopp_id
            ), stopps AS (
                DELETE FROM public.tourstopp s USING a
                WHERE s.tour_id = a.tour_id AND s.stopp_id IN (SELECT stopp_id FROM eigene)
                  AND NOT EXISTS (SELECT 1 FROM public.stopp_auftrag x WHERE x.stopp_id = s.stopp_id AND x.auftrag_id <> a.auftrag_id)
                RETURNING s.stopp_id
            ), sendung AS (
                UPDATE public.auftrag x SET tour_id = NULL FROM a WHERE x.auftrag_id = a.auftrag_id
                RETURNING x.auftrag_id
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
                SELECT CASE WHEN $3::bigint IS NULL THEN 'Entwicklungsansicht' ELSE 'Benutzer' END,
                       'Sendung aus Tour genommen', 'Gespeichert', 'Sendung ' || a.auftragsnummer || ' aus Tour ' || a.tournummer,
                       $1::bigint, a.abteilung_id, $3::bigint
                FROM a
            )
            SELECT a.tour_id, a.tournummer,
                   (SELECT count(*) FROM public.auftrag y WHERE y.tour_id = a.tour_id AND y.auftrag_id <> a.auftrag_id),
                   (SELECT count(*) FROM public.tourstopp s WHERE s.tour_id = a.tour_id) - (SELECT count(*) FROM stopps)
            FROM a
            """;
        var rows = await _client.Value.QueryAsync(loesen, new string?[]
        {
            firma, Text(auftragId),
            BenutzerSitzung.AktuellerBenutzer is { } benutzer ? Text(benutzer.Id) : null,
            TourWerte.StatusGeplant, TourWerte.StatusZugewiesen
        }, cancellationToken);
        if (rows.Count != 1)
            throw new ArgumentException("Die Sendung lässt sich nicht mehr aus der Tour nehmen: Die Tour ist schon unterwegs " +
                                        "oder abgeschlossen. Bitte laden Sie die Liste neu.");

        string tourId = Required(rows[0], 0);
        string tournummer = Required(rows[0], 1);
        long andereSendungen = long.Parse(Required(rows[0], 2), CultureInfo.InvariantCulture);
        long uebrigeStopps = long.Parse(Required(rows[0], 3), CultureInfo.InvariantCulture);
        var tour = new string?[] { firma, tourId };

        if (uebrigeStopps > 0)
        {
            // Übrige Stopps wieder 1, 2, 3 … nummerieren. Erst weit nach hinten schieben, damit die eindeutige
            // Reihenfolge je Tour unterwegs nie doppelt vorkommt.
            await _client.Value.QueryAsync("""
                UPDATE public.tourstopp s SET reihenfolge = s.reihenfolge + 100000
                FROM public.tour t WHERE t.tour_id = s.tour_id AND t.tour_id = $2::bigint AND t.unternehmen_id = $1::bigint
                """, tour, cancellationToken);
            await _client.Value.QueryAsync("""
                UPDATE public.tourstopp s SET reihenfolge = n.nr
                FROM (SELECT x.stopp_id, row_number() OVER (ORDER BY x.reihenfolge) AS nr
                      FROM public.tourstopp x JOIN public.tour t ON t.tour_id = x.tour_id
                      WHERE x.tour_id = $2::bigint AND t.unternehmen_id = $1::bigint) n
                WHERE s.stopp_id = n.stopp_id
                """, tour, cancellationToken);
            return (tournummer, false);
        }
        if (andereSendungen > 0)
            return (tournummer, false);

        // Leere Tour entfernen. Verweisen andere Daten auf sie (z. B. Protokoll, Nachrichten), wird sie storniert.
        try
        {
            await _client.Value.QueryAsync("""
                DELETE FROM public.tour t
                WHERE t.tour_id = $2::bigint AND t.unternehmen_id = $1::bigint
                  AND NOT EXISTS (SELECT 1 FROM public.tourstopp s WHERE s.tour_id = t.tour_id)
                  AND NOT EXISTS (SELECT 1 FROM public.auftrag x WHERE x.tour_id = t.tour_id)
                """, tour, cancellationToken);
        }
        catch (NeonSqlException exception) when (exception.Code == "23503")
        {
            await _client.Value.QueryAsync("""
                UPDATE public.tour t SET status = 'Storniert', fahrer_id = NULL, fahrzeug_id = NULL
                WHERE t.tour_id = $2::bigint AND t.unternehmen_id = $1::bigint
                """, tour, cancellationToken);
        }
        return (tournummer, true);
    }

    // ── D06: Tour zuweisen ───────────────────────────────────────────────────────────

    /// <summary>
    /// Die Tour mit Stopps und Ladung sowie alle Fahrer und Fahrzeuge, die im Zeitraum der Tour frei sind.
    /// Frei wie in D16: keine ANDERE offene Tour im Zeitraum. Die bisherige Zuordnung dieser Tour gilt als frei.
    /// </summary>
    /// <exception cref="ArgumentException">Die Tour gibt es in dieser Firma nicht.</exception>
    public async Task<TourZuweisungDaten> TourZuweisungLadenAsync(long tourId, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var ids = new string?[] { Text(zugriff.UnternehmenId), Text(tourId) };

        var rows = await _client.Value.QueryAsync("""
            SELECT t.tour_id, t.tournummer, a.bezeichnung, t.status,
                   lower(t.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%',
                   t.startort, t.zielort,
                   to_char(t.startzeit AT TIME ZONE 'Europe/Berlin', 'YYYY-MM-DD HH24:MI'),
                   to_char(t.endzeit AT TIME ZONE 'Europe/Berlin', 'YYYY-MM-DD HH24:MI'),
                   t.fahrer_id, t.fahrzeug_id,
                   l.anzahl, l.gewicht, l.lademeter, l.paletten, l.gekuehlt
            FROM public.tour t
            LEFT JOIN public.abteilung a ON a.abteilung_id = t.abteilung_id AND a.unternehmen_id = t.unternehmen_id
            CROSS JOIN LATERAL (
                SELECT count(*) AS anzahl,
                       COALESCE(sum(x.gewicht_kg), 0) AS gewicht,
                       COALESCE(round(sum(x.anzahl * x.laenge_m * x.breite_m / 2.4), 1), 0) AS lademeter,
                       COALESCE(sum(x.anzahl) FILTER (WHERE x.verpackungstyp = 'Europalette'), 0) AS paletten,
                       COALESCE(bool_or(x.gutart = 'Frische'), false) AS gekuehlt
                FROM public.auftrag x WHERE x.tour_id = t.tour_id AND x.unternehmen_id = t.unternehmen_id
            ) l
            WHERE t.unternehmen_id = $1::bigint AND t.tour_id = $2::bigint
            """, ids, cancellationToken);
        if (rows.Count != 1)
            throw new ArgumentException("Diese Tour gibt es nicht (mehr).");
        var r = rows[0];
        var tour = new TourZuweisungKopf(
            Id(r, 0), Required(r, 1), r[2], Required(r, 3), Required(r, 4) == "t",
            Required(r, 5), Required(r, 6), Zeitpunkt(r, 7), Zeitpunkt(r, 8),
            r[9] is string fahrer ? long.Parse(fahrer, CultureInfo.InvariantCulture) : null,
            r[10] is string fahrzeug ? long.Parse(fahrzeug, CultureInfo.InvariantCulture) : null,
            int.Parse(Required(r, 11), CultureInfo.InvariantCulture), Zahl(r, 12), Zahl(r, 13),
            int.Parse(Required(r, 14), CultureInfo.InvariantCulture), Required(r, 15) == "t");

        var stopps = (await _client.Value.QueryAsync("""
            SELECT s.reihenfolge, s.art, s.bezeichnung,
                   to_char(s.zeitfenster_von AT TIME ZONE 'Europe/Berlin', 'DD.MM. HH24:MI') || '–' ||
                   to_char(s.zeitfenster_bis AT TIME ZONE 'Europe/Berlin', 'HH24:MI')
            FROM public.tourstopp s JOIN public.tour t ON t.tour_id = s.tour_id
            WHERE t.unternehmen_id = $1::bigint AND s.tour_id = $2::bigint
            ORDER BY s.reihenfolge, s.stopp_id
            """, ids, cancellationToken))
            .Select(row => new TourStoppKurz(int.Parse(Required(row, 0), CultureInfo.InvariantCulture),
                Required(row, 1), Required(row, 2), Required(row, 3)))
            .ToArray();

        // Zeitraum der Tour; „belegt“ = ANDERE offene Tour, die sich damit überschneidet.
        const string zeitraum = """
            WITH z AS (
                SELECT t.startzeit AS von, t.endzeit AS bis
                FROM public.tour t WHERE t.tour_id = $2::bigint AND t.unternehmen_id = $1::bigint
            ), belegt AS (
                SELECT t.fahrer_id, t.fahrzeug_id FROM public.tour t, z
                WHERE t.unternehmen_id = $1::bigint AND t.tour_id <> $2::bigint
                  AND t.startzeit < z.bis AND t.endzeit > z.von
                  AND lower(t.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%'
            )
            """;
        var fahrerListe = (await _client.Value.QueryAsync(zeitraum + "\n" + """
            SELECT fa.fahrer_id, trim(concat_ws(' ', b.vorname, b.nachname)), fa.fuehrerscheinklasse, fa.verfuegbarkeit, ab.bezeichnung
            FROM public.fahrer fa
            JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
            LEFT JOIN public.abteilung ab ON ab.abteilung_id = b.abteilung_id AND ab.unternehmen_id = b.unternehmen_id
            WHERE b.unternehmen_id = $1::bigint AND b.aktiv = true
              AND lower(fa.verfuegbarkeit) NOT SIMILAR TO '%(urlaub|krank|abwesend|gesperrt|nicht verfügbar)%'
              AND NOT EXISTS (SELECT 1 FROM belegt x WHERE x.fahrer_id = fa.fahrer_id)
            ORDER BY lower(b.nachname), lower(b.vorname), fa.fahrer_id
            """, ids, cancellationToken))
            .Select(row => new FreierFahrer(Id(row, 0), Required(row, 1), Required(row, 2), Required(row, 3), row[4]))
            .ToArray();

        var fahrzeugListe = (await _client.Value.QueryAsync(zeitraum + "\n" + """
            SELECT f.fahrzeug_id, f.kennzeichen, f.fahrzeugtyp, f.aufbau, f.nutzlast_kg, f.nutzbare_lademeter, f.ep_stellplaetze,
                   f.status, ab.bezeichnung
            FROM public.fahrzeug f
            LEFT JOIN public.abteilung ab ON ab.abteilung_id = f.abteilung_id AND ab.unternehmen_id = f.unternehmen_id
            WHERE f.unternehmen_id = $1::bigint
              AND lower(f.status) NOT SIMILAR TO '%(wartung|gesperrt|ausgemustert)%'
              AND NOT EXISTS (SELECT 1 FROM belegt x WHERE x.fahrzeug_id = f.fahrzeug_id)
            ORDER BY f.kennzeichen, f.fahrzeug_id
            """, ids, cancellationToken))
            .Select(row => new FreiesFahrzeug(Id(row, 0), Required(row, 1), Required(row, 2), Required(row, 3),
                Zahl(row, 4), Zahl(row, 5), int.Parse(Required(row, 6), CultureInfo.InvariantCulture), Required(row, 7), row[8]))
            .ToArray();

        return new TourZuweisungDaten(tour, stopps, fahrerListe, fahrzeugListe);
    }

    /// <summary>
    /// Setzt Fahrer und Fahrzeug einer offenen Tour. Eine geplante Tour wird dabei „Zugewiesen“, eine schon
    /// laufende Tour behält ihren Status. Geprüft wird in einem Befehl: Tour, Fahrer und Fahrzeug gehören zur Firma,
    /// die Tour ist nicht abgeschlossen, und Fahrer bzw. Fahrzeug haben im Tourzeitraum keine andere offene Tour.
    /// </summary>
    /// <exception cref="ArgumentException">Eine der Prüfungen ist fehlgeschlagen.</exception>
    public async Task ZuweisenAsync(long tourId, long fahrerId, long fahrzeugId, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        const string query = """
            WITH ziel AS (
                SELECT t.tour_id, t.startzeit, t.endzeit FROM public.tour t
                WHERE t.tour_id = $2::bigint AND t.unternehmen_id = $1::bigint
                  AND lower(t.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%'
                  AND EXISTS (SELECT 1 FROM public.fahrer fa JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
                              WHERE fa.fahrer_id = $3::bigint AND b.unternehmen_id = $1::bigint AND b.aktiv = true)
                  AND EXISTS (SELECT 1 FROM public.fahrzeug f WHERE f.fahrzeug_id = $4::bigint AND f.unternehmen_id = $1::bigint)
                  AND NOT EXISTS (
                        SELECT 1 FROM public.tour x
                        WHERE x.unternehmen_id = $1::bigint AND x.tour_id <> t.tour_id
                          AND (x.fahrer_id = $3::bigint OR x.fahrzeug_id = $4::bigint)
                          AND x.startzeit < t.endzeit AND x.endzeit > t.startzeit
                          AND lower(x.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%')
            ), geaendert AS (
                UPDATE public.tour t
                SET fahrer_id = $3::bigint, fahrzeug_id = $4::bigint,
                    status = CASE WHEN lower(t.status) IN ('geplant', 'offen', 'zugewiesen') THEN $6 ELSE t.status END
                FROM ziel z WHERE t.tour_id = z.tour_id
                RETURNING t.tour_id, t.tournummer, t.unternehmen_id, t.abteilung_id
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
                SELECT CASE WHEN $5::bigint IS NULL THEN 'Entwicklungsansicht' ELSE 'Benutzer' END,
                       'Tour zugewiesen', 'Gespeichert', 'Tour ' || g.tournummer, g.unternehmen_id, g.abteilung_id, $5::bigint
                FROM geaendert g
            )
            SELECT tour_id FROM geaendert
            """;
        var rows = await _client.Value.QueryAsync(query, new string?[]
        {
            Text(zugriff.UnternehmenId), Text(tourId), Text(fahrerId), Text(fahrzeugId),
            BenutzerSitzung.AktuellerBenutzer is { } benutzer ? Text(benutzer.Id) : null,
            TourWerte.StatusZugewiesen
        }, cancellationToken);
        if (rows.Count != 1)
            throw new ArgumentException("Die Zuweisung konnte nicht gespeichert werden: Die Tour ist inzwischen abgeschlossen, " +
                                        "oder Fahrer bzw. Fahrzeug haben in diesem Zeitraum schon eine andere Tour. Bitte laden Sie die Ansicht neu.");
    }

    // ── D07: Tourdetails ─────────────────────────────────────────────────────────────

    /// <summary>Tour mit Fahrer, Fahrzeug, Ladung und allen Stopps (Ist-Zeiten, ETA, Kunden der Sendungen).</summary>
    /// <exception cref="ArgumentException">Die Tour gibt es in dieser Firma nicht.</exception>
    public async Task<TourDetails> DetailsLadenAsync(long tourId, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var ids = new string?[] { Text(zugriff.UnternehmenId), Text(tourId) };

        var rows = await _client.Value.QueryAsync("""
            SELECT t.tour_id, t.tournummer, a.bezeichnung, t.status,
                   NULLIF(trim(concat_ws(' ', b.vorname, b.nachname)), ''), f.kennzeichen, f.fahrzeugtyp, f.aufbau,
                   to_char(t.startzeit AT TIME ZONE 'Europe/Berlin', 'Dy DD.MM. HH24:MI') || ' – ' ||
                   CASE WHEN (t.startzeit AT TIME ZONE 'Europe/Berlin')::date = (t.endzeit AT TIME ZONE 'Europe/Berlin')::date
                        THEN to_char(t.endzeit AT TIME ZONE 'Europe/Berlin', 'HH24:MI')
                        ELSE to_char(t.endzeit AT TIME ZONE 'Europe/Berlin', 'Dy DD.MM. HH24:MI') END,
                   t.startort, t.zielort,
                   l.anzahl, l.plaetze, l.lademeter, l.gewicht
            FROM public.tour t
            LEFT JOIN public.abteilung a ON a.abteilung_id = t.abteilung_id AND a.unternehmen_id = t.unternehmen_id
            LEFT JOIN public.fahrer fa ON fa.fahrer_id = t.fahrer_id
            LEFT JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id AND b.unternehmen_id = t.unternehmen_id
            LEFT JOIN public.fahrzeug f ON f.fahrzeug_id = t.fahrzeug_id AND f.unternehmen_id = t.unternehmen_id
            CROSS JOIN LATERAL (
                SELECT count(*) AS anzahl, COALESCE(sum(x.anzahl), 0) AS plaetze,
                       COALESCE(round(sum(x.anzahl * x.laenge_m * x.breite_m / 2.4), 1), 0) AS lademeter,
                       COALESCE(sum(x.gewicht_kg), 0) AS gewicht
                FROM public.auftrag x WHERE x.tour_id = t.tour_id AND x.unternehmen_id = t.unternehmen_id
            ) l
            WHERE t.unternehmen_id = $1::bigint AND t.tour_id = $2::bigint
            """, ids, cancellationToken);
        if (rows.Count != 1)
            throw new ArgumentException("Diese Tour gibt es nicht (mehr).");
        var r = rows[0];
        var kopf = new TourDetailKopf(
            Id(r, 0), Required(r, 1), r[2], Required(r, 3), r[4], r[5], r[6], r[7],
            Wochentag(Required(r, 8)), Required(r, 9), Required(r, 10),
            int.Parse(Required(r, 11), CultureInfo.InvariantCulture), int.Parse(Required(r, 12), CultureInfo.InvariantCulture),
            Zahl(r, 13), Zahl(r, 14));

        // Kunden eines Stopps = Kunden der Sendungen, die an diesem Stopp abgeholt oder zugestellt werden.
        var stopps = (await _client.Value.QueryAsync("""
            SELECT s.stopp_id, s.reihenfolge, s.art, s.bezeichnung, s.adresse,
                   (SELECT string_agg(DISTINCT k.firmenname, ', ')
                    FROM public.stopp_auftrag sa
                    JOIN public.auftrag x ON x.auftrag_id = sa.auftrag_id AND x.unternehmen_id = t.unternehmen_id
                    JOIN public.kunde k ON k.kunde_id = x.kunde_id
                    WHERE sa.stopp_id = s.stopp_id),
                   to_char(s.zeitfenster_von AT TIME ZONE 'Europe/Berlin', 'DD.MM.'),
                   to_char(s.zeitfenster_von AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
                   to_char(s.zeitfenster_bis AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
                   to_char(s.ist_ankunft AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
                   to_char(s.ist_abfahrt AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
                   to_char(COALESCE(s.aktuelle_eta, s.geplante_ankunft) AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
                   GREATEST(0, ceil(extract(epoch FROM COALESCE(s.aktuelle_eta, s.geplante_ankunft) - s.zeitfenster_bis) / 60))::integer,
                   s.status, s.breitengrad, s.laengengrad
            FROM public.tourstopp s JOIN public.tour t ON t.tour_id = s.tour_id
            WHERE t.unternehmen_id = $1::bigint AND s.tour_id = $2::bigint
            ORDER BY s.reihenfolge, s.stopp_id
            """, ids, cancellationToken))
            .Select(row => new TourDetailStopp(
                Id(row, 0), int.Parse(Required(row, 1), CultureInfo.InvariantCulture), Required(row, 2), Required(row, 3),
                Required(row, 4), row[5], Required(row, 6), Required(row, 7), Required(row, 8), row[9], row[10], row[11],
                row[12] is string minuten ? int.Parse(minuten, CultureInfo.InvariantCulture) : 0,
                Required(row, 13),
                row[14] is string breite ? decimal.Parse(breite, CultureInfo.InvariantCulture) : null,
                row[15] is string laenge ? decimal.Parse(laenge, CultureInfo.InvariantCulture) : null))
            .ToArray();

        return new TourDetails(kopf, stopps);
    }

    // PostgreSQL liefert englische Wochentage („Thu“). Für die Anzeige auf Deutsch übersetzen.
    private static string Wochentag(string text)
    {
        string[] englisch = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        string[] deutsch = { "Mo", "Di", "Mi", "Do", "Fr", "Sa", "So" };
        for (int i = 0; i < englisch.Length; i++)
            text = text.Replace(englisch[i], deutsch[i], StringComparison.Ordinal);
        return text;
    }

    // „2026-09-29 08:00“ aus der Datenbank (deutsche Ortszeit).
    private static DateTime Zeitpunkt(string?[] row, int index) =>
        DateTime.ParseExact(Required(row, index), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static decimal Zahl(string?[] row, int index) => decimal.Parse(Required(row, index), CultureInfo.InvariantCulture);

    /// <summary>Prüft die Eingaben noch einmal unabhängig von der Oberfläche. Die Meldungen sind für die Anzeige gedacht.</summary>
    private static TourEntwurf Pruefen(TourEntwurf entwurf)
    {
        ArgumentNullException.ThrowIfNull(entwurf);
        string tournummer = Pflichttext(entwurf.Tournummer, "Tournummer", 30);
        string startort = Pflichttext(entwurf.Startort, "Startort", 200);
        string zielort = Pflichttext(entwurf.Zielort, "Zielort", 200);
        if (entwurf.Ende <= entwurf.Start)
            throw new ArgumentException("Das Tourende muss nach dem Tourstart liegen.");
        if (entwurf.Stopps.Count == 0)
            throw new ArgumentException("Bitte mindestens einen Stopp hinzufügen oder eine Sendung auswählen.");
        if (entwurf.Stopps.Count > 100)
            throw new ArgumentException("Eine Tour darf höchstens 100 Stopps haben.");

        var stopps = new List<TourStoppEntwurf>();
        foreach (TourStoppEntwurf stopp in entwurf.Stopps)
        {
            if (!TourWerte.StoppArten.Contains(stopp.Art))
                throw new ArgumentException("Bitte für jeden Stopp eine gültige Art wählen.");
            if (stopp.Bis < stopp.Von)
                throw new ArgumentException("Bei einem Stopp liegt das Zeitfenster-Ende vor dem Beginn.");
            if (stopp.AuftragId is not null && stopp.Vorgang is not (TourWerte.ArtAbholung or TourWerte.ArtZustellung))
                throw new ArgumentException("Ein Stopp einer Sendung muss eine Abholung oder Zustellung sein.");
            stopps.Add(stopp with
            {
                Bezeichnung = Pflichttext(stopp.Bezeichnung, "Bezeichnung des Stopps", 200),
                Adresse = Pflichttext(stopp.Adresse, "Adresse des Stopps", 300)
            });
        }

        // Je Sendung: Abholung vor Zustellung.
        for (int i = 0; i < stopps.Count; i++)
        {
            if (stopps[i].Vorgang == TourWerte.ArtZustellung && stopps[i].AuftragId is long id &&
                stopps.Skip(i + 1).Any(s => s.AuftragId == id && s.Vorgang == TourWerte.ArtAbholung))
            {
                throw new ArgumentException("Bei einer Sendung steht die Zustellung vor der Abholung. Bitte die Reihenfolge ändern.");
            }
        }

        return entwurf with { Tournummer = tournummer, Startort = startort, Zielort = zielort, Stopps = stopps };
    }

    private static string Pflichttext(string? text, string feld, int maximaleLaenge)
    {
        string wert = (text ?? string.Empty).Trim();
        if (wert.Length == 0 || wert.Length > maximaleLaenge)
            throw new ArgumentException($"{feld} muss ausgefüllt sein und darf höchstens {maximaleLaenge} Zeichen enthalten.");
        return wert;
    }

    // Deutsche Ortszeit ohne Zone, z. B. „2026-09-29T08:00:00“. Die Datenbank rechnet mit AT TIME ZONE um.
    private static string Lokal(DateTime zeit) => zeit.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private static DateOnly Datum(string?[] row, int index) =>
        DateOnly.ParseExact(Required(row, index), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static TimeOnly Uhrzeit(string?[] row, int index) =>
        TimeOnly.ParseExact(Required(row, index), "HH:mm", CultureInfo.InvariantCulture);

    private static long Id(string?[] row, int index) => long.Parse(Required(row, index), CultureInfo.InvariantCulture);

    private static string Text(long wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}

/// <summary>Eine verständliche Meldung statt des technischen PostgreSQL-Fehlers.</summary>
public sealed class TournummerVergebenException : Exception
{
    public TournummerVergebenException(Exception innerException)
        : base("Diese Tournummer gibt es in Ihrem Unternehmen schon. Bitte eine andere wählen.", innerException) { }
}
