using Disposio.Api.Services;
using Npgsql;

namespace Disposio.Api.Endpoints;

public sealed record LoginAnfrage(string? Kennung, string? Email, string? Passwort, string? Sprache);

/// <summary>
/// Phase 1: Anmeldung der Fahrer-App (Wireframe M01).
/// Firma, E-Mail und Passwort werden wie in der WinUI-App geprueft. Nur Konten mit Rolle Fahrer
/// und Fahrerprofil bekommen ein Token.
/// </summary>
public static class AuthEndpoints
{
    public static readonly string[] Sprachen = { "de", "en", "pl" };

    // Falsche Firma, E-Mail oder Passwort werden absichtlich nicht unterschieden.
    private static IResult Fehlgeschlagen() => Results.Json(new { fehler = "anmeldung_fehlgeschlagen" }, statusCode: 401);
    private static IResult Verboten(string fehler) => Results.Json(new { fehler }, statusCode: 403);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", LoginAsync);
    }

    private static async Task<IResult> LoginAsync(LoginAnfrage anfrage, NpgsqlDataSource db,
        PasswortPruefer pruefer, TokenService tokens)
    {
        var kennung = (anfrage.Kennung ?? "").Trim();
        var email = (anfrage.Email ?? "").Trim().ToLowerInvariant();
        var passwort = anfrage.Passwort ?? "";
        var sprache = (anfrage.Sprache ?? "de").Trim().ToLowerInvariant();

        if (!Sprachen.Contains(sprache))
        {
            return Results.Json(new { fehler = "sprache_ungueltig" }, statusCode: 400);
        }
        if (kennung.Length is 0 or > 200 || email.Length is 0 or > 254 || passwort.Length is 0 or > 128)
        {
            return Fehlgeschlagen();
        }

        // Kennung oder ausgeschriebener Firmenname, wie in der WinUI-App. LIMIT 2 erkennt mehrdeutige Treffer.
        await using var befehl = db.CreateCommand("""
            SELECT b.benutzer_id, b.vorname, b.nachname, b.aktiv, b.auth_user_id, b.passwort_hash,
                   u.unternehmen_id, u.status, f.fahrer_id,
                   EXISTS (SELECT 1 FROM public.benutzer_rolle br
                           JOIN public.rolle r ON r.rollen_id = br.rollen_id
                           WHERE br.benutzer_id = b.benutzer_id AND r.unternehmen_id IS NULL
                             AND r.bezeichnung = 'Fahrer') AS ist_fahrer
            FROM public.benutzer b
            JOIN public.unternehmen u ON u.unternehmen_id = b.unternehmen_id
            LEFT JOIN public.fahrer f ON f.benutzer_id = b.benutzer_id
            WHERE lower(btrim(b.email)) = $1
              AND (lower(btrim(u.kennung)) = lower($2) OR lower(btrim(u.firmenname)) = lower($2))
            LIMIT 2
            """);
        befehl.Parameters.Add(new() { Value = email });
        befehl.Parameters.Add(new() { Value = kennung });

        var treffer = new List<Konto>();
        await using (var leser = await befehl.ExecuteReaderAsync())
        {
            while (await leser.ReadAsync())
            {
                treffer.Add(new Konto(
                    leser.GetInt64(0), leser.GetString(1), leser.GetString(2), leser.GetBoolean(3),
                    leser.IsDBNull(4) ? null : leser.GetString(4), leser.GetString(5),
                    leser.GetInt64(6), leser.GetString(7),
                    leser.IsDBNull(8) ? null : leser.GetInt64(8), leser.GetBoolean(9)));
            }
        }

        if (treffer.Count != 1)
        {
            return Fehlgeschlagen();
        }

        var konto = treffer[0];
        if (!await pruefer.PasstAsync(email, passwort, konto.AuthUserId, konto.PasswortHash))
        {
            return Fehlgeschlagen();
        }

        // Erst nach dem Passwortbeweis eigene Meldungen, damit Fremde nichts erfahren.
        if (!konto.Aktiv)
        {
            return Verboten("konto_deaktiviert");
        }
        if (konto.FirmenStatus == "Inaktiv")
        {
            return Verboten("firma_gesperrt");
        }
        if (!konto.IstFahrer || konto.FahrerId is not long fahrerId)
        {
            return Verboten("kein_fahrerkonto");
        }

        // Gewaehlte Sprache wird zur Kontosprache (FA-14).
        await using (var update = db.CreateCommand("UPDATE public.benutzer SET sprache = $1 WHERE benutzer_id = $2"))
        {
            update.Parameters.Add(new() { Value = sprache });
            update.Parameters.Add(new() { Value = konto.BenutzerId });
            await update.ExecuteNonQueryAsync();
        }

        return Results.Ok(new
        {
            token = tokens.Erstellen(konto.BenutzerId, fahrerId, konto.UnternehmenId),
            benutzerId = konto.BenutzerId,
            fahrerId,
            vorname = konto.Vorname,
            nachname = konto.Nachname,
            sprache,
        });
    }

    private sealed record Konto(long BenutzerId, string Vorname, string Nachname, bool Aktiv,
        string? AuthUserId, string PasswortHash, long UnternehmenId, string FirmenStatus,
        long? FahrerId, bool IstFahrer);
}
