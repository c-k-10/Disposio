using System.Security.Claims;
using Disposio.Api.Services;
using Npgsql;

namespace Disposio.Api.Endpoints;

public sealed record NachrichtAnfrage(string? Text);

/// <summary>
/// Phase 4: Chat zwischen Fahrer und Disposition je Tour (Wireframe M08, Tabellen chat und nachricht).
/// Ein Chat gehoert dem Fahrer, wenn chat.fahrer_benutzer_id und die Firma der Tour zum Token passen, sonst 404.
/// Schreiben nur bis 7 Tage nach Tourende und nicht bei Status geschlossen.
/// Uebersetzung mit DeepL: Fahrer-Nachrichten sofort in die Sprache des Disponenten,
/// Nachrichten aus der Software beim Abruf durch den Fahrer in seine Sprache.
/// </summary>
public static class ChatEndpoints
{
    // Werte wie in den vorhandenen Daten der Software (klein geschrieben)
    public const string ChatOffen = "offen";
    public const string ChatGeschlossen = "geschlossen";

    public const string UeNichtNoetig = "nicht_noetig";
    public const string UeAusstehend = "ausstehend";
    public const string UeUebersetzt = "uebersetzt";
    public const string UeFehlgeschlagen = "fehlgeschlagen";

    public const string Gesendet = "gesendet";
    public const string Zugestellt = "zugestellt";

    public const int MaxZeichen = 2000;
    private const int OhneSeit = 100;
    private const int MitSeit = 200;

    private static IResult Fehler(string fehler, int status) => Results.Json(new { fehler }, statusCode: status);
    private static IResult ChatFehlt() => Fehler("chat_nicht_gefunden", 404);

    public static void MapChatEndpoints(this WebApplication app)
    {
        var chats = app.MapGroup("/chats").RequireAuthorization();
        chats.MapGet("", ListeAsync);
        chats.MapGet("/{id:long}/nachrichten", NachrichtenAsync);
        chats.MapPost("/{id:long}/nachrichten", SendenAsync);
        chats.MapPost("/{id:long}/gelesen", GelesenAsync);

        app.MapGet("/touren/{id:long}/chat", TourChatAsync).RequireAuthorization();
    }

    /// <summary>M08: alle Chats des Fahrers, neueste Nachricht zuerst.</summary>
    private static async Task<IResult> ListeAsync(ClaimsPrincipal nutzer, NpgsqlDataSource db)
    {
        var ids = TourEndpoints.Ids.Aus(nutzer);
        await using var verbindung = await db.OpenConnectionAsync();
        if (!await TourEndpoints.GueltigAsync(verbindung, null, ids))
        {
            return Results.Unauthorized();
        }
        return Results.Ok(await ChatsAsync(verbindung, null, ids, null));
    }

    /// <summary>M03/M10 „Disposition kontaktieren“: Chat der Tour holen, beim ersten Mal anlegen.</summary>
    private static async Task<IResult> TourChatAsync(long id, ClaimsPrincipal nutzer, NpgsqlDataSource db)
    {
        var ids = TourEndpoints.Ids.Aus(nutzer);
        await using var verbindung = await db.OpenConnectionAsync();
        await using var tx = await verbindung.BeginTransactionAsync();
        if (!await TourEndpoints.GueltigAsync(verbindung, tx, ids))
        {
            return Results.Unauthorized();
        }

        var (chatId, fehler) = await ChatDerTourAsync(verbindung, tx, ids, id);
        if (chatId is null)
        {
            return Fehler(fehler!, fehler == "tour_nicht_gefunden" ? 404 : 409);
        }

        var chat = (await ChatsAsync(verbindung, tx, ids, chatId)).Single();
        await tx.CommitAsync();
        return Results.Ok(chat);
    }

