using System.Security.Claims;
using Disposio.Api.Services;
using Npgsql;

namespace Disposio.Api.Endpoints;

public sealed record NachweisAnfrage(long? AuftragId, int? Anzahl, string? Empfaengername, string? Bestaetigungsart,
    string? Ausnahmegrund, string? Bemerkung, DateTimeOffset? Zeitpunkt, string? ClientId);

/// <summary>
/// Phase 5: Ablieferung dokumentieren (Wireframes M06/M07, Tabelle abliefernachweis).
/// Ein Nachweis je Zustellauftrag eines Stopps. Fotos und Unterschrift kommen danach ueber POST /dokumente.
/// Ein Stopp gehoert dem Fahrer, wenn seine Tour zu fahrer_id und unternehmen_id aus dem Token passt, sonst 404.
/// </summary>
public static class AblieferungEndpoints
{
    // Werte fuer abliefernachweis.bestaetigungsart
    public const string Unterschrift = "Unterschrift";
    public const string Foto = "Foto";
    public const string OhneEmpfaenger = "Ohne Empfänger";
    public static readonly string[] Bestaetigungsarten = { Unterschrift, Foto, OhneEmpfaenger };

    // Werte fuer abliefernachweis.ausnahmegrund. Pflicht, wenn weniger als die Soll-Anzahl abgeliefert wurde.
    public static readonly string[] Ausnahmegruende = { "Beschädigt", "Fehlmenge", "Annahme verweigert", "Sonstiges" };

    private const string Zustellung = "Zustellung";

    private static IResult Fehler(string fehler, int status) => Results.Json(new { fehler }, statusCode: status);
    private static IResult StoppFehlt() => Fehler("stopp_nicht_gefunden", 404);

    public static void MapAblieferungEndpoints(this WebApplication app)
    {
        var stopps = app.MapGroup("/stopps").RequireAuthorization();
        stopps.MapGet("/{id:long}/auftraege", AuftraegeAsync);
        stopps.MapPost("/{id:long}/nachweise", NachweisAsync);
    }

    /// <summary>M06: Zustellauftraege des Stopps mit Soll-Anzahl und dem letzten Nachweis (falls schon gesendet).</summary>
    private static async Task<IResult> AuftraegeAsync(long id, ClaimsPrincipal nutzer, NpgsqlDataSource db)
    {
        var ids = TourEndpoints.Ids.Aus(nutzer);
        await using var verbindung = await db.OpenConnectionAsync();
        if (!await TourEndpoints.GueltigAsync(verbindung, null, ids))
        {
            return Results.Unauthorized();
        }
        if (!await StoppGehoertAsync(verbindung, null, ids, id))
        {
            return StoppFehlt();
        }

        await using var befehl = new NpgsqlCommand("""
            SELECT a.auftrag_id, a.auftragsnummer, k.firmenname, a.anzahl, a.verpackungstyp,
                   n.nachweis_id, n.tatsaechlich_abgelieferte_anzahl, n.zeitpunkt
            FROM public.stopp_auftrag sa
            JOIN public.auftrag a ON a.auftrag_id = sa.auftrag_id
            JOIN public.kunde k ON k.kunde_id = a.kunde_id
            LEFT JOIN LATERAL (
                SELECT x.nachweis_id, x.tatsaechlich_abgelieferte_anzahl, x.zeitpunkt FROM public.abliefernachweis x
                WHERE x.stopp_id = sa.stopp_id AND x.auftrag_id = sa.auftrag_id
                ORDER BY x.zeitpunkt DESC, x.nachweis_id DESC LIMIT 1) n ON true
            WHERE sa.stopp_id = $1 AND sa.vorgang_art = $2 AND a.unternehmen_id = $3
            ORDER BY a.auftragsnummer
            """, verbindung);
        befehl.Parameters.Add(new() { Value = id });
        befehl.Parameters.Add(new() { Value = Zustellung });
        befehl.Parameters.Add(new() { Value = ids.UnternehmenId });

        var liste = new List<object>();
        await using var leser = await befehl.ExecuteReaderAsync();
        while (await leser.ReadAsync())
        {
            liste.Add(new
            {
                auftragId = leser.GetInt64(0),
                auftragsnummer = leser.GetString(1),
                kunde = leser.GetString(2),
                sollAnzahl = leser.GetInt32(3),
                verpackungstyp = leser.GetString(4),
                nachweis = leser.IsDBNull(5) ? null : new
                {
                    nachweisId = leser.GetInt64(5),
                    anzahl = leser.GetInt32(6),
                    zeitpunkt = leser.GetDateTime(7),
                },
            });
        }
        return Results.Ok(liste);
    }

