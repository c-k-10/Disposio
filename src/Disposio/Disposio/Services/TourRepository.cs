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
/// Touren für die Ansichten des Disponenten (D04, D05 …). Wie in den anderen Repositories wird zuerst die Rolle
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

        return new TourPlanungsdaten(abteilungen, fahrer, fahrzeuge, sendungen, vorschlag);
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
            throw new ArgumentException("Die Tour konnte nicht gespeichert werden: Eine Sendung wurde inzwischen verplant " +
                                        "oder Abteilung (inzwischen deaktiviert?), Fahrer bzw. Fahrzeug gibt es nicht mehr. Bitte laden Sie das Formular neu.");
        }
        catch (NeonSqlException exception) when (exception.Code == "23505" &&
            exception.ConstraintName == "tour_unternehmen_tournummer_uq")
        {
            // Die Datenbank prüft eindeutige Tournummern je Firma, auch bei zwei gleichzeitigen Speichervorgängen.
            throw new TournummerVergebenException(exception);
        }
    }

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