    /// <summary>
    /// Chat der Tour holen, beim ersten Mal anlegen. Ohne Chat kommt der Fehlercode
    /// tour_nicht_gefunden oder kein_disponent zurueck.
    /// </summary>
    private static async Task<(long? ChatId, string? Fehler)> ChatDerTourAsync(NpgsqlConnection verbindung,
        NpgsqlTransaction tx, TourEndpoints.Ids ids, long tourId)
    {
        // Tour sperren, damit zwei gleichzeitige Aufrufe nicht zwei Chats anlegen
        long? disponent;
        await using (var befehl = new NpgsqlCommand("""
            SELECT disponent_benutzer_id FROM public.tour
            WHERE tour_id = $1 AND fahrer_id = $2 AND unternehmen_id = $3
            FOR UPDATE
            """, verbindung, tx))
        {
            befehl.Parameters.Add(new() { Value = tourId });
            befehl.Parameters.Add(new() { Value = ids.FahrerId });
            befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
            await using var leser = await befehl.ExecuteReaderAsync();
            if (!await leser.ReadAsync())
            {
                return (null, "tour_nicht_gefunden");
            }
            disponent = leser.IsDBNull(0) ? null : leser.GetInt64(0);
        }

        await using (var befehl = new NpgsqlCommand("""
            SELECT chat_id FROM public.chat WHERE tour_id = $1 AND fahrer_benutzer_id = $2
            ORDER BY chat_id LIMIT 1
            """, verbindung, tx))
        {
            befehl.Parameters.Add(new() { Value = tourId });
            befehl.Parameters.Add(new() { Value = ids.BenutzerId });
            if (await befehl.ExecuteScalarAsync() is long vorhanden)
            {
                return (vorhanden, null);
            }
        }

        // Neuer Chat nur mit einem Disponenten der eigenen Firma
        if (disponent is not long disponentId || disponentId == ids.BenutzerId)
        {
            return (null, "kein_disponent");
        }
        await using (var pruefen = new NpgsqlCommand(
            "SELECT 1 FROM public.benutzer WHERE benutzer_id = $1 AND unternehmen_id = $2", verbindung, tx))
        {
            pruefen.Parameters.Add(new() { Value = disponentId });
            pruefen.Parameters.Add(new() { Value = ids.UnternehmenId });
            if (await pruefen.ExecuteScalarAsync() is null)
            {
                return (null, "kein_disponent");
            }
        }

        await using var anlegen = new NpgsqlCommand("""
            INSERT INTO public.chat (erstellungsdatum, status, tour_id, fahrer_benutzer_id, disponent_benutzer_id)
            VALUES (now(), $1, $2, $3, $4)
            RETURNING chat_id
            """, verbindung, tx);
        anlegen.Parameters.Add(new() { Value = ChatOffen });
        anlegen.Parameters.Add(new() { Value = tourId });
        anlegen.Parameters.Add(new() { Value = ids.BenutzerId });
        anlegen.Parameters.Add(new() { Value = disponentId });
        return ((long)(await anlegen.ExecuteScalarAsync())!, null);
    }

    /// <summary>
    /// Phase 5: automatische Nachricht des Fahrers an die Disposition seiner Tour (z. B. Fahrzeug nicht fahrbereit).
    /// [text] bekommt die Sprache des Fahrers und liefert den Text in dieser Sprache.
    /// Gibt false zurueck, wenn es keinen Chat gibt (kein Disponent) oder der Chat geschlossen ist.
    /// </summary>
    internal static async Task<bool> AutomatischSendenAsync(NpgsqlDataSource db, Uebersetzer uebersetzer,
        TourEndpoints.Ids ids, long tourId, Func<string, string> text)
    {
        await using var verbindung = await db.OpenConnectionAsync();
        long chatId;
        await using (var tx = await verbindung.BeginTransactionAsync())
        {
            var (gefunden, _) = await ChatDerTourAsync(verbindung, tx, ids, tourId);
            if (gefunden is not long id)
            {
                return false;
            }
            chatId = id;
            await tx.CommitAsync();
        }

        if (await ChatAsync(verbindung, ids, chatId) is not { Schreibbar: true } chat)
        {
            return false;
        }
        var inhalt = text(chat.FahrerSprache);
        if (inhalt.Length > MaxZeichen)
        {
            inhalt = inhalt[..MaxZeichen];
        }
        await SpeichernAsync(verbindung, uebersetzer, chat, ids, chatId, inhalt);
        return true;
    }

