using System.Security.Claims;
using Disposio.Api.Services;
using Npgsql;

namespace Disposio.Api.Endpoints;

public sealed record SpracheAnfrage(string? Sprache);
public sealed record VerfuegbarkeitAnfrage(string? Verfuegbarkeit);

/// <summary>
/// Phase 1: Profil des angemeldeten Fahrers (Wireframe M09).
/// Alle IDs kommen aus dem Token. Jede Abfrage prueft erneut, ob Konto, Firma und Fahrerprofil
/// noch gueltig sind; sonst 401, und die App meldet sich ab.
/// </summary>
public static class ProfilEndpoints
{
    // Werte der Spalte fahrer.verfuegbarkeit (Text). Der Schalter in der App nutzt die ersten beiden.
    public static readonly string[] Verfuegbarkeiten = { "Verfügbar", "Nicht verfügbar", "Urlaub" };

    // Gilt fuer b = benutzer, f = fahrer: Konto aktiv, Firma nicht gesperrt, Fahrer-Rolle vorhanden.
    internal const string NochGueltig = """
        b.benutzer_id = $1 AND f.fahrer_id = $2 AND b.unternehmen_id = $3 AND b.aktiv = true
        AND EXISTS (SELECT 1 FROM public.unternehmen u WHERE u.unternehmen_id = b.unternehmen_id AND u.status <> 'Inaktiv')
        AND EXISTS (SELECT 1 FROM public.benutzer_rolle br JOIN public.rolle r ON r.rollen_id = br.rollen_id
                    WHERE br.benutzer_id = b.benutzer_id AND r.unternehmen_id IS NULL AND r.bezeichnung = 'Fahrer')
        """;

    public static void MapProfilEndpoints(this WebApplication app)
    {
        var me = app.MapGroup("/me").RequireAuthorization();
        me.MapGet("", ProfilAsync);
        me.MapPut("/sprache", SpracheAsync);
        me.MapPut("/verfuegbarkeit", VerfuegbarkeitAsync);
    }

    private static async Task<IResult> ProfilAsync(ClaimsPrincipal nutzer, NpgsqlDataSource db)
    {
        await using var befehl = Befehl(db, nutzer, $"""
            SELECT b.vorname, b.nachname, b.email, u.firmenname, f.fuehrerscheinklasse, f.verfuegbarkeit, b.sprache
            FROM public.benutzer b
            JOIN public.fahrer f ON f.benutzer_id = b.benutzer_id
            JOIN public.unternehmen u ON u.unternehmen_id = b.unternehmen_id
            WHERE {NochGueltig}
            """);
        await using var leser = await befehl.ExecuteReaderAsync();
        if (!await leser.ReadAsync())
        {
            return Results.Unauthorized();
        }

        return Results.Ok(new
        {
            vorname = leser.GetString(0),
            nachname = leser.GetString(1),
            email = leser.GetString(2),
            firmenname = leser.GetString(3),
            fuehrerscheinklasse = leser.GetString(4),
            verfuegbarkeit = leser.GetString(5),
            sprache = leser.GetString(6),
        });
    }

    private static async Task<IResult> SpracheAsync(SpracheAnfrage anfrage, ClaimsPrincipal nutzer, NpgsqlDataSource db)
    {
        var sprache = (anfrage.Sprache ?? "").Trim().ToLowerInvariant();
        if (!AuthEndpoints.Sprachen.Contains(sprache))
        {
            return Results.Json(new { fehler = "sprache_ungueltig" }, statusCode: 400);
        }

        await using var befehl = Befehl(db, nutzer, $"""
            UPDATE public.benutzer b SET sprache = $4
            FROM public.fahrer f
            WHERE f.benutzer_id = b.benutzer_id AND {NochGueltig}
            """, sprache);
        return await befehl.ExecuteNonQueryAsync() == 1 ? Results.NoContent() : Results.Unauthorized();
    }

    private static async Task<IResult> VerfuegbarkeitAsync(VerfuegbarkeitAnfrage anfrage, ClaimsPrincipal nutzer, NpgsqlDataSource db)
    {
        var wert = (anfrage.Verfuegbarkeit ?? "").Trim();
        if (!Verfuegbarkeiten.Contains(wert))
        {
            return Results.Json(new { fehler = "verfuegbarkeit_ungueltig" }, statusCode: 400);
        }

        await using var befehl = Befehl(db, nutzer, $"""
            UPDATE public.fahrer f SET verfuegbarkeit = $4
            FROM public.benutzer b
            WHERE f.benutzer_id = b.benutzer_id AND {NochGueltig}
            """, wert);
        return await befehl.ExecuteNonQueryAsync() == 1 ? Results.NoContent() : Results.Unauthorized();
    }

    // $1 benutzer_id, $2 fahrer_id, $3 unternehmen_id aus dem Token, danach optional ein Wert ($4).
    private static NpgsqlCommand Befehl(NpgsqlDataSource db, ClaimsPrincipal nutzer, string sql, string? wert = null)
    {
        var befehl = db.CreateCommand(sql);
        befehl.Parameters.Add(new() { Value = AnmeldeClaims.Lesen(nutzer, AnmeldeClaims.BenutzerId) });
        befehl.Parameters.Add(new() { Value = AnmeldeClaims.Lesen(nutzer, AnmeldeClaims.FahrerId) });
        befehl.Parameters.Add(new() { Value = AnmeldeClaims.Lesen(nutzer, AnmeldeClaims.UnternehmenId) });
        if (wert is not null)
        {
            befehl.Parameters.Add(new() { Value = wert });
        }
        return befehl;
    }
}
