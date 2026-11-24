using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// F06 „Tankvorgänge“: Tankvorgänge der Fahrer-App je Fahrzeug für den Fuhrparkmanager (nur lesen).
/// Bearbeiten (Status, Notiz, Korrektur) bleibt in D15 „Meldungen“ beim Disponenten.
/// Wie überall: erst die Rolle prüfen, dann nur Fahrzeuge der eigenen Firma.
/// </summary>
public sealed class TankvorgangRepository
{
    private const FirmenRolle Rolle = FirmenRolle.Fuhrparkmanager;

    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    // $1 Firma, $2 Fahrzeug (leer = alle), $3 ab Datum (leer = alles). Neueste zuerst.
    private const string TankQuery = """
        SELECT t.tankvorgang_id, t.fahrzeug_id, fz.kennzeichen, to_char(t.datum, 'YYYY-MM-DD'), t.liter, t.kosten,
               t.kilometerstand, COALESCE(t.tankstelle, ''), COALESCE(t.kraftstoff, ''), COALESCE(t.bearbeitungsstatus, ''),
               NULLIF(btrim(concat_ws(' ', b.vorname, b.nachname)), ''), NULLIF(btrim(t.bearbeitungsnotiz), ''),
               d.dateireferenz, COALESCE(d.anzahl, 0)
        FROM public.tankvorgang t
        JOIN public.fahrzeug fz ON fz.fahrzeug_id = t.fahrzeug_id AND fz.unternehmen_id = $1::bigint
        LEFT JOIN public.fahrer fa ON fa.fahrer_id = t.fahrer_id
        LEFT JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id AND b.unternehmen_id = $1::bigint
        LEFT JOIN LATERAL (
            SELECT dk.dateireferenz, count(*) OVER () AS anzahl
            FROM public.dokument dk
            WHERE dk.tankvorgang_id = t.tankvorgang_id
            ORDER BY dk.hochgeladen_am DESC, dk.dokument_id DESC
            LIMIT 1
        ) d ON true
        WHERE ($2::bigint IS NULL OR t.fahrzeug_id = $2::bigint)
          AND ($3::date IS NULL OR t.datum >= $3::date)
        ORDER BY t.datum DESC, t.tankvorgang_id DESC
        LIMIT 1000
        """;

    private const string FahrzeugeQuery = """
        SELECT fahrzeug_id, kennzeichen, aktiv FROM public.fahrzeug
        WHERE unternehmen_id = $1::bigint
        ORDER BY NOT aktiv, kennzeichen, fahrzeug_id
        """;

    public async Task<IReadOnlyList<TankFahrzeug>> FahrzeugeLadenAsync(CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var rows = await _client.Value.QueryAsync(FahrzeugeQuery, new string?[] { Text(zugriff.UnternehmenId) }, cancellationToken);
        return rows.Select(r => new TankFahrzeug(Id(r, 0), Required(r, 1), r[2] == "t")).ToArray();
    }

    /// <param name="fahrzeugId">Null = alle Fahrzeuge der Firma.</param>
    /// <param name="ab">Null = ohne Zeitgrenze.</param>
    public async Task<IReadOnlyList<Tankvorgang>> LadenAsync(long? fahrzeugId, DateOnly? ab,
        CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var rows = await _client.Value.QueryAsync(TankQuery, new string?[]
        {
            Text(zugriff.UnternehmenId),
            fahrzeugId is long f ? Text(f) : null,
            ab?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        }, cancellationToken);

        return rows.Select(r => new Tankvorgang(
                Id(r, 0),
                Id(r, 1),
                Required(r, 2),
                DateOnly.ParseExact(Required(r, 3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Zahl(r[4]) ?? 0,
                Zahl(r[5]) ?? 0,
                r[6] is string km ? (int)decimal.Parse(km, CultureInfo.InvariantCulture) : null,
                r[7] ?? "",
                r[8] ?? "",
                r[9] ?? "",
                r[10],
                r[11],
                r[12],
                int.Parse(r[13] ?? "0", CultureInfo.InvariantCulture)))
            .ToArray();
    }

    // Neon liefert Zahlen mit Punkt als Dezimaltrenner. Deshalb InvariantCulture.
    private static decimal? Zahl(string? text) =>
        text is null ? null : decimal.Parse(text, CultureInfo.InvariantCulture);

    private static long Id(string?[] row, int index) => long.Parse(Required(row, index), CultureInfo.InvariantCulture);

    private static string Text(long wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}