    /// <summary>
    /// M08 Verlauf. Ohne seit: die letzten 100 Nachrichten. Mit seit (nachricht_id der letzten bekannten Nachricht):
    /// neuere Nachrichten und zur Sicherheit alle der letzten 30 Sekunden; die App fuehrt sie nach ID zusammen.
    /// Offene Uebersetzungen der Disponenten-Nachrichten werden dabei erledigt und gespeichert.
    /// </summary>
    private static async Task<IResult> NachrichtenAsync(long id, long? seit, ClaimsPrincipal nutzer,
        NpgsqlDataSource db, Uebersetzer uebersetzer)
    {
        var ids = TourEndpoints.Ids.Aus(nutzer);
        await using var verbindung = await db.OpenConnectionAsync();
        if (!await TourEndpoints.GueltigAsync(verbindung, null, ids))
        {
            return Results.Unauthorized();
        }
        if (await ChatAsync(verbindung, ids, id) is not { } chat)
        {
            return ChatFehlt();
        }

        var sql = seit is null
            ? $"""
               SELECT * FROM (
                   SELECT {Spalten} FROM public.nachricht WHERE chat_id = $1
                   ORDER BY zeitstempel DESC, nachricht_id DESC LIMIT {OhneSeit}) n
               ORDER BY zeitstempel, nachricht_id
               """
            : $"""
               SELECT {Spalten} FROM public.nachricht
               WHERE chat_id = $1 AND (nachricht_id > $2 OR zeitstempel > now() - interval '30 seconds')
               ORDER BY zeitstempel, nachricht_id LIMIT {MitSeit}
               """;
        var nachrichten = new List<Nachricht>();
        await using (var befehl = new NpgsqlCommand(sql, verbindung))
        {
            befehl.Parameters.Add(new() { Value = id });
            if (seit is long ab)
            {
                befehl.Parameters.Add(new() { Value = ab });
            }
            await using var leser = await befehl.ExecuteReaderAsync();
            while (await leser.ReadAsync())
            {
                nachrichten.Add(Nachricht.Lesen(leser));
            }
        }

        var fremde = nachrichten.Where(n => n.BenutzerId != ids.BenutzerId).ToList();
        await UebersetzenAsync(verbindung, uebersetzer, fremde, chat.FahrerSprache, nochmalFehlgeschlagene: seit is null);

        // Abgeholte Disponenten-Nachrichten gelten als zugestellt
        var neuZugestellt = fremde.Where(n => n.Uebertragungsstatus == Gesendet).Select(n => n.Id).ToArray();
        if (neuZugestellt.Length > 0)
        {
            await using var befehl = new NpgsqlCommand(
                "UPDATE public.nachricht SET uebertragungsstatus = $1 WHERE nachricht_id = ANY($2) AND uebertragungsstatus = $3",
                verbindung);
            befehl.Parameters.Add(new() { Value = Zugestellt });
            befehl.Parameters.Add(new() { Value = neuZugestellt });
            befehl.Parameters.Add(new() { Value = Gesendet });
            await befehl.ExecuteNonQueryAsync();
        }

        // Eigene Nachrichten, die die Disposition noch nicht gelesen hat (fuer die Haekchen)
        var eigeneUngelesen = new List<long>();
        await using (var befehl = new NpgsqlCommand(
            "SELECT nachricht_id FROM public.nachricht WHERE chat_id = $1 AND benutzer_id = $2 AND NOT gelesen", verbindung))
        {
            befehl.Parameters.Add(new() { Value = id });
            befehl.Parameters.Add(new() { Value = ids.BenutzerId });
            await using var leser = await befehl.ExecuteReaderAsync();
            while (await leser.ReadAsync())
            {
                eigeneUngelesen.Add(leser.GetInt64(0));
            }
        }

        return Results.Ok(new
        {
            chatId = id,
            schreibbar = chat.Schreibbar,
            nachrichten = nachrichten.Select(n => n.FuerFahrer(ids.BenutzerId, chat.FahrerSprache)),
            eigeneUngelesen,
        });
    }

