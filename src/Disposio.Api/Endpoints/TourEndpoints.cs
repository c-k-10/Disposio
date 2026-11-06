using System.Security.Claims;
using Disposio.Api.Services;
using Npgsql;

namespace Disposio.Api.Endpoints;

public sealed record TourStatusAnfrage(string? Aktion, string? Bemerkung, bool? Erzwingen);
public sealed record StoppStatusAnfrage(string? Aktion);
public sealed record ProblemAnfrage(long? StoppId, string? Art, string? Beschreibung, int? DauerMinuten);

/// <summary>
/// Phase 2: Touren des Fahrers (Wireframes M02 bis M05). Phase 3: ETA je Stopp, Pause seit (M10), Abschluss (M12).
/// Eine Tour gehoert dem Fahrer, wenn tour.fahrer_id und tour.unternehmen_id zum Token passen.
/// Jede Aktion schreibt eine STATUSMELDUNG und setzt tour.status bzw. tourstopp.status.
/// </summary>
public static class TourEndpoints
{
    // Statuswerte wie in der Datenbank der Software. Pausiert, Abgeschlossen, Angekommen und Entladen sind neu (Phase 2).
    public const string TourGeplant = "Geplant";
    public const string TourZugewiesen = "Zugewiesen";
    public const string TourUnterwegs = "Unterwegs";
    public const string TourPausiert = "Pausiert";
    public const string TourAbgeschlossen = "Abgeschlossen";

    public const string StoppOffen = "Offen";
    public const string StoppAngekommen = "Angekommen";
    public const string StoppEntladen = "Entladen";
    public const string StoppErledigt = "Erledigt";

    // Arten fuer M05. Werden so in statusmeldung.status gespeichert.
    public static readonly string[] ProblemArten = { "Stau", "Panne", "Kunde nicht da", "Adresse falsch", "Ware beschädigt", "Sonstiges" };

    private static IResult Fehler(string fehler, int status) => Results.Json(new { fehler }, statusCode: status);
    private static IResult TourFehlt() => Fehler("tour_nicht_gefunden", 404);
    private static IResult StatusUngueltig() => Fehler("status_ungueltig", 409);

    public static void MapTourEndpoints(this WebApplication app)
    {
        var touren = app.MapGroup("/touren").RequireAuthorization();
        touren.MapGet("", ListeAsync);
        touren.MapGet("/{id:long}", DetailsAsync);
        touren.MapPost("/{id:long}/status", TourStatusAsync);
        touren.MapPost("/{id:long}/problem", ProblemAsync);
        touren.MapGet("/{id:long}/abschluss", AbschlussAsync);

        app.MapPost("/stopps/{id:long}/status", StoppStatusAsync).RequireAuthorization();
    }

