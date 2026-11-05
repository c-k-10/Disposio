using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>Vertrag zwischen F03 „Fahrerzuordnung“ (bzw. F01) und der Datenbank.</summary>
public interface IFahrerzuordnungRepository
{
    Task<FahrerzuordnungDaten> LadenAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Fahrerzuordnung>> AktuelleLadenAsync(CancellationToken cancellationToken = default);
    Task ZuordnenAsync(long fahrzeugId, long fahrerId, string? bemerkung, CancellationToken cancellationToken = default);
    Task WechselnAsync(long zuordnungId, long neuerFahrerId, string? bemerkung, CancellationToken cancellationToken = default);
    Task<bool> BeendenAsync(long zuordnungId, CancellationToken cancellationToken = default);
}

/// <summary>
/// SQL für public.fahrer_fahrzeug_zuordnung (US-03, #83–#88). Wie im FahrzeugRepository prüft jede Methode
/// zuerst die Rolle Fuhrparkmanager und verwendet danach nur die Firma aus dieser Prüfung.
/// Zuordnungen beginnen und enden immer „jetzt“ (Zeit der Datenbank). Beendete Zuordnungen werden nie
/// gelöscht, sondern bekommen ein Ende (gueltig_bis) und bilden so die Historie.
/// Voraussetzung: 010_fahrerzuordnung_wartung.sql wurde in Neon ausgeführt.
/// </summary>
public sealed class FahrerzuordnungRepository : IFahrerzuordnungRepository
{
    private const FirmenRolle Rolle = FirmenRolle.Fuhrparkmanager;
    public const int BemerkungMaximal = 300;

    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    // #84 „verfügbar“: aktiv, nicht in Wartung oder gesperrt (dieselbe Regel wie in der Tourenplanung)
    // und ohne laufende Fahrerzuordnung.
    private const string FahrzeugVerfuegbar = """
        f.aktiv
        AND lower(f.status) NOT SIMILAR TO '%(wartung|gesperrt|ausgemustert)%'
        AND NOT EXISTS (SELECT 1 FROM public.fahrer_fahrzeug_zuordnung x
                        WHERE x.fahrzeug_id = f.fahrzeug_id AND x.gueltig_bis IS NULL)
        """;

    // Alle Abfragen auf Zuordnungen liefern dieselben Spalten. ReadZuordnung liest genau diese Positionen.
    private const string ZuordnungSpalten = """
        z.zuordnung_id, z.fahrzeug_id, f.kennzeichen, z.fahrer_id, trim(concat_ws(' ', b.vorname, b.nachname)),
        to_char(z.gueltig_von AT TIME ZONE 'Europe/Berlin', 'YYYY-MM-DD HH24:MI'),
        to_char(z.gueltig_bis AT TIME ZONE 'Europe/Berlin', 'YYYY-MM-DD HH24:MI'),
        z.bemerkung
        """;

    // Nur Zuordnungen, deren Fahrzeug zur Firma gehört ($1).
    private const string ZuordnungVon = """
        FROM public.fahrer_fahrzeug_zuordnung z
        JOIN public.fahrzeug f ON f.fahrzeug_id = z.fahrzeug_id
        JOIN public.fahrer fa ON fa.fahrer_id = z.fahrer_id
        JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
        WHERE f.unternehmen_id = $1::bigint
        """;