    /// <summary>M08: Nachricht senden. Die Uebersetzung darf fehlen, gespeichert wird immer.</summary>
    private static async Task<IResult> SendenAsync(long id, NachrichtAnfrage anfrage, ClaimsPrincipal nutzer,
        NpgsqlDataSource db, Uebersetzer uebersetzer)
    {
        var ids = TourEndpoints.Ids.Aus(nutzer);
        var text = (anfrage.Text ?? "").Trim();
        if (text.Length is 0 or > MaxZeichen)
        {
            return Fehler("eingabe_ungueltig", 400);
        }

        await using var verbindung = await db.OpenConnectionAsync();
        if (!await TourEndpoints.GueltigAsync(verbindung, null, ids))
        {
            return Results.Unauthorized();
        }
        if (await ChatAsync(verbindung, ids, id) is not { } chat)
        {
            return ChatFehlt();
        }
        if (!chat.Schreibbar)
        {
            return Fehler("chat_geschlossen", 409);
        }

        // Doppelt gesendet (App hat nach Netzabbruch erneut gesendet)? Dann die vorhandene Nachricht zurueckgeben.
        await using (var befehl = new NpgsqlCommand($"""
            SELECT {Spalten} FROM public.nachricht
            WHERE chat_id = $1 AND benutzer_id = $2 AND nachrichtentext = $3 AND zeitstempel > now() - interval '60 seconds'
            ORDER BY nachricht_id DESC LIMIT 1
            """, verbindung))
        {
            befehl.Parameters.Add(new() { Value = id });
            befehl.Parameters.Add(new() { Value = ids.BenutzerId });
            befehl.Parameters.Add(new() { Value = text });
            await using var leser = await befehl.ExecuteReaderAsync();
            if (await leser.ReadAsync())
            {
                return Results.Ok(Nachricht.Lesen(leser).FuerFahrer(ids.BenutzerId, chat.FahrerSprache));
            }
        }

        var neu = await SpeichernAsync(verbindung, uebersetzer, chat, ids, id, text);
        return Results.Created($"/chats/{id}/nachrichten", neu.FuerFahrer(ids.BenutzerId, chat.FahrerSprache));
    }