    /// <summary>M02: aktive Touren zuerst, dann Touren von heute bis in 7 Tagen (Zeitzone Berlin).</summary>
    private static async Task<IResult> ListeAsync(ClaimsPrincipal nutzer, NpgsqlDataSource db)
    {
        var ids = Ids.Aus(nutzer);
        await using var verbindung = await db.OpenConnectionAsync();
        if (!await GueltigAsync(verbindung, null, ids))
        {
            return Results.Unauthorized();
        }

        await using var befehl = new NpgsqlCommand("""
            WITH heute AS (SELECT date_trunc('day', now() AT TIME ZONE 'Europe/Berlin') AS tag)
            SELECT t.tour_id, t.tournummer, t.startzeit, t.endzeit, t.startort, t.zielort, t.status,
                   (SELECT count(*) FROM public.tourstopp s WHERE s.tour_id = t.tour_id),
                   (SELECT s.bezeichnung FROM public.tourstopp s
                    WHERE s.tour_id = t.tour_id AND s.status <> $3 ORDER BY s.reihenfolge LIMIT 1)
            FROM public.tour t, heute
            WHERE t.fahrer_id = $1 AND t.unternehmen_id = $2
              AND (t.status IN ($4, $5)
                   OR (t.endzeit >= heute.tag AT TIME ZONE 'Europe/Berlin'
                       AND t.startzeit < (heute.tag + interval '8 days') AT TIME ZONE 'Europe/Berlin'))
            ORDER BY (t.status IN ($4, $5)) DESC, t.startzeit
            """, verbindung);
        befehl.Parameters.Add(new() { Value = ids.FahrerId });
        befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
        befehl.Parameters.Add(new() { Value = StoppErledigt });
        befehl.Parameters.Add(new() { Value = TourUnterwegs });
        befehl.Parameters.Add(new() { Value = TourPausiert });

        var liste = new List<object>();
        await using var leser = await befehl.ExecuteReaderAsync();
        while (await leser.ReadAsync())
        {
            liste.Add(new
            {
                tourId = leser.GetInt64(0),
                tournummer = leser.GetString(1),
                startzeit = leser.GetDateTime(2),
                endzeit = leser.GetDateTime(3),
                startort = leser.GetString(4),
                zielort = leser.GetString(5),
                status = leser.GetString(6),
                anzahlStopps = leser.GetInt64(7),
                naechsterStopp = leser.IsDBNull(8) ? null : leser.GetString(8),
            });
        }
        return Results.Ok(liste);
    }