    /// <summary>
    /// M06/M07: Nachweis fuer einen Zustellauftrag speichern. Weniger als die Soll-Anzahl nur mit Ausnahmegrund.
    /// Doppelt gesendet (gleiche clientId oder gleicher Auftrag mit gleichem Zeitpunkt)? Dann der vorhandene Nachweis.
    /// </summary>
    private static async Task<IResult> NachweisAsync(long id, NachweisAnfrage anfrage, ClaimsPrincipal nutzer,
        NpgsqlDataSource db, Wiederholschutz schutz)
    {
        var ids = TourEndpoints.Ids.Aus(nutzer);
        var clientId = Wiederholschutz.Pruefen(anfrage.ClientId);
        var art = (anfrage.Bestaetigungsart ?? "").Trim();
        var empfaenger = (anfrage.Empfaengername ?? "").Trim();
        var grund = (anfrage.Ausnahmegrund ?? "").Trim();
        var bemerkung = (anfrage.Bemerkung ?? "").Trim();
        var jetzt = DateTimeOffset.UtcNow;
        if (clientId is null || anfrage.AuftragId is not long auftragId || anfrage.Anzahl is not int anzahl || anzahl < 0
            || !Bestaetigungsarten.Contains(art)
            || empfaenger.Length > 100 || (art == Unterschrift && empfaenger.Length == 0)
            || (grund.Length > 0 && !Ausnahmegruende.Contains(grund))
            || bemerkung.Length > 1000
            || anfrage.Zeitpunkt is not { } zeitpunkt || zeitpunkt > jetzt.AddMinutes(5) || zeitpunkt < jetzt.AddDays(-7))
        {
            return Fehler("eingabe_ungueltig", 400);
        }

        if (schutz.Finden("nachweis", ids.BenutzerId, clientId) is long bekannt)
        {
            return Results.Ok(new { nachweisId = bekannt });
        }

        await using var verbindung = await db.OpenConnectionAsync();
        await using var tx = await verbindung.BeginTransactionAsync();
        if (!await TourEndpoints.GueltigAsync(verbindung, tx, ids))
        {
            return Results.Unauthorized();
        }
        if (!await StoppGehoertAsync(verbindung, tx, ids, id))
        {
            return StoppFehlt();
        }

        int soll;
        await using (var befehl = new NpgsqlCommand("""
            SELECT a.anzahl FROM public.stopp_auftrag sa
            JOIN public.auftrag a ON a.auftrag_id = sa.auftrag_id
            WHERE sa.stopp_id = $1 AND sa.auftrag_id = $2 AND sa.vorgang_art = $3 AND a.unternehmen_id = $4
            """, verbindung, tx))
        {
            befehl.Parameters.Add(new() { Value = id });
            befehl.Parameters.Add(new() { Value = auftragId });
            befehl.Parameters.Add(new() { Value = Zustellung });
            befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
            if (await befehl.ExecuteScalarAsync() is not int gelesen)
            {
                return Fehler("auftrag_nicht_gefunden", 404);
            }
            soll = gelesen;
        }
        if (anzahl > soll)
        {
            return Fehler("anzahl_zu_hoch", 400);
        }
        if (anzahl < soll && grund.Length == 0)
        {
            return Fehler("ausnahmegrund_fehlt", 400);
        }

        // Nach einem Neustart der API: gleicher Auftrag, gleicher Fahrer, gleicher Zeitpunkt = derselbe Nachweis
        await using (var befehl = new NpgsqlCommand("""
            SELECT nachweis_id FROM public.abliefernachweis
            WHERE stopp_id = $1 AND auftrag_id = $2 AND fahrer_id = $3 AND zeitpunkt = $4
            """, verbindung, tx))
        {
            befehl.Parameters.Add(new() { Value = id });
            befehl.Parameters.Add(new() { Value = auftragId });
            befehl.Parameters.Add(new() { Value = ids.FahrerId });
            befehl.Parameters.Add(new() { Value = zeitpunkt.UtcDateTime });
            if (await befehl.ExecuteScalarAsync() is long vorhanden)
            {
                schutz.Merken("nachweis", ids.BenutzerId, clientId, vorhanden);
                return Results.Ok(new { nachweisId = vorhanden });
            }
        }

        long nachweisId;
        await using (var befehl = new NpgsqlCommand("""
            INSERT INTO public.abliefernachweis (stopp_id, auftrag_id, fahrer_id, zeitpunkt, tatsaechlich_abgelieferte_anzahl,
                                                 empfaengername, bestaetigungsart, ausnahmegrund, bemerkung)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            RETURNING nachweis_id
            """, verbindung, tx))
        {
            befehl.Parameters.Add(new() { Value = id });
            befehl.Parameters.Add(new() { Value = auftragId });
            befehl.Parameters.Add(new() { Value = ids.FahrerId });
            befehl.Parameters.Add(new() { Value = zeitpunkt.UtcDateTime });
            befehl.Parameters.Add(new() { Value = anzahl });
            befehl.Parameters.Add(Text(empfaenger));
            befehl.Parameters.Add(new() { Value = art });
            befehl.Parameters.Add(Text(grund));
            befehl.Parameters.Add(Text(bemerkung));
            nachweisId = (long)(await befehl.ExecuteScalarAsync())!;
        }
        await tx.CommitAsync();
        schutz.Merken("nachweis", ids.BenutzerId, clientId, nachweisId);

        return Results.Created($"/stopps/{id}/auftraege", new { nachweisId });
    }

    /// <summary>Leerer Text wird als NULL gespeichert.</summary>
    internal static NpgsqlParameter Text(string wert) => new()
    {
        Value = wert.Length == 0 ? DBNull.Value : wert,
        NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text,
    };

    private static async Task<bool> StoppGehoertAsync(NpgsqlConnection verbindung, NpgsqlTransaction? tx,
        TourEndpoints.Ids ids, long stoppId)
    {
        await using var befehl = new NpgsqlCommand("""
            SELECT 1 FROM public.tourstopp s JOIN public.tour t ON t.tour_id = s.tour_id
            WHERE s.stopp_id = $1 AND t.fahrer_id = $2 AND t.unternehmen_id = $3
            """, verbindung, tx);
        befehl.Parameters.Add(new() { Value = stoppId });
        befehl.Parameters.Add(new() { Value = ids.FahrerId });
        befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
        return await befehl.ExecuteScalarAsync() is not null;
    }
}
