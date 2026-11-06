using System.Globalization;
using System.Security.Claims;
using Disposio.Api.Services;
using Npgsql;

namespace Disposio.Api.Endpoints;

public sealed record TankAnfrage(decimal? Liter, decimal? Kosten, int? Kilometerstand, string? Tankstelle,
    string? Kraftstoff, DateOnly? Datum, string? GrundOhneBeleg, string? ClientId);

public sealed record SchadenAnfrage(string? Schadensart, string? Beschreibung, string? Bauteil, bool? Fahrbereit,
    string? Ort, double? Lat, double? Lon, DateOnly? Datum, string? ClientId);

/// <summary>
/// Phase 5: Tankvorgang (M14) und Schadensmeldung (M15) des Fahrers.
/// Fahrzeug ist immer das der aktiven Tour (Unterwegs/Pausiert), sonst das der letzten begonnenen Tour.
/// Felder ohne Spalte im ERM: Grund ohne Beleg steht in tankvorgang.bearbeitungsnotiz („Kein Beleg: …“),
/// Bauteil, fahrbereit und Ort stehen vorne in schadensmeldung.beschreibung („[Bauteil: …] [Fahrbereit: ja] [Ort: …] “).
/// </summary>
public static class MeldungEndpoints
{
    public const string TankOffen = "Offen";
    public const string SchadenGemeldet = "Gemeldet";

    public static readonly string[] Kraftstoffe = { "Diesel", "AdBlue", "Benzin", "Strom" };
    public static readonly string[] Schadensarten =
        { "Unfall", "Steinschlag", "Reifenschaden", "Technischer Defekt", "Vandalismus", "Sonstiges" };
    public static readonly string[] Bauteile =
        { "Karosserie", "Scheibe", "Reifen/Felge", "Beleuchtung", "Spiegel", "Motor/Antrieb", "Bremsen", "Aufbau/Ladefläche", "Sonstiges" };

    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static IResult Fehler(string fehler, int status) => Results.Json(new { fehler }, statusCode: status);

    public static void MapMeldungEndpoints(this WebApplication app)
    {
        app.MapGet("/fahrzeug", AktuellesFahrzeugAsync).RequireAuthorization();
        app.MapPost("/tankvorgaenge", TankenAsync).RequireAuthorization();
        app.MapPost("/schaeden", SchadenAsync).RequireAuthorization();
    }

    /// <summary>M14/M15: aktuelles Fahrzeug mit letztem Kilometerstand (Vorschlag beim Tanken).</summary>
    private static async Task<IResult> AktuellesFahrzeugAsync(ClaimsPrincipal nutzer, NpgsqlDataSource db)
    {
        var ids = TourEndpoints.Ids.Aus(nutzer);
        await using var verbindung = await db.OpenConnectionAsync();
        if (!await TourEndpoints.GueltigAsync(verbindung, null, ids))
        {
            return Results.Unauthorized();
        }
        if (await FahrzeugAsync(verbindung, null, ids) is not { } fz)
        {
            return Fehler("kein_fahrzeug", 404);
        }
        return Results.Ok(new
        {
            fahrzeugId = fz.FahrzeugId,
            kennzeichen = fz.Kennzeichen,
            typ = fz.Typ,
            kilometerstand = fz.Kilometerstand,
            aktiveTourId = fz.AktiveTourId,
        });
    }