    /// <summary>M03/M04: Tour mit Fahrzeug, Stopps in Reihenfolge und den Auftraegen je Stopp.</summary>
    private static async Task<IResult> DetailsAsync(long id, ClaimsPrincipal nutzer, NpgsqlDataSource db)
    {
        var ids = Ids.Aus(nutzer);
        await using var verbindung = await db.OpenConnectionAsync();
        if (!await GueltigAsync(verbindung, null, ids))
        {
            return Results.Unauthorized();
        }

        object tour;
        await using (var befehl = new NpgsqlCommand("""
            SELECT t.tournummer, t.startzeit, t.endzeit, t.startort, t.zielort, t.status, fz.kennzeichen, fz.fahrzeugtyp,
                   (SELECT max(m.zeitstempel) FROM public.statusmeldung m
                    WHERE m.tour_id = t.tour_id AND m.stopp_id IS NULL AND m.status = 'Pause')
            FROM public.tour t
            LEFT JOIN public.fahrzeug fz ON fz.fahrzeug_id = t.fahrzeug_id
            WHERE t.tour_id = $1 AND t.fahrer_id = $2 AND t.unternehmen_id = $3
            """, verbindung))
        {
            befehl.Parameters.Add(new() { Value = id });
            befehl.Parameters.Add(new() { Value = ids.FahrerId });
            befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
            await using var leser = await befehl.ExecuteReaderAsync();
            if (!await leser.ReadAsync())
            {
                return TourFehlt();
            }
            tour = new
            {
                tournummer = leser.GetString(0),
                startzeit = leser.GetDateTime(1),
                endzeit = leser.GetDateTime(2),
                startort = leser.GetString(3),
                zielort = leser.GetString(4),
                status = leser.GetString(5),
                fahrzeug = leser.IsDBNull(6) ? null : new { kennzeichen = leser.GetString(6), typ = leser.GetString(7) },
                // M10: Beginn der aktuellen Pause (letzte Meldung "Pause")
                pauseSeit = leser.GetString(5) == TourPausiert && !leser.IsDBNull(8) ? leser.GetDateTime(8) : (DateTime?)null,
            };
        }

        // Auftraege aller Stopps der Tour, nach Stopp gruppiert
        var auftraege = new Dictionary<long, List<object>>();
        await using (var befehl = new NpgsqlCommand("""
            SELECT sa.stopp_id, sa.vorgang_art, a.auftragsnummer, k.firmenname, a.anzahl, a.verpackungstyp,
                   a.gewicht_kg, a.gutart, a.gefahrgut, a.temperatur_min_c, a.temperatur_max_c,
                   a.ansprechpartner, a.lade_entladeequipment, a.avisierung_erforderlich, a.kundenreferenz, a.auftrag_id
            FROM public.stopp_auftrag sa
            JOIN public.tourstopp s ON s.stopp_id = sa.stopp_id
            JOIN public.auftrag a ON a.auftrag_id = sa.auftrag_id
            JOIN public.kunde k ON k.kunde_id = a.kunde_id
            WHERE s.tour_id = $1 AND a.unternehmen_id = $2
            ORDER BY a.auftragsnummer
            """, verbindung))
        {
            befehl.Parameters.Add(new() { Value = id });
            befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
            await using var leser = await befehl.ExecuteReaderAsync();
            while (await leser.ReadAsync())
            {
                var stoppId = leser.GetInt64(0);
                if (!auftraege.TryGetValue(stoppId, out var liste))
                {
                    auftraege[stoppId] = liste = new List<object>();
                }
                liste.Add(new
                {
                    // Phase 5: fuer den Ablieferungsnachweis (M06), auch ohne Netz
                    auftragId = leser.GetInt64(15),
                    vorgangArt = leser.GetString(1),
                    auftragsnummer = leser.GetString(2),
                    kunde = leser.GetString(3),
                    anzahl = leser.GetInt32(4),
                    verpackungstyp = leser.GetString(5),
                    gewichtKg = leser.GetDecimal(6),
                    gutart = leser.GetString(7),
                    gefahrgut = leser.GetBoolean(8),
                    temperaturMin = leser.IsDBNull(9) ? (decimal?)null : leser.GetDecimal(9),
                    temperaturMax = leser.IsDBNull(10) ? (decimal?)null : leser.GetDecimal(10),
                    ansprechpartner = leser.GetString(11),
                    ladeEntladeequipment = leser.GetString(12),
                    avisierungErforderlich = leser.GetBoolean(13),
                    kundenreferenz = leser.IsDBNull(14) ? null : leser.GetString(14),
                });
            }
        }

        var stopps = new List<object>();
        await using (var befehl = new NpgsqlCommand("""
            SELECT stopp_id, reihenfolge, art, bezeichnung, adresse, breitengrad, laengengrad,
                   zeitfenster_von, zeitfenster_bis, geplante_ankunft, ist_ankunft, ist_abfahrt, status,
                   aktuelle_eta, eta_berechnet_am
            FROM public.tourstopp
            WHERE tour_id = $1
            ORDER BY reihenfolge
            """, verbindung))
        {
            befehl.Parameters.Add(new() { Value = id });
            await using var leser = await befehl.ExecuteReaderAsync();
            while (await leser.ReadAsync())
            {
                var stoppId = leser.GetInt64(0);
                var status = leser.GetString(12);
                DateTime? eta = leser.IsDBNull(13) ? null : leser.GetDateTime(13);
                stopps.Add(new
                {
                    stoppId,
                    reihenfolge = leser.GetInt32(1),
                    art = leser.GetString(2),
                    bezeichnung = leser.GetString(3),
                    adresse = leser.GetString(4),
                    breitengrad = leser.IsDBNull(5) ? (decimal?)null : leser.GetDecimal(5),
                    laengengrad = leser.IsDBNull(6) ? (decimal?)null : leser.GetDecimal(6),
                    zeitfensterVon = leser.GetDateTime(7),
                    zeitfensterBis = leser.GetDateTime(8),
                    geplanteAnkunft = leser.IsDBNull(9) ? (DateTime?)null : leser.GetDateTime(9),
                    istAnkunft = leser.IsDBNull(10) ? (DateTime?)null : leser.GetDateTime(10),
                    istAbfahrt = leser.IsDBNull(11) ? (DateTime?)null : leser.GetDateTime(11),
                    status,
                    // Phase 3: ETA aus TomTom (siehe EtaRechner). Verspaetet, wenn nach dem Zeitfenster.
                    aktuelleEta = eta,
                    etaBerechnetAm = leser.IsDBNull(14) ? (DateTime?)null : leser.GetDateTime(14),
                    verspaetet = status != StoppErledigt && eta > leser.GetDateTime(8),
                    auftraege = auftraege.GetValueOrDefault(stoppId) ?? new List<object>(),
                });
            }
        }

        return Results.Ok(new { tourId = id, tour, stopps });
    }