    /// <summary>Speichert eine Nachricht des Fahrers, sofort in die Sprache des Disponenten uebersetzt (wenn moeglich).</summary>
    private static async Task<Nachricht> SpeichernAsync(NpgsqlConnection verbindung, Uebersetzer uebersetzer,
        ChatDaten chat, TourEndpoints.Ids ids, long chatId, string text)
    {
        // Sofort in die Sprache des Disponenten uebersetzen
        string status;
        string? uebersetzung = null;
        if (chat.FahrerSprache == chat.DisponentSprache)
        {
            status = UeNichtNoetig;
        }
        else if (await uebersetzer.UebersetzenAsync(new[] { text }, chat.FahrerSprache, chat.DisponentSprache) is [var ergebnis])
        {
            (status, uebersetzung) = (UeUebersetzt, ergebnis);
        }
        else
        {
            // Ohne Schluessel bleibt sie ausstehend (kann spaeter noch uebersetzt werden)
            status = uebersetzer.Verfuegbar ? UeFehlgeschlagen : UeAusstehend;
        }

        await using (var befehl = new NpgsqlCommand($"""
            INSERT INTO public.nachricht (nachrichtentext, zeitstempel, gelesen, originalsprache, uebersetzungstext,
                                          zielsprache, uebersetzungsstatus, uebertragungsstatus, chat_id, benutzer_id)
            VALUES ($1, now(), false, $2, $3, $4, $5, $6, $7, $8)
            RETURNING {Spalten}
            """, verbindung))
        {
            befehl.Parameters.Add(new() { Value = text });
            befehl.Parameters.Add(new() { Value = chat.FahrerSprache });
            befehl.Parameters.Add(new() { Value = (object?)uebersetzung ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            befehl.Parameters.Add(new() { Value = chat.DisponentSprache });
            befehl.Parameters.Add(new() { Value = status });
            befehl.Parameters.Add(new() { Value = Gesendet });
            befehl.Parameters.Add(new() { Value = chatId });
            befehl.Parameters.Add(new() { Value = ids.BenutzerId });
            await using var leser = await befehl.ExecuteReaderAsync();
            await leser.ReadAsync();
            return Nachricht.Lesen(leser);
        }
    }

    /// <summary>M08: Verlauf geoeffnet, alle Nachrichten der Disposition gelten als gelesen.</summary>
    private static async Task<IResult> GelesenAsync(long id, ClaimsPrincipal nutzer, NpgsqlDataSource db)
    {
        var ids = TourEndpoints.Ids.Aus(nutzer);
        await using var verbindung = await db.OpenConnectionAsync();
        if (!await TourEndpoints.GueltigAsync(verbindung, null, ids))
        {
            return Results.Unauthorized();
        }
        if (await ChatAsync(verbindung, ids, id) is null)
        {
            return ChatFehlt();
        }

        await using var befehl = new NpgsqlCommand("""
            UPDATE public.nachricht SET gelesen = true, gelesen_am = now(), uebertragungsstatus = $3
            WHERE chat_id = $1 AND benutzer_id <> $2 AND NOT gelesen
            """, verbindung);
        befehl.Parameters.Add(new() { Value = id });
        befehl.Parameters.Add(new() { Value = ids.BenutzerId });
        befehl.Parameters.Add(new() { Value = Zugestellt });
        return Results.Ok(new { gelesen = await befehl.ExecuteNonQueryAsync() });
    }

    /// <summary>
    /// Uebersetzt Disponenten-Nachrichten in die Sprache des Fahrers und speichert das Ergebnis.
    /// Betroffen: ausstehende, in eine andere Sprache uebersetzte (Fahrer hat die Sprache gewechselt)
    /// und beim vollen Laden auch fehlgeschlagene.
    /// </summary>
    private static async Task UebersetzenAsync(NpgsqlConnection verbindung, Uebersetzer uebersetzer,
        List<Nachricht> nachrichten, string ziel, bool nochmalFehlgeschlagene)
    {
        var gleicheSprache = nachrichten
            .Where(n => n.Originalsprache == ziel && n.Uebersetzungsstatus == UeAusstehend)
            .ToList();
        var offen = nachrichten
            .Where(n => n.Originalsprache != ziel && n.Uebersetzungsstatus switch
            {
                UeAusstehend or UeNichtNoetig => true,
                UeUebersetzt => n.Zielsprache != ziel || n.Uebersetzungstext is null,
                UeFehlgeschlagen => nochmalFehlgeschlagene,
                _ => false,
            })
            .ToList();

        await using var batch = new NpgsqlBatch(verbindung);
        foreach (var n in gleicheSprache)
        {
            n.Uebersetzungsstatus = UeNichtNoetig;
            n.Zielsprache = ziel;
            batch.BatchCommands.Add(Speichern(n));
        }

        if (uebersetzer.Verfuegbar)
        {
            foreach (var gruppe in offen.GroupBy(n => n.Originalsprache))
            {
                var liste = gruppe.ToList();
                var ergebnis = await uebersetzer.UebersetzenAsync(liste.Select(n => n.Text).ToList(), gruppe.Key, ziel);
                for (var i = 0; i < liste.Count; i++)
                {
                    var n = liste[i];
                    if (ergebnis is not null)
                    {
                        (n.Uebersetzungstext, n.Zielsprache, n.Uebersetzungsstatus) = (ergebnis[i], ziel, UeUebersetzt);
                    }
                    else if (n.Uebersetzungsstatus == UeAusstehend)
                    {
                        n.Uebersetzungsstatus = UeFehlgeschlagen;
                    }
                    else
                    {
                        continue;
                    }
                    batch.BatchCommands.Add(Speichern(n));
                }
            }
        }

        if (batch.BatchCommands.Count > 0)
        {
            await batch.ExecuteNonQueryAsync();
        }

        static NpgsqlBatchCommand Speichern(Nachricht n)
        {
            var befehl = new NpgsqlBatchCommand("""
                UPDATE public.nachricht SET uebersetzungstext = $1, zielsprache = $2, uebersetzungsstatus = $3
                WHERE nachricht_id = $4
                """);
            befehl.Parameters.Add(new() { Value = (object?)n.Uebersetzungstext ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            befehl.Parameters.Add(new() { Value = (object?)n.Zielsprache ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            befehl.Parameters.Add(new() { Value = n.Uebersetzungsstatus });
            befehl.Parameters.Add(new() { Value = n.Id });
            return befehl;
        }
    }

    /// <summary>Chat des Fahrers mit Sprachen und ob geschrieben werden darf. null = nicht seiner.</summary>
    private static async Task<ChatDaten?> ChatAsync(NpgsqlConnection verbindung, TourEndpoints.Ids ids, long chatId)
    {
        await using var befehl = new NpgsqlCommand($"""
            SELECT f.sprache, d.sprache, {Schreibbar}
            FROM public.chat c
            JOIN public.tour t ON t.tour_id = c.tour_id
            JOIN public.benutzer f ON f.benutzer_id = c.fahrer_benutzer_id
            JOIN public.benutzer d ON d.benutzer_id = c.disponent_benutzer_id
            WHERE c.chat_id = $1 AND c.fahrer_benutzer_id = $2 AND t.unternehmen_id = $3
            """, verbindung);
        befehl.Parameters.Add(new() { Value = chatId });
        befehl.Parameters.Add(new() { Value = ids.BenutzerId });
        befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
        await using var leser = await befehl.ExecuteReaderAsync();
        if (!await leser.ReadAsync())
        {
            return null;
        }
        return new ChatDaten(leser.GetString(0), leser.GetString(1), leser.GetBoolean(2));
    }

    // Gilt fuer c = chat, t = tour
    private const string Schreibbar = $"(lower(c.status) <> '{ChatGeschlossen}' AND now() <= t.endzeit + interval '7 days')";

    /// <summary>Chats des Fahrers fuer die Liste (oder nur [chatId]).</summary>
    private static async Task<List<object>> ChatsAsync(NpgsqlConnection verbindung, NpgsqlTransaction? tx,
        TourEndpoints.Ids ids, long? chatId)
    {
        await using var befehl = new NpgsqlCommand($"""
            SELECT c.chat_id, c.tour_id, t.tournummer, t.status, d.vorname, d.nachname, {Schreibbar},
                   n.nachrichtentext, n.uebersetzungstext, n.uebersetzungsstatus, n.zielsprache, n.zeitstempel,
                   n.benutzer_id = $1,
                   (SELECT count(*) FROM public.nachricht u
                    WHERE u.chat_id = c.chat_id AND u.benutzer_id <> $1 AND NOT u.gelesen),
                   f.sprache
            FROM public.chat c
            JOIN public.tour t ON t.tour_id = c.tour_id
            JOIN public.benutzer d ON d.benutzer_id = c.disponent_benutzer_id
            JOIN public.benutzer f ON f.benutzer_id = c.fahrer_benutzer_id
            LEFT JOIN LATERAL (
                SELECT * FROM public.nachricht x WHERE x.chat_id = c.chat_id
                ORDER BY x.zeitstempel DESC, x.nachricht_id DESC LIMIT 1) n ON true
            WHERE c.fahrer_benutzer_id = $1 AND t.unternehmen_id = $2 AND ($3::bigint IS NULL OR c.chat_id = $3)
            ORDER BY COALESCE(n.zeitstempel, c.erstellungsdatum) DESC
            """, verbindung, tx);
        befehl.Parameters.Add(new() { Value = ids.BenutzerId });
        befehl.Parameters.Add(new() { Value = ids.UnternehmenId });
        befehl.Parameters.Add(new() { Value = (object?)chatId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });

        var liste = new List<object>();
        await using var leser = await befehl.ExecuteReaderAsync();
        while (await leser.ReadAsync())
        {
            object? letzte = null;
            if (!leser.IsDBNull(7))
            {
                var eigen = leser.GetBoolean(12);
                // Fremde Nachricht in der Uebersetzung zeigen, wenn sie schon in der Sprache des Fahrers vorliegt
                var uebersetzt = !eigen && !leser.IsDBNull(8) && leser.GetString(9) == UeUebersetzt
                                 && !leser.IsDBNull(10) && leser.GetString(10) == leser.GetString(14);
                letzte = new
                {
                    text = uebersetzt ? leser.GetString(8) : leser.GetString(7),
                    zeit = leser.GetDateTime(11),
                    eigen,
                };
            }
            liste.Add(new
            {
                chatId = leser.GetInt64(0),
                tourId = leser.GetInt64(1),
                tournummer = leser.GetString(2),
                tourStatus = leser.GetString(3),
                disponent = $"{leser.GetString(4)} {leser.GetString(5)}".Trim(),
                schreibbar = leser.GetBoolean(6),
                letzteNachricht = letzte,
                ungelesen = leser.GetInt64(13),
            });
        }
        return liste;
    }

    private const string Spalten = """
        nachricht_id, nachrichtentext, zeitstempel, gelesen, originalsprache, uebersetzungstext,
        zielsprache, uebersetzungsstatus, uebertragungsstatus, benutzer_id
        """;

    private sealed record ChatDaten(string FahrerSprache, string DisponentSprache, bool Schreibbar);

    private sealed class Nachricht
    {
        public long Id { get; init; }
        public string Text { get; init; } = "";
        public DateTime Zeit { get; init; }
        public bool Gelesen { get; init; }
        public string Originalsprache { get; init; } = "";
        public string? Uebersetzungstext { get; set; }
        public string? Zielsprache { get; set; }
        public string Uebersetzungsstatus { get; set; } = "";
        public string Uebertragungsstatus { get; init; } = "";
        public long BenutzerId { get; init; }

        // Reihenfolge wie in Spalten
        public static Nachricht Lesen(NpgsqlDataReader leser) => new()
        {
            Id = leser.GetInt64(0),
            Text = leser.GetString(1),
            Zeit = leser.GetDateTime(2),
            Gelesen = leser.GetBoolean(3),
            Originalsprache = leser.GetString(4),
            Uebersetzungstext = leser.IsDBNull(5) ? null : leser.GetString(5),
            Zielsprache = leser.IsDBNull(6) ? null : leser.GetString(6),
            Uebersetzungsstatus = leser.GetString(7),
            Uebertragungsstatus = leser.GetString(8),
            BenutzerId = leser.GetInt64(9),
        };

        /// <summary>Text fuer den Fahrer: bei fremden Nachrichten die Uebersetzung in seine Sprache, sonst das Original.</summary>
        public object FuerFahrer(long benutzerId, string sprache)
        {
            var eigen = BenutzerId == benutzerId;
            var uebersetzt = !eigen && Uebersetzungsstatus == UeUebersetzt && Zielsprache == sprache && Uebersetzungstext is not null;
            return new
            {
                nachrichtId = Id,
                text = uebersetzt ? Uebersetzungstext : Text,
                original = Text,
                originalsprache = Originalsprache,
                uebersetzt,
                eigen,
                zeit = Zeit,
                gelesen = Gelesen,
            };
        }
    }
}