    /// <summary>M14: Tankvorgang speichern. Hoeherer Kilometerstand wird auch beim Fahrzeug eingetragen.</summary>
    private static async Task<IResult> TankenAsync(TankAnfrage anfrage, ClaimsPrincipal nutzer, NpgsqlDataSource db,
        Wiederholschutz schutz)
    {
        var ids = TourEndpoints.Ids.Aus(nutzer);
        var clientId = Wiederholschutz.Pruefen(anfrage.ClientId);
        var tankstelle = (anfrage.Tankstelle ?? "").Trim();
        var kraftstoff = (anfrage.Kraftstoff ?? "").Trim();
        var grund = (anfrage.GrundOhneBeleg ?? "").Trim();
        if (clientId is null
            || anfrage.Liter is not (> 0 and <= 5000) || anfrage.Kosten is not (>= 0 and <= 100000)
            || anfrage.Kilometerstand is not (>= 0 and <= 5_000_000)
            || tankstelle.Length is 0 or > 200 || !Kraftstoffe.Contains(kraftstoff) || grund.Length > 500
            || anfrage.Datum is not { } datum || !DatumOk(datum, 30))
        {
            return Fehler("eingabe_ungueltig", 400);
        }
        var liter = Math.Round(anfrage.Liter.Value, 2);
        var kosten = Math.Round(anfrage.Kosten.Value, 2);
        var km = anfrage.Kilometerstand.Value;

        if (schutz.Finden("tank", ids.BenutzerId, clientId) is long bekannt)
        {
            return Results.Ok(new { tankvorgangId = bekannt });
        }

        await using var verbindung = await db.OpenConnectionAsync();
        await using var tx = await verbindung.BeginTransactionAsync();
        if (!await TourEndpoints.GueltigAsync(verbindung, tx, ids))
        {
            return Results.Unauthorized();
        }
        if (await FahrzeugAsync(verbindung, tx, ids) is not { } fz)
        {
            return Fehler("kein_fahrzeug", 409);
        }

        // Fahrzeug sperren: Kilometerstand und doppelte Meldungen sauber pruefen
        await using (var befehl = new NpgsqlCommand(
            "SELECT 1 FROM public.fahrzeug WHERE fahrzeug_id = $1 FOR UPDATE", verbindung, tx))
        {
            befehl.Parameters.Add(new() { Value = fz.FahrzeugId });
            await befehl.ExecuteNonQueryAsync();
        }

        // Nach einem Neustart der API: gleicher Fahrer, gleiches Fahrzeug und gleiche Werte = derselbe Tankvorgang
        await using (var befehl = new NpgsqlCommand("""
            SELECT tankvorgang_id FROM public.tankvorgang
            WHERE fahrer_id = $1 AND fahrzeug_id = $2 AND datum = $3 AND liter = $4 AND kosten = $5 AND kilometerstand = $6
            ORDER BY tankvorgang_id LIMIT 1
            """, verbindung, tx))
        {
            befehl.Parameters.Add(new() { Value = ids.FahrerId });
            befehl.Parameters.Add(new() { Value = fz.FahrzeugId });
            befehl.Parameters.Add(new() { Value = datum });
            befehl.Parameters.Add(new() { Value = liter });
            befehl.Parameters.Add(new() { Value = kosten });
            befehl.Parameters.Add(new() { Value = km });
            if (await befehl.ExecuteScalarAsync() is long vorhanden)
            {
                schutz.Merken("tank", ids.BenutzerId, clientId, vorhanden);
                return Results.Ok(new { tankvorgangId = vorhanden });
            }
        }

        long tankvorgangId;
        await using (var befehl = new NpgsqlCommand("""
            INSERT INTO public.tankvorgang (datum, liter, kosten, kilometerstand, tankstelle, kraftstoff,
                                            bearbeitungsstatus, bearbeitungsnotiz, fahrzeug_id, fahrer_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            RETURNING tankvorgang_id
            """, verbindung, tx))
        {
            befehl.Parameters.Add(new() { Value = datum });
            befehl.Parameters.Add(new() { Value = liter });
            befehl.Parameters.Add(new() { Value = kosten });
            befehl.Parameters.Add(new() { Value = km });
            befehl.Parameters.Add(new() { Value = tankstelle });
            befehl.Parameters.Add(new() { Value = kraftstoff });
            befehl.Parameters.Add(new() { Value = TankOffen });
            // M14 ohne Belegfoto: Grund in der Bearbeitungsnotiz (keine eigene Spalte im ERM)
            befehl.Parameters.Add(AblieferungEndpoints.Text(grund.Length == 0 ? "" : $"Kein Beleg: {grund}"));
            befehl.Parameters.Add(new() { Value = fz.FahrzeugId });
            befehl.Parameters.Add(new() { Value = ids.FahrerId });
            tankvorgangId = (long)(await befehl.ExecuteScalarAsync())!;
        }

        // Nur ein hoeherer Stand als der letzte bekannte wird uebernommen (Tippfehler nach unten aendern nichts)
        if (km > fz.Kilometerstand)
        {
            await using var batch = new NpgsqlBatch(verbindung, tx);
            var neu = new NpgsqlBatchCommand(
                "INSERT INTO public.kilometerstand (datum, kilometerstand, fahrzeug_id) VALUES ($1, $2, $3)");
            neu.Parameters.Add(new() { Value = datum });
            neu.Parameters.Add(new() { Value = km });
            neu.Parameters.Add(new() { Value = fz.FahrzeugId });
            batch.BatchCommands.Add(neu);
            var fahrzeug = new NpgsqlBatchCommand(
                "UPDATE public.fahrzeug SET kilometerstand = $1 WHERE fahrzeug_id = $2 AND kilometerstand < $1");
            fahrzeug.Parameters.Add(new() { Value = km });
            fahrzeug.Parameters.Add(new() { Value = fz.FahrzeugId });
            batch.BatchCommands.Add(fahrzeug);
            await batch.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        schutz.Merken("tank", ids.BenutzerId, clientId, tankvorgangId);
        return Results.Created("/tankvorgaenge", new { tankvorgangId });
    }

    /// <summary>
    /// M15: Schaden melden. Nicht fahrbereit: automatische Nachricht an den Disponenten der aktiven Tour.
    /// </summary>
    private static async Task<IResult> SchadenAsync(SchadenAnfrage anfrage, ClaimsPrincipal nutzer, NpgsqlDataSource db,
        Wiederholschutz schutz, Uebersetzer uebersetzer, ILoggerFactory logFabrik)
    {
        var ids = TourEndpoints.Ids.Aus(nutzer);
        var clientId = Wiederholschutz.Pruefen(anfrage.ClientId);
        var art = (anfrage.Schadensart ?? "").Trim();
        var bauteil = (anfrage.Bauteil ?? "").Trim();
        var text = (anfrage.Beschreibung ?? "").Trim();
        var ort = (anfrage.Ort ?? "").Trim();
        if (clientId is null || !Schadensarten.Contains(art) || !Bauteile.Contains(bauteil)
            || text.Length is 0 or > 1500 || ort.Length > 200 || anfrage.Fahrbereit is not bool fahrbereit
            || anfrage.Lat is < -90 or > 90 || anfrage.Lon is < -180 or > 180 || (anfrage.Lat is null) != (anfrage.Lon is null)
            || anfrage.Datum is not { } datum || !DatumOk(datum, 30))
        {
            return Fehler("eingabe_ungueltig", 400);
        }

        // Ort: Text des Fahrers, sonst die GPS-Koordinaten
        if (ort.Length == 0 && anfrage.Lat is double lat && anfrage.Lon is double lon)
        {
            ort = string.Create(CultureInfo.InvariantCulture, $"{lat:F5}, {lon:F5}");
        }
        var beschreibung = $"[Bauteil: {bauteil}] [Fahrbereit: {(fahrbereit ? "ja" : "nein")}] "
                           + (ort.Length > 0 ? $"[Ort: {ort}] " : "") + text;

        if (schutz.Finden("schaden", ids.BenutzerId, clientId) is long bekannt)
        {
            return Results.Ok(new { schadenId = bekannt, dispositionInformiert = false });
        }

        FahrzeugDaten fz;
        long schadenId;
        await using (var verbindung = await db.OpenConnectionAsync())
        await using (var tx = await verbindung.BeginTransactionAsync())
        {
            if (!await TourEndpoints.GueltigAsync(verbindung, tx, ids))
            {
                return Results.Unauthorized();
            }
            if (await FahrzeugAsync(verbindung, tx, ids) is not { } gefunden)
            {
                return Fehler("kein_fahrzeug", 409);
            }
            fz = gefunden;

            await using (var befehl = new NpgsqlCommand(
                "SELECT 1 FROM public.fahrzeug WHERE fahrzeug_id = $1 FOR UPDATE", verbindung, tx))
            {
                befehl.Parameters.Add(new() { Value = fz.FahrzeugId });
                await befehl.ExecuteNonQueryAsync();
            }

            // Nach einem Neustart der API: gleicher Fahrer, gleiches Fahrzeug, gleicher Tag und Text = dieselbe Meldung
            await using (var befehl = new NpgsqlCommand("""
                SELECT schaden_id FROM public.schadensmeldung
                WHERE fahrer_id = $1 AND fahrzeug_id = $2 AND datum = $3 AND beschreibung = $4
                ORDER BY schaden_id LIMIT 1
                """, verbindung, tx))
            {
                befehl.Parameters.Add(new() { Value = ids.FahrerId });
                befehl.Parameters.Add(new() { Value = fz.FahrzeugId });
                befehl.Parameters.Add(new() { Value = datum });
                befehl.Parameters.Add(new() { Value = beschreibung });
                if (await befehl.ExecuteScalarAsync() is long vorhanden)
                {
                    schutz.Merken("schaden", ids.BenutzerId, clientId, vorhanden);
                    return Results.Ok(new { schadenId = vorhanden, dispositionInformiert = false });
                }
            }

            await using (var befehl = new NpgsqlCommand("""
                INSERT INTO public.schadensmeldung (datum, beschreibung, schadensart, status, kosten, fahrzeug_id, fahrer_id)
                VALUES ($1, $2, $3, $4, 0, $5, $6)
                RETURNING schaden_id
                """, verbindung, tx))
            {
                befehl.Parameters.Add(new() { Value = datum });
                befehl.Parameters.Add(new() { Value = beschreibung });
                befehl.Parameters.Add(new() { Value = art });
                befehl.Parameters.Add(new() { Value = SchadenGemeldet });
                befehl.Parameters.Add(new() { Value = fz.FahrzeugId });
                befehl.Parameters.Add(new() { Value = ids.FahrerId });
                schadenId = (long)(await befehl.ExecuteScalarAsync())!;
            }
            await tx.CommitAsync();
        }
        schutz.Merken("schaden", ids.BenutzerId, clientId, schadenId);

        // Nicht fahrbereit: Disposition der aktiven Tour sofort per Chat informieren. Fehler aendern nichts an der Meldung.
        var informiert = false;
        if (!fahrbereit && fz.AktiveTourId is long tourId)
        {
            try
            {
                informiert = await ChatEndpoints.AutomatischSendenAsync(db, uebersetzer, ids, tourId,
                    sprache => NichtFahrbereitText(sprache, fz.Kennzeichen, art, bauteil, ort, text));
            }
            catch (Exception ex)
            {
                logFabrik.CreateLogger(nameof(MeldungEndpoints))
                    .LogWarning(ex, "Chatnachricht zur Schadensmeldung {Id} fehlgeschlagen", schadenId);
            }
        }

        return Results.Created("/schaeden", new { schadenId, dispositionInformiert = informiert });
    }

    /// <summary>Text der automatischen Chatnachricht in der Sprache des Fahrers (wird fuer die Disposition uebersetzt).</summary>
    private static string NichtFahrbereitText(string sprache, string kennzeichen, string art, string bauteil, string ort, string text)
    {
        var ortTeil = ort.Length == 0 ? "" : sprache switch
        {
            "en" => $" Location: {ort}.",
            "pl" => $" Miejsce: {ort}.",
            _ => $" Ort: {ort}.",
        };
        return sprache switch
        {
            "en" => $"Automatic message: vehicle {kennzeichen} is NOT roadworthy. Damage: {art}, part: {bauteil}.{ortTeil} {text}",
            "pl" => $"Wiadomość automatyczna: pojazd {kennzeichen} NIE nadaje się do jazdy. Szkoda: {art}, część: {bauteil}.{ortTeil} {text}",
            _ => $"Automatische Meldung: Fahrzeug {kennzeichen} ist NICHT fahrbereit. Schaden: {art}, Bauteil: {bauteil}.{ortTeil} {text}",
        };
    }

    /// <summary>Datum nicht in der Zukunft und hoechstens [tage] Tage alt (Kalender Berlin).</summary>
    private static bool DatumOk(DateOnly datum, int tage)
    {
        var heute = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Berlin));
        return datum <= heute && datum >= heute.AddDays(-tage);
    }

    private sealed record FahrzeugDaten(long FahrzeugId, string Kennzeichen, string Typ, int Kilometerstand, long? AktiveTourId);

    /// <summary>
    /// Fahrzeug der aktiven Tour, sonst der zuletzt begonnenen Tour des Fahrers. null = keine Tour mit Fahrzeug.
    /// Kilometerstand = hoechster Wert aus fahrzeug und kilometerstand.
    /// </summary>
    private static async Task<FahrzeugDaten?> FahrzeugAsync(NpgsqlConnection verbindung, NpgsqlTransaction? tx, TourEndpoints.Ids ids)
    {
        await using var befehl = new NpgsqlCommand("""
            SELECT fz.fahrzeug_id, fz.kennzeichen, fz.fahrzeugtyp,
                   GREATEST(fz.kilometerstand,
                            COALESCE((SELECT max(k.kilometerstand) FROM public.kilometerstand k WHERE k.fahrzeug_id = fz.fahrzeug_id), 0)),
                   CASE WHEN t.status IN ($3, $4) THEN t.tour_id END
            FROM public.tour t
            JOIN public.fahrzeug fz ON fz.fahrzeug_id = t.fahrzeug_id AND fz.unternehmen_id = t.unternehmen_id
            WHERE t.fahrer_id = $1 AND t.unternehmen_id = $2 AND (t.status IN ($3, $4) OR t.startzeit <= now())
            ORDER BY (t.status IN ($3, $4)) DESC, t.startzeit DESC
            LIMIT 1
            """, verbindung, tx);
        befehl.Parameters.Add(new() { Value = ids.FahrerId });
        befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
        befehl.Parameters.Add(new() { Value = TourEndpoints.TourUnterwegs });
        befehl.Parameters.Add(new() { Value = TourEndpoints.TourPausiert });
        await using var leser = await befehl.ExecuteReaderAsync();
        if (!await leser.ReadAsync())
        {
            return null;
        }
        return new FahrzeugDaten(leser.GetInt64(0), leser.GetString(1), leser.GetString(2), leser.GetInt32(3),
            leser.IsDBNull(4) ? null : leser.GetInt64(4));
    }
}