    /// <summary>M03: Fahrt starten, Pause, Fahrt fortsetzen, Tour abschliessen.</summary>
    private static async Task<IResult> TourStatusAsync(long id, TourStatusAnfrage anfrage, ClaimsPrincipal nutzer, NpgsqlDataSource db)
    {
        var ids = Ids.Aus(nutzer);
        var aktion = (anfrage.Aktion ?? "").Trim().ToLowerInvariant();
        var bemerkung = (anfrage.Bemerkung ?? "").Trim();
        if (bemerkung.Length > 1000)
        {
            return Fehler("eingabe_ungueltig", 400);
        }

        await using var verbindung = await db.OpenConnectionAsync();
        await using var tx = await verbindung.BeginTransactionAsync();
        if (!await GueltigAsync(verbindung, tx, ids))
        {
            return Results.Unauthorized();
        }
        // Fahrer sperren, damit zwei gleichzeitige "Fahrt starten" nicht beide durchkommen
        await AusfuehrenAsync(verbindung, tx, "SELECT 1 FROM public.fahrer WHERE fahrer_id = $1 FOR UPDATE", ids.FahrerId);

        string status;
        bool hatFahrzeug;
        await using (var befehl = new NpgsqlCommand("""
            SELECT status, fahrzeug_id IS NOT NULL FROM public.tour
            WHERE tour_id = $1 AND fahrer_id = $2 AND unternehmen_id = $3
            FOR UPDATE
            """, verbindung, tx))
        {
            befehl.Parameters.Add(new() { Value = id });
            befehl.Parameters.Add(new() { Value = ids.FahrerId });
            befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
            await using var leser = await befehl.ExecuteReaderAsync();
            if (!await leser.ReadAsync())
            {
                return TourFehlt();
            }
            status = leser.GetString(0);
            hatFahrzeug = leser.GetBoolean(1);
        }

        string neuerStatus;
        string meldung;
        switch (aktion)
        {
            case "starten":
                if (status is not (TourGeplant or TourZugewiesen))
                {
                    return StatusUngueltig();
                }
                if (!hatFahrzeug)
                {
                    return Fehler("kein_fahrzeug", 409);
                }
                await using (var befehl = new NpgsqlCommand("""
                    SELECT tournummer FROM public.tour
                    WHERE fahrer_id = $1 AND unternehmen_id = $2 AND tour_id <> $3 AND status IN ($4, $5)
                    LIMIT 1
                    """, verbindung, tx))
                {
                    befehl.Parameters.Add(new() { Value = ids.FahrerId });
                    befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
                    befehl.Parameters.Add(new() { Value = id });
                    befehl.Parameters.Add(new() { Value = TourUnterwegs });
                    befehl.Parameters.Add(new() { Value = TourPausiert });
                    if (await befehl.ExecuteScalarAsync() is string andere)
                    {
                        return Results.Json(new { fehler = "andere_tour_aktiv", tournummer = andere }, statusCode: 409);
                    }
                }
                (neuerStatus, meldung) = (TourUnterwegs, "Fahrt gestartet");
                break;

            case "pausieren":
                if (status != TourUnterwegs)
                {
                    return StatusUngueltig();
                }
                (neuerStatus, meldung) = (TourPausiert, "Pause");
                break;

            case "fortsetzen":
                if (status != TourPausiert)
                {
                    return StatusUngueltig();
                }
                (neuerStatus, meldung) = (TourUnterwegs, "Fahrt fortgesetzt");
                break;

            case "beenden":
                if (status is not (TourUnterwegs or TourPausiert))
                {
                    return StatusUngueltig();
                }
                if (anfrage.Erzwingen != true)
                {
                    var offene = await OffeneStoppsAsync(verbindung, tx, id);
                    if (offene.Count > 0)
                    {
                        return Results.Json(new { fehler = "offene_stopps", stopps = offene }, statusCode: 409);
                    }
                }
                (neuerStatus, meldung) = (TourAbgeschlossen, "Tour beendet");
                break;

            default:
                return Fehler("aktion_ungueltig", 400);
        }

        await using (var befehl = new NpgsqlCommand("UPDATE public.tour SET status = $1 WHERE tour_id = $2", verbindung, tx))
        {
            befehl.Parameters.Add(new() { Value = neuerStatus });
            befehl.Parameters.Add(new() { Value = id });
            await befehl.ExecuteNonQueryAsync();
        }
        await MeldungAsync(verbindung, tx, ids.FahrerId, id, null, meldung, bemerkung);
        await tx.CommitAsync();

        return Results.Ok(new { status = neuerStatus });
    }

