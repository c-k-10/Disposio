using System.Security.Claims;
using Disposio.Api.Services;
using Npgsql;

namespace Disposio.Api.Endpoints;

public sealed record PositionAnfrage(decimal? Breitengrad, decimal? Laengengrad, DateTimeOffset? ErfasstAm, decimal? GenauigkeitM);

/// <summary>
/// Phase 3: GPS-Positionen der laufenden Tour (Tabelle gps_position).
/// Die App sammelt Punkte und sendet sie alle 60 Sekunden gesammelt.
/// Nur fuer eigene Touren mit Status Unterwegs, sonst 409: die App leert dann ihren Puffer und stoppt GPS.
/// </summary>
public static class PositionEndpoints
{
    public const int MaxPunkte = 100;

    public static void MapPositionEndpoints(this WebApplication app)
    {
        app.MapPost("/touren/{id:long}/positionen", SpeichernAsync).RequireAuthorization();
    }

    private static async Task<IResult> SpeichernAsync(long id, List<PositionAnfrage>? punkte, ClaimsPrincipal nutzer,
        NpgsqlDataSource db, EtaRechner eta)
    {
        if (punkte is null || punkte.Count is 0 or > MaxPunkte)
        {
            return Results.Json(new { fehler = "eingabe_ungueltig" }, statusCode: 400);
        }

        var ids = TourEndpoints.Ids.Aus(nutzer);
        await using var verbindung = await db.OpenConnectionAsync();
        if (!await TourEndpoints.GueltigAsync(verbindung, null, ids))
        {
            return Results.Unauthorized();
        }

        await using (var befehl = new NpgsqlCommand("""
            SELECT status FROM public.tour WHERE tour_id = $1 AND fahrer_id = $2 AND unternehmen_id = $3
            """, verbindung))
        {
            befehl.Parameters.Add(new() { Value = id });
            befehl.Parameters.Add(new() { Value = ids.FahrerId });
            befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
            var status = await befehl.ExecuteScalarAsync() as string;
            if (status is null)
            {
                return Results.Json(new { fehler = "tour_nicht_gefunden" }, statusCode: 404);
            }
            if (status != TourEndpoints.TourUnterwegs)
            {
                return Results.Json(new { fehler = "tour_nicht_unterwegs", status }, statusCode: 409);
            }
        }

        // Unplausible Punkte verwerfen: Koordinaten ausserhalb des Bereichs,
        // Zeit in der Zukunft (1 Minute Spielraum fuer die Uhr des Handys) oder aelter als 24 Stunden
        var jetzt = DateTimeOffset.UtcNow;
        var gueltig = punkte.Where(p =>
                p.Breitengrad is >= -90 and <= 90
                && p.Laengengrad is >= -180 and <= 180
                && p.ErfasstAm is { } zeit && zeit <= jetzt.AddMinutes(1) && zeit >= jetzt.AddHours(-24))
            .ToList();

        if (gueltig.Count > 0)
        {
            await using var batch = new NpgsqlBatch(verbindung);
            foreach (var p in gueltig)
            {
                var befehl = new NpgsqlBatchCommand("""
                    INSERT INTO public.gps_position (tour_id, fahrer_id, breitengrad, laengengrad, erfasst_am, empfangen_am, genauigkeit_m)
                    VALUES ($1, $2, $3, $4, $5, now(), $6)
                    """);
                befehl.Parameters.Add(new() { Value = id });
                befehl.Parameters.Add(new() { Value = ids.FahrerId });
                befehl.Parameters.Add(new() { Value = Math.Round(p.Breitengrad!.Value, 6) });
                befehl.Parameters.Add(new() { Value = Math.Round(p.Laengengrad!.Value, 6) });
                befehl.Parameters.Add(new() { Value = p.ErfasstAm!.Value.UtcDateTime });
                befehl.Parameters.Add(new()
                {
                    Value = p.GenauigkeitM is >= 0 and < 100000 ? Math.Round(p.GenauigkeitM.Value, 1) : (object)DBNull.Value,
                    NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Numeric,
                });
                batch.BatchCommands.Add(befehl);
            }
            await batch.ExecuteNonQueryAsync();

            // ETA nach dem Speichern; Fehler dabei aendern nichts an der Antwort
            eta.Anstossen(id);
        }

        return Results.Ok(new { gespeichert = gueltig.Count, verworfen = punkte.Count - gueltig.Count });
    }
}
