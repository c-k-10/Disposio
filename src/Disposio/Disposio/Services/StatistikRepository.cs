using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>Vertrag zwischen der Statistik-Ansicht (G02) und der Datenbank. Liest nur, ändert nichts.</summary>
public interface IStatistikRepository
{
    Task<IReadOnlyList<DateOnly>> MonateLadenAsync(CancellationToken cancellationToken = default);
    Task<MonatsStatistik> MonatLadenAsync(DateOnly monat, CancellationToken cancellationToken = default);
}

/// <summary>
/// SQL für die Monatsstatistik. Jede Methode prüft zuerst mit AuthService.StatistikZugriffPruefenAsync
/// (Inhaber/Geschäftsführer oder Kundenadmin, auch bei gesperrter Firma) und verwendet danach nur diese Firma.
/// Monatsgrenzen gelten in deutscher Zeit (Europe/Berlin), auch für Zeitstempel aus der Datenbank.
/// </summary>
public sealed class StatistikRepository : IStatistikRepository
{
    // Höchstens zehn Jahre in der Download-Liste.
    private const int MaximaleMonate = 120;

    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    /// <summary>
    /// Alle Monate vom ersten Monat mit Daten der Firma bis zum aktuellen Monat, der neueste zuerst.
    /// Ohne Daten gibt es nur den aktuellen Monat.
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> MonateLadenAsync(CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.StatistikZugriffPruefenAsync(cancellationToken);

        // LEAST überspringt leere Werte. Erst wenn es gar keine Daten gibt, gilt der aktuelle Monat.
        const string query = """
            WITH beginn AS (
                SELECT date_trunc('month', COALESCE(LEAST(
                    (SELECT min(p.zeitpunkt AT TIME ZONE 'Europe/Berlin') FROM public.aktionsprotokoll p
                     WHERE p.unternehmen_id = $1::bigint),
                    (SELECT min(t.startzeit AT TIME ZONE 'Europe/Berlin') FROM public.tour t
                     WHERE t.unternehmen_id = $1::bigint),
                    (SELECT min(a.lieferdatum)::timestamp FROM public.auftrag a WHERE a.unternehmen_id = $1::bigint),
                    (SELECT min(x.datum)::timestamp FROM public.tankvorgang x
                     JOIN public.fahrzeug f ON f.fahrzeug_id = x.fahrzeug_id WHERE f.unternehmen_id = $1::bigint),
                    (SELECT min(x.datum)::timestamp FROM public.wartung x
                     JOIN public.fahrzeug f ON f.fahrzeug_id = x.fahrzeug_id WHERE f.unternehmen_id = $1::bigint),
                    (SELECT min(x.datum)::timestamp FROM public.schadensmeldung x
                     JOIN public.fahrzeug f ON f.fahrzeug_id = x.fahrzeug_id WHERE f.unternehmen_id = $1::bigint)
                ), now() AT TIME ZONE 'Europe/Berlin')) AS monat
            )
            SELECT to_char(m, 'YYYY-MM-DD')
            FROM beginn, generate_series(beginn.monat, date_trunc('month', now() AT TIME ZONE 'Europe/Berlin'),
                                         interval '1 month') AS m
            ORDER BY m DESC
            LIMIT $2::integer
            """;
        var rows = await _client.Value.QueryAsync(query,
            new string?[] { Text(zugriff.UnternehmenId), MaximaleMonate.ToString(CultureInfo.InvariantCulture) },
            cancellationToken);

        var monate = new List<DateOnly>();
        foreach (var row in rows)
        {
            monate.Add(DateOnly.ParseExact(Required(row, 0), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return monate;
    }

    public async Task<MonatsStatistik> MonatLadenAsync(DateOnly monat, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.StatistikZugriffPruefenAsync(cancellationToken);
        var monatserster = new DateOnly(monat.Year, monat.Month, 1);

        // Ein Befehl für alle Zahlen: z ist der Monat [von, bis). Kosten ohne Eintrag zählen als 0.
        const string query = """
            WITH z AS (
                SELECT $2::date AS von, ($2::date + interval '1 month')::date AS bis
            ), tank AS (
                SELECT count(*) AS anzahl, COALESCE(sum(x.liter), 0) AS liter, COALESCE(sum(x.kosten), 0) AS kosten
                FROM public.tankvorgang x JOIN public.fahrzeug f ON f.fahrzeug_id = x.fahrzeug_id CROSS JOIN z
                WHERE f.unternehmen_id = $1::bigint AND x.datum >= z.von AND x.datum < z.bis
            ), wartung AS (
                SELECT count(*) AS anzahl, COALESCE(sum(x.kosten), 0) AS kosten
                FROM public.wartung x JOIN public.fahrzeug f ON f.fahrzeug_id = x.fahrzeug_id CROSS JOIN z
                WHERE f.unternehmen_id = $1::bigint AND x.datum >= z.von AND x.datum < z.bis
            ), schaden AS (
                SELECT count(*) AS anzahl, COALESCE(sum(x.kosten), 0) AS kosten
                FROM public.schadensmeldung x JOIN public.fahrzeug f ON f.fahrzeug_id = x.fahrzeug_id CROSS JOIN z
                WHERE f.unternehmen_id = $1::bigint AND x.datum >= z.von AND x.datum < z.bis
            )
            SELECT
                (SELECT u.firmenname FROM public.unternehmen u WHERE u.unternehmen_id = $1::bigint),
                (SELECT count(*) FROM public.tour t CROSS JOIN z WHERE t.unternehmen_id = $1::bigint
                    AND t.startzeit AT TIME ZONE 'Europe/Berlin' >= z.von
                    AND t.startzeit AT TIME ZONE 'Europe/Berlin' < z.bis),
                (SELECT count(*) FROM public.auftrag a CROSS JOIN z WHERE a.unternehmen_id = $1::bigint
                    AND a.lieferdatum >= z.von AND a.lieferdatum < z.bis),
                tank.anzahl, tank.liter, tank.kosten,
                wartung.anzahl, wartung.kosten,
                schaden.anzahl, schaden.kosten,
                (SELECT count(*) FROM public.aktionsprotokoll p CROSS JOIN z WHERE p.unternehmen_id = $1::bigint
                    AND p.zeitpunkt AT TIME ZONE 'Europe/Berlin' >= z.von
                    AND p.zeitpunkt AT TIME ZONE 'Europe/Berlin' < z.bis)
            FROM tank, wartung, schaden
            """;
        var rows = await _client.Value.QueryAsync(query, new string?[]
        {
            Text(zugriff.UnternehmenId),
            monatserster.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        }, cancellationToken);
        if (rows.Count != 1)
        {
            throw new InvalidOperationException("Die Datenbank hat keine Statistik geliefert.");
        }

        // Neon liefert alle Werte als Text, Zahlen mit Punkt als Dezimaltrenner. Deshalb InvariantCulture.
        var row = rows[0];
        return new MonatsStatistik(
            monatserster,
            Required(row, 0),
            Ganzzahl(row, 1), Ganzzahl(row, 2),
            Ganzzahl(row, 3), Kommazahl(row, 4), Kommazahl(row, 5),
            Ganzzahl(row, 6), Kommazahl(row, 7),
            Ganzzahl(row, 8), Kommazahl(row, 9),
            Ganzzahl(row, 10));
    }

    private static int Ganzzahl(string?[] row, int index) => int.Parse(Required(row, index), CultureInfo.InvariantCulture);

    private static decimal Kommazahl(string?[] row, int index) => decimal.Parse(Required(row, index), CultureInfo.InvariantCulture);

    private static string Text(long wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}