    /// <summary>M12: offene Stopps und ob die Tour beendet werden darf.</summary>
    private static async Task<IResult> AbschlussAsync(long id, ClaimsPrincipal nutzer, NpgsqlDataSource db)
    {
        var ids = Ids.Aus(nutzer);
        await using var verbindung = await db.OpenConnectionAsync();
        await using var tx = await verbindung.BeginTransactionAsync();
        if (!await GueltigAsync(verbindung, tx, ids))
        {
            return Results.Unauthorized();
        }

        string status;
        await using (var befehl = new NpgsqlCommand("""
            SELECT status FROM public.tour WHERE tour_id = $1 AND fahrer_id = $2 AND unternehmen_id = $3
            """, verbindung, tx))
        {
            befehl.Parameters.Add(new() { Value = id });
            befehl.Parameters.Add(new() { Value = ids.FahrerId });
            befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
            if (await befehl.ExecuteScalarAsync() is not string gelesen)
            {
                return TourFehlt();
            }
            status = gelesen;
        }

        var offene = await OffeneStoppsAsync(verbindung, tx, id);
        await tx.CommitAsync();
        return Results.Ok(new
        {
            status,
            darfBeenden = status is TourUnterwegs or TourPausiert,
            // Bei offenen Stopps beendet die App mit erzwingen: true
            offeneStopps = offene,
        });
    }

