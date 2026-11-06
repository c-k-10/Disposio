using System.Security.Claims;
using System.Text.RegularExpressions;
using Disposio.Api.Services;
using Npgsql;

namespace Disposio.Api.Endpoints;

/// <summary>
/// Phase 5: Fotos, Unterschrift und Belege (Tabelle dokument). Die App sendet multipart/form-data mit
/// datei, art, clientId und genau einem Eigentuemer: nachweisId, tankvorgangId oder schadenId.
/// Die API laedt die Datei zu Cloudinary hoch (siehe BildSpeicher) und speichert die https-Adresse in dateireferenz.
/// Der Eigentuemer muss vom Fahrer stammen, sonst 404.
/// </summary>
public static partial class DokumentEndpoints
{
    public const long MaxBytes = 10 * 1024 * 1024;

    // Erlaubte Dokumentarten je Eigentuemer
    private static readonly Dictionary<string, string[]> Arten = new()
    {
        ["nachweisId"] = new[] { "Foto", "Unterschrift", "Lieferschein" },
        ["tankvorgangId"] = new[] { "Tankbeleg" },
        ["schadenId"] = new[] { "Schadenfoto" },
    };

    private static IResult Fehler(string fehler, int status) => Results.Json(new { fehler }, statusCode: status);

    public static void MapDokumentEndpoints(this WebApplication app)
    {
        app.MapPost("/dokumente", HochladenAsync).RequireAuthorization();
    }

    private static async Task<IResult> HochladenAsync(HttpRequest request, ClaimsPrincipal nutzer, NpgsqlDataSource db,
        BildSpeicher speicher, Wiederholschutz schutz)
    {
        var ids = TourEndpoints.Ids.Aus(nutzer);
        if (!request.HasFormContentType)
        {
            return Fehler("eingabe_ungueltig", 400);
        }
        var formular = await request.ReadFormAsync();
        var clientId = Wiederholschutz.Pruefen(formular["clientId"]);
        var art = formular["art"].ToString().Trim();
        var datei = formular.Files.GetFile("datei");

        // Genau ein Eigentuemer
        var eigentuemer = Arten.Keys.Where(k => !string.IsNullOrWhiteSpace(formular[k])).ToList();
        if (clientId is null || datei is null || eigentuemer.Count != 1
            || !long.TryParse(formular[eigentuemer[0]], out var eigentuemerId)
            || !Arten[eigentuemer[0]].Contains(art))
        {
            return Fehler("eingabe_ungueltig", 400);
        }
        if (datei.Length is 0 or > MaxBytes)
        {
            return Fehler("datei_zu_gross", 413);
        }

        if (schutz.Finden("dokument", ids.BenutzerId, clientId) is long bekannt)
        {
            return Results.Ok(new { dokumentId = bekannt });
        }

        await using var verbindung = await db.OpenConnectionAsync();
        if (!await TourEndpoints.GueltigAsync(verbindung, null, ids))
        {
            return Results.Unauthorized();
        }

        var pruefen = eigentuemer[0] switch
        {
            "nachweisId" => "SELECT 1 FROM public.abliefernachweis WHERE nachweis_id = $1 AND fahrer_id = $2",
            "tankvorgangId" => "SELECT 1 FROM public.tankvorgang WHERE tankvorgang_id = $1 AND fahrer_id = $2",
            _ => "SELECT 1 FROM public.schadensmeldung WHERE schaden_id = $1 AND fahrer_id = $2",
        };
        await using (var befehl = new NpgsqlCommand(pruefen, verbindung))
        {
            befehl.Parameters.Add(new() { Value = eigentuemerId });
            befehl.Parameters.Add(new() { Value = ids.FahrerId });
            if (await befehl.ExecuteScalarAsync() is null)
            {
                return Fehler("eigentuemer_nicht_gefunden", 404);
            }
        }

        // Nach einem Neustart der API: die clientId steckt im Dateinamen bei Cloudinary
        await using (var befehl = new NpgsqlCommand("""
            SELECT dokument_id FROM public.dokument WHERE benutzer_id = $1 AND dateireferenz LIKE $2
            ORDER BY dokument_id LIMIT 1
            """, verbindung))
        {
            befehl.Parameters.Add(new() { Value = ids.BenutzerId });
            befehl.Parameters.Add(new() { Value = $"%/{clientId}.%" });
            if (await befehl.ExecuteScalarAsync() is long vorhanden)
            {
                schutz.Merken("dokument", ids.BenutzerId, clientId, vorhanden);
                return Results.Ok(new { dokumentId = vorhanden });
            }
        }

        if (!speicher.Verfuegbar)
        {
            // Die App laesst das Dokument in der Warteschlange und versucht es spaeter erneut
            return Fehler("speicher_nicht_eingerichtet", 503);
        }

        byte[] inhalt;
        await using (var strom = datei.OpenReadStream())
        using (var puffer = new MemoryStream())
        {
            await strom.CopyToAsync(puffer);
            inhalt = puffer.ToArray();
        }
        // Nur echte JPEG- oder PNG-Bilder, egal was der Client als Typ angibt
        var mime = inhalt switch
        {
            [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
            [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
            _ => null,
        };
        if (mime is null)
        {
            return Fehler("dateityp_ungueltig", 415);
        }

        string url;
        try
        {
            var ordner = $"disposio/{ids.UnternehmenId}/{art.ToLowerInvariant()}";
            url = await speicher.HochladenAsync(inhalt, mime, ordner, clientId);
        }
        catch (Exception)
        {
            return Fehler("hochladen_fehlgeschlagen", 502);
        }

        var dateiname = DateinameErlaubt().Replace(Path.GetFileName(datei.FileName ?? ""), "_");
        if (dateiname.Length is 0 or > 200)
        {
            dateiname = $"{art.ToLowerInvariant()}_{clientId}{(mime == "image/png" ? ".png" : ".jpg")}";
        }

        long dokumentId;
        await using (var befehl = new NpgsqlCommand($"""
            INSERT INTO public.dokument (dokumentart, dateiname, dateireferenz, mime_typ, dateigroesse_bytes, benutzer_id,
                                         {Spalte(eigentuemer[0])})
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            RETURNING dokument_id
            """, verbindung))
        {
            befehl.Parameters.Add(new() { Value = art });
            befehl.Parameters.Add(new() { Value = dateiname });
            befehl.Parameters.Add(new() { Value = url });
            befehl.Parameters.Add(new() { Value = mime });
            befehl.Parameters.Add(new() { Value = (long)inhalt.Length });
            befehl.Parameters.Add(new() { Value = ids.BenutzerId });
            befehl.Parameters.Add(new() { Value = eigentuemerId });
            dokumentId = (long)(await befehl.ExecuteScalarAsync())!;
        }
        schutz.Merken("dokument", ids.BenutzerId, clientId, dokumentId);

        return Results.Created("/dokumente", new { dokumentId });
    }

    private static string Spalte(string eigentuemer) => eigentuemer switch
    {
        "nachweisId" => "nachweis_id",
        "tankvorgangId" => "tankvorgang_id",
        _ => "schaden_id",
    };

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex DateinameErlaubt();
}