    public async Task<FahrerzuordnungDaten> LadenAsync(CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var firma = new string?[] { Text(zugriff.UnternehmenId) };

        // Aktuelle zuerst (nach Kennzeichen), danach die Historie, die zuletzt beendete oben.
        var zuordnungen = (await _client.Value.QueryAsync(
                $"SELECT {ZuordnungSpalten} {ZuordnungVon} ORDER BY z.gueltig_bis DESC NULLS FIRST, f.kennzeichen, z.zuordnung_id",
                firma, cancellationToken))
            .Select(ReadZuordnung)
            .ToArray();

        // #83 Fahrerliste: alle Fahrer mit aktivem Konto in dieser Firma. Wer schon ein Fahrzeug fährt,
        // bekommt dessen Kennzeichen dazu, damit der Fuhrparkmanager es beim Auswählen sieht.
        var fahrer = (await _client.Value.QueryAsync("""
                SELECT fa.fahrer_id, trim(concat_ws(' ', b.vorname, b.nachname)), fa.fuehrerscheinklasse, fa.verfuegbarkeit,
                       (SELECT string_agg(f.kennzeichen, ', ' ORDER BY f.kennzeichen)
                        FROM public.fahrer_fahrzeug_zuordnung z
                        JOIN public.fahrzeug f ON f.fahrzeug_id = z.fahrzeug_id
                        WHERE z.fahrer_id = fa.fahrer_id AND z.gueltig_bis IS NULL AND f.unternehmen_id = $1::bigint)
                FROM public.fahrer fa
                JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
                WHERE b.unternehmen_id = $1::bigint AND b.aktiv = true
                ORDER BY lower(b.nachname), lower(b.vorname), fa.fahrer_id
                """, firma, cancellationToken))
            .Select(row => new ZuordnungFahrer(Id(row, 0), Required(row, 1), Required(row, 2), Required(row, 3), row[4]))
            .ToArray();

        var fahrzeuge = (await _client.Value.QueryAsync($"""
                SELECT f.fahrzeug_id, f.kennzeichen, f.fahrzeugtyp, f.status
                FROM public.fahrzeug f
                WHERE f.unternehmen_id = $1::bigint AND {FahrzeugVerfuegbar}
                ORDER BY f.kennzeichen, f.fahrzeug_id
                """, firma, cancellationToken))
            .Select(row => new ZuordnungFahrzeug(Id(row, 0), Required(row, 1), Required(row, 2), Required(row, 3)))
            .ToArray();

        return new FahrerzuordnungDaten(
            zuordnungen.Where(z => z.IstAktuell).ToArray(),
            zuordnungen.Where(z => !z.IstAktuell).ToArray(),
            fahrer,
            fahrzeuge);
    }

    /// <summary>Nur die laufenden Zuordnungen, zum Beispiel für die Spalte „Fahrer“ in F01 (#85).</summary>
    public async Task<IReadOnlyList<Fahrerzuordnung>> AktuelleLadenAsync(CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        return (await _client.Value.QueryAsync(
                $"SELECT {ZuordnungSpalten} {ZuordnungVon} AND z.gueltig_bis IS NULL ORDER BY f.kennzeichen",
                new string?[] { Text(zugriff.UnternehmenId) }, cancellationToken))
            .Select(ReadZuordnung)
            .ToArray();
    }

    /// <summary>
    /// Neue Zuordnung ab jetzt (#83, #84, #87). Fahrzeug und Fahrer müssen zur Firma gehören, das Fahrzeug muss
    /// verfügbar sein. Die Datenbank verhindert zusätzlich Doppelbelegungen, auch bei zwei gleichzeitigen Klicks.
    /// </summary>
    public async Task ZuordnenAsync(long fahrzeugId, long fahrerId, string? bemerkung, CancellationToken cancellationToken = default)
    {
        string? text = BemerkungPruefen(bemerkung);
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);

        // „pruefung“ liefert nur eine Zeile, wenn alles passt. Zuordnung und Protokolleintrag entstehen im selben Befehl.
        const string query = $"""
            WITH pruefung AS (
                SELECT f.fahrzeug_id, f.kennzeichen, f.unternehmen_id, f.abteilung_id, fa.fahrer_id,
                       trim(concat_ws(' ', b.vorname, b.nachname)) AS name
                FROM public.fahrzeug f
                CROSS JOIN public.fahrer fa
                JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
                WHERE f.fahrzeug_id = $1::bigint AND f.unternehmen_id = $3::bigint AND {FahrzeugVerfuegbar}
                  AND fa.fahrer_id = $2::bigint AND b.unternehmen_id = $3::bigint AND b.aktiv = true
            ), neu AS (
                INSERT INTO public.fahrer_fahrzeug_zuordnung (fahrer_id, fahrzeug_id, gueltig_von, bemerkung)
                SELECT p.fahrer_id, p.fahrzeug_id, now(), $4::text FROM pruefung p
                RETURNING zuordnung_id
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
                SELECT CASE WHEN $5::bigint IS NULL THEN 'Entwicklungsansicht' ELSE 'Benutzer' END,
                       'Fahrer zugeordnet', 'Gespeichert', p.name || ' → Fahrzeug ' || p.kennzeichen,
                       p.unternehmen_id, p.abteilung_id, $5::bigint
                FROM pruefung p
            )
            SELECT zuordnung_id FROM neu
            """;