    /// <summary>M03/M04: Angekommen, Entladen, Stopp abschliessen. Nur bei laufender Fahrt.</summary>
    private static async Task<IResult> StoppStatusAsync(long id, StoppStatusAnfrage anfrage, ClaimsPrincipal nutzer, NpgsqlDataSource db)
    {
        var ids = Ids.Aus(nutzer);
        var aktion = (anfrage.Aktion ?? "").Trim().ToLowerInvariant();

        await using var verbindung = await db.OpenConnectionAsync();
        await using var tx = await verbindung.BeginTransactionAsync();
        if (!await GueltigAsync(verbindung, tx, ids))
        {
            return Results.Unauthorized();
        }

        long tourId;
        string stoppStatus;
        string tourStatus;
        await using (var befehl = new NpgsqlCommand("""
            SELECT t.tour_id, s.status, t.status
            FROM public.tourstopp s
            JOIN public.tour t ON t.tour_id = s.tour_id
            WHERE s.stopp_id = $1 AND t.fahrer_id = $2 AND t.unternehmen_id = $3
            FOR UPDATE OF s, t
            """, verbindung, tx))
        {
            befehl.Parameters.Add(new() { Value = id });
            befehl.Parameters.Add(new() { Value = ids.FahrerId });
            befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
            await using var leser = await befehl.ExecuteReaderAsync();
            if (!await leser.ReadAsync())
            {
                return Fehler("stopp_nicht_gefunden", 404);
            }
            tourId = leser.GetInt64(0);
            stoppStatus = leser.GetString(1);
            tourStatus = leser.GetString(2);
        }

        if (tourStatus != TourUnterwegs)
        {
            return Fehler("tour_nicht_unterwegs", 409);
        }

        // ist_ankunft und ist_abfahrt werden nur beim ersten Mal gesetzt
        (string neuerStatus, string meldung, string sql) = (aktion, stoppStatus) switch
        {
            ("angekommen", StoppOffen) => (StoppAngekommen, "Angekommen",
                "UPDATE public.tourstopp SET status = $1, ist_ankunft = COALESCE(ist_ankunft, now()) WHERE stopp_id = $2"),
            ("entladen", StoppAngekommen) => (StoppEntladen, "Entladen",
                "UPDATE public.tourstopp SET status = $1 WHERE stopp_id = $2"),
            ("abgeschlossen", StoppAngekommen or StoppEntladen) => (StoppErledigt, "Erledigt",
                "UPDATE public.tourstopp SET status = $1, ist_abfahrt = COALESCE(ist_abfahrt, now()) WHERE stopp_id = $2"),
            ("angekommen" or "entladen" or "abgeschlossen", _) => ("", "", ""),
            _ => (null!, null!, null!),
        };
        if (neuerStatus is null)
        {
            return Fehler("aktion_ungueltig", 400);
        }
        if (neuerStatus.Length == 0)
        {
            return StatusUngueltig();
        }

        await using (var befehl = new NpgsqlCommand(sql, verbindung, tx))
        {
            befehl.Parameters.Add(new() { Value = neuerStatus });
            befehl.Parameters.Add(new() { Value = id });
            await befehl.ExecuteNonQueryAsync();
        }
        await MeldungAsync(verbindung, tx, ids.FahrerId, tourId, id, meldung, "");
        await tx.CommitAsync();

        return Results.Ok(new { status = neuerStatus });
    }

    /// <summary>M05: Problem melden. Vorlaeufig nur als STATUSMELDUNG (Status = Art), ohne Foto.</summary>
    private static async Task<IResult> ProblemAsync(long id, ProblemAnfrage anfrage, ClaimsPrincipal nutzer, NpgsqlDataSource db)
    {
        var ids = Ids.Aus(nutzer);
        var art = (anfrage.Art ?? "").Trim();
        var beschreibung = (anfrage.Beschreibung ?? "").Trim();
        if (!ProblemArten.Contains(art) || beschreibung.Length is 0 or > 1000
            || anfrage.DauerMinuten is < 1 or > 1440)
        {
            return Fehler("eingabe_ungueltig", 400);
        }

        await using var verbindung = await db.OpenConnectionAsync();
        await using var tx = await verbindung.BeginTransactionAsync();
        if (!await GueltigAsync(verbindung, tx, ids))
        {
            return Results.Unauthorized();
        }

        await using (var befehl = new NpgsqlCommand("""
            SELECT status FROM public.tour WHERE tour_id = $1 AND fahrer_id = $2 AND unternehmen_id = $3
            """, verbindung, tx))
        {
            befehl.Parameters.Add(new() { Value = id });
            befehl.Parameters.Add(new() { Value = ids.FahrerId });
            befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
            var status = await befehl.ExecuteScalarAsync() as string;
            if (status is null)
            {
                return TourFehlt();
            }
            if (status == TourAbgeschlossen)
            {
                return StatusUngueltig();
            }
        }

        if (anfrage.StoppId is long stoppId)
        {
            await using var befehl = new NpgsqlCommand(
                "SELECT 1 FROM public.tourstopp WHERE stopp_id = $1 AND tour_id = $2", verbindung, tx);
            befehl.Parameters.Add(new() { Value = stoppId });
            befehl.Parameters.Add(new() { Value = id });
            if (await befehl.ExecuteScalarAsync() is null)
            {
                return Fehler("stopp_nicht_gefunden", 404);
            }
        }

        var bemerkung = anfrage.DauerMinuten is int dauer ? $"{beschreibung} · Dauer ca. {dauer} Min." : beschreibung;
        await MeldungAsync(verbindung, tx, ids.FahrerId, id, anfrage.StoppId, art, bemerkung);
        await tx.CommitAsync();

        return Results.Created($"/touren/{id}", new { gemeldet = true });
    }

    private static async Task<List<object>> OffeneStoppsAsync(NpgsqlConnection verbindung, NpgsqlTransaction tx, long tourId)
    {
        await using var befehl = new NpgsqlCommand("""
            SELECT stopp_id, reihenfolge, bezeichnung, status FROM public.tourstopp
            WHERE tour_id = $1 AND status <> $2
            ORDER BY reihenfolge
            """, verbindung, tx);
        befehl.Parameters.Add(new() { Value = tourId });
        befehl.Parameters.Add(new() { Value = StoppErledigt });

        var offene = new List<object>();
        await using var leser = await befehl.ExecuteReaderAsync();
        while (await leser.ReadAsync())
        {
            offene.Add(new
            {
                stoppId = leser.GetInt64(0),
                reihenfolge = leser.GetInt32(1),
                bezeichnung = leser.GetString(2),
                status = leser.GetString(3),
            });
        }
        return offene;
    }

    private static async Task MeldungAsync(NpgsqlConnection verbindung, NpgsqlTransaction tx,
        long fahrerId, long tourId, long? stoppId, string status, string bemerkung)
    {
        await using var befehl = new NpgsqlCommand("""
            INSERT INTO public.statusmeldung (status, zeitstempel, bemerkung, meldungsart, fahrer_id, tour_id, stopp_id)
            VALUES ($1, now(), $2, $3, $4, $5, $6)
            """, verbindung, tx);
        befehl.Parameters.Add(new() { Value = status });
        befehl.Parameters.Add(new() { Value = bemerkung });
        befehl.Parameters.Add(new() { Value = stoppId is null ? "Tour" : "Stopp" });
        befehl.Parameters.Add(new() { Value = fahrerId });
        befehl.Parameters.Add(new() { Value = tourId });
        befehl.Parameters.Add(new() { Value = (object?)stoppId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        await befehl.ExecuteNonQueryAsync();
    }

    /// <summary>Wie im Profil: Konto aktiv, Firma nicht gesperrt, Fahrer-Rolle vorhanden.</summary>
    internal static async Task<bool> GueltigAsync(NpgsqlConnection verbindung, NpgsqlTransaction? tx, Ids ids)
    {
        await using var befehl = new NpgsqlCommand($"""
            SELECT 1 FROM public.benutzer b JOIN public.fahrer f ON f.benutzer_id = b.benutzer_id
            WHERE {ProfilEndpoints.NochGueltig}
            """, verbindung, tx);
        befehl.Parameters.Add(new() { Value = ids.BenutzerId });
        befehl.Parameters.Add(new() { Value = ids.FahrerId });
        befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
        return await befehl.ExecuteScalarAsync() is not null;
    }

    private static async Task AusfuehrenAsync(NpgsqlConnection verbindung, NpgsqlTransaction tx, string sql, long wert)
    {
        await using var befehl = new NpgsqlCommand(sql, verbindung, tx);
        befehl.Parameters.Add(new() { Value = wert });
        await befehl.ExecuteNonQueryAsync();
    }

    internal sealed record Ids(long BenutzerId, long FahrerId, long UnternehmenId)
    {
        public static Ids Aus(ClaimsPrincipal nutzer) => new(
            AnmeldeClaims.Lesen(nutzer, AnmeldeClaims.BenutzerId),
            AnmeldeClaims.Lesen(nutzer, AnmeldeClaims.FahrerId),
            AnmeldeClaims.Lesen(nutzer, AnmeldeClaims.UnternehmenId));
    }
}