        await SchreibenAsync(query,
            new[] { Text(fahrzeugId), Text(fahrerId), Text(zugriff.UnternehmenId), text, BenutzerId() },
            "Das Fahrzeug ist nicht mehr verfügbar oder hat schon einen Fahrer, oder den Fahrer gibt es nicht mehr. " +
            "Bitte laden Sie die Ansicht neu.",
            cancellationToken);
    }

    /// <summary>
    /// „Fahrer wechseln“ (#86): Die laufende Zuordnung endet jetzt und bleibt in der Historie, die neue beginnt
    /// im selben Augenblick. Beides geschieht in einem Befehl, also ganz oder gar nicht.
    /// </summary>
    public async Task WechselnAsync(long zuordnungId, long neuerFahrerId, string? bemerkung, CancellationToken cancellationToken = default)
    {
        string? text = BemerkungPruefen(bemerkung);
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);

        // „beendet“ läuft nur, wenn es die alte Zuordnung noch gibt UND der neue Fahrer gültig ist.
        // Die Prüfung auf Doppelbelegung wartet bis zum Ende des Befehls (DEFERRABLE in der Migration).
        const string query = """
            WITH alt AS (
                SELECT z.zuordnung_id, z.fahrzeug_id, f.kennzeichen, f.unternehmen_id, f.abteilung_id,
                       trim(concat_ws(' ', b.vorname, b.nachname)) AS name
                FROM public.fahrer_fahrzeug_zuordnung z
                JOIN public.fahrzeug f ON f.fahrzeug_id = z.fahrzeug_id
                JOIN public.fahrer fa ON fa.fahrer_id = z.fahrer_id
                JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
                WHERE z.zuordnung_id = $1::bigint AND f.unternehmen_id = $3::bigint
                  AND z.gueltig_bis IS NULL AND z.fahrer_id <> $2::bigint
            ), neuer_fahrer AS (
                SELECT fa.fahrer_id, trim(concat_ws(' ', b.vorname, b.nachname)) AS name
                FROM public.fahrer fa
                JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
                WHERE fa.fahrer_id = $2::bigint AND b.unternehmen_id = $3::bigint AND b.aktiv = true
            ), beendet AS (
                UPDATE public.fahrer_fahrzeug_zuordnung z SET gueltig_bis = now()
                FROM alt a, neuer_fahrer n
                WHERE z.zuordnung_id = a.zuordnung_id
                RETURNING z.zuordnung_id
            ), neu AS (
                INSERT INTO public.fahrer_fahrzeug_zuordnung (fahrer_id, fahrzeug_id, gueltig_von, bemerkung)
                SELECT n.fahrer_id, a.fahrzeug_id, now(), $4::text FROM alt a, neuer_fahrer n
                RETURNING zuordnung_id
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
                SELECT CASE WHEN $5::bigint IS NULL THEN 'Entwicklungsansicht' ELSE 'Benutzer' END,
                       'Fahrer gewechselt', 'Gespeichert', 'Fahrzeug ' || a.kennzeichen || ': ' || a.name || ' → ' || n.name,
                       a.unternehmen_id, a.abteilung_id, $5::bigint
                FROM alt a, neuer_fahrer n
            )
            SELECT zuordnung_id FROM neu
            """;

        await SchreibenAsync(query,
            new[] { Text(zuordnungId), Text(neuerFahrerId), Text(zugriff.UnternehmenId), text, BenutzerId() },
            "Die Zuordnung wurde inzwischen geändert oder beendet, oder den Fahrer gibt es nicht mehr. " +
            "Bitte laden Sie die Ansicht neu.",
            cancellationToken);
    }

    /// <summary>
    /// „Zuordnung beenden“ (#86): Ende = jetzt. Die Zeile bleibt mit ihrem Zeitraum in der Historie (#88).
    /// False, wenn sie nicht (mehr) läuft oder nicht zur Firma gehört.
    /// </summary>
    public async Task<bool> BeendenAsync(long zuordnungId, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        const string query = """
            WITH beendet AS (
                UPDATE public.fahrer_fahrzeug_zuordnung z SET gueltig_bis = now()
                FROM public.fahrzeug f
                WHERE z.zuordnung_id = $1::bigint AND z.gueltig_bis IS NULL
                  AND f.fahrzeug_id = z.fahrzeug_id AND f.unternehmen_id = $2::bigint
                RETURNING z.zuordnung_id, z.fahrer_id, f.kennzeichen, f.unternehmen_id, f.abteilung_id
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
                SELECT CASE WHEN $3::bigint IS NULL THEN 'Entwicklungsansicht' ELSE 'Benutzer' END,
                       'Fahrerzuordnung beendet', 'Gespeichert',
                       trim(concat_ws(' ', b.vorname, b.nachname)) || ' → Fahrzeug ' || e.kennzeichen,
                       e.unternehmen_id, e.abteilung_id, $3::bigint
                FROM beendet e
                JOIN public.fahrer fa ON fa.fahrer_id = e.fahrer_id
                JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
            )
            SELECT zuordnung_id FROM beendet
            """;
        var rows = await _client.Value.QueryAsync(query,
            new[] { Text(zuordnungId), Text(zugriff.UnternehmenId), BenutzerId() }, cancellationToken);
        return rows.Count == 1;
    }

    // Führt Zuordnen bzw. Wechseln aus. Genau eine neue Zuordnung muss zurückkommen.
    private async Task SchreibenAsync(string query, string?[] parameters, string meldungOhneZeile,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _client.Value.QueryAsync(query, parameters, cancellationToken);
            if (rows.Count != 1)
                throw new ArgumentException(meldungOhneZeile);
        }
        catch (NeonSqlException exception) when (exception.Code == "23P01" &&
            exception.ConstraintName == "fahrer_fahrzeug_zuordnung_keine_doppelbelegung")
        {
            // Jemand anderes hat dem Fahrzeug im selben Moment einen Fahrer zugeordnet.
            throw new FahrzeugDoppeltBelegtException(exception);
        }
    }

    private static string? BemerkungPruefen(string? bemerkung)
    {
        string text = (bemerkung ?? string.Empty).Trim();
        if (text.Length > BemerkungMaximal)
            throw new ArgumentException($"Die Bemerkung darf höchstens {BemerkungMaximal} Zeichen lang sein.");
        return text.Length == 0 ? null : text;
    }

    private static Fahrerzuordnung ReadZuordnung(string?[] row) => new(
        Id(row, 0), Id(row, 1), Required(row, 2), Id(row, 3), Required(row, 4),
        Zeit(Required(row, 5)),
        row[6] is string bis ? Zeit(bis) : null,
        row[7]);

    private static DateTime Zeit(string text) =>
        DateTime.ParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    // Wer speichert: das angemeldete Konto. Nur die Entwicklungsansicht (Debug-Build) hat keines.
    private static string? BenutzerId() => BenutzerSitzung.AktuellerBenutzer is { } benutzer ? Text(benutzer.Id) : null;

    private static long Id(string?[] row, int index) => long.Parse(Required(row, index), CultureInfo.InvariantCulture);

    private static string Text(long wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}

/// <summary>Eine verständliche Meldung statt des technischen PostgreSQL-Fehlers (#87).</summary>
public sealed class FahrzeugDoppeltBelegtException : Exception
{
    public FahrzeugDoppeltBelegtException(Exception innerException)
        : base("Dieses Fahrzeug hat inzwischen schon einen Fahrer. Ein Fahrzeug kann nicht doppelt belegt werden. " +
               "Bitte laden Sie die Ansicht neu.", innerException) { }
}
