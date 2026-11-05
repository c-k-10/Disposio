using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Chat zwischen Disponent und Fahrer für D09 „Nachrichten mit Fahrer“ (Tabellen chat und nachricht).
/// Ein Chat gehört zu genau einer Tour und deren Fahrer; alle Disponenten der Firma sehen und schreiben darin.
/// Wie in den anderen Repositories wird zuerst die Rolle Disponent geprüft und danach nur die Firma aus dieser
/// Prüfung verwendet. Jede Abfrage prüft zusätzlich, dass die Tour zu dieser Firma gehört.
/// Format und Ablauf für die Fahrer-App: docs/04_Architektur/Chat-und-Uebersetzung.md
/// </summary>
public sealed class NachrichtenRepository
{
    private const FirmenRolle Rolle = FirmenRolle.Disponent;
    public const int MaximaleLaenge = 2000;

    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    // Links in D09: Touren mit Fahrer von heute, laufende Touren und Touren mit Nachrichten der letzten 48 Stunden.
    // $2 = eine Tour, die immer dabei ist (z. B. aus D07 „Fahrer kontaktieren“ geöffnet, auch wenn sie morgen fährt).
    // Ungelesen = Nachrichten des Fahrers, die noch kein Disponent geöffnet hat. Ungelesene zuerst.
    private const string GespraecheQuery = """
        WITH h AS (SELECT (now() AT TIME ZONE 'Europe/Berlin')::date AS tag)
        SELECT t.tour_id, t.tournummer, COALESCE(fz.kennzeichen, ''), fb.benutzer_id,
               fb.vorname || ' ' || fb.nachname, fb.sprache,
               to_char(lm.zeit AT TIME ZONE 'Europe/Berlin', 'YYYY-MM-DD HH24:MI'), u.anzahl
        FROM public.tour t
        JOIN public.fahrer f ON f.fahrer_id = t.fahrer_id
        JOIN public.benutzer fb ON fb.benutzer_id = f.benutzer_id
        LEFT JOIN public.fahrzeug fz ON fz.fahrzeug_id = t.fahrzeug_id
        LEFT JOIN LATERAL (
            SELECT max(n.zeitstempel) AS zeit FROM public.chat c
            JOIN public.nachricht n ON n.chat_id = c.chat_id
            WHERE c.tour_id = t.tour_id AND c.fahrer_benutzer_id = fb.benutzer_id) lm ON true
        LEFT JOIN LATERAL (
            SELECT count(*) AS anzahl FROM public.chat c
            JOIN public.nachricht n ON n.chat_id = c.chat_id
            WHERE c.tour_id = t.tour_id AND c.fahrer_benutzer_id = fb.benutzer_id
              AND n.benutzer_id = fb.benutzer_id AND NOT n.gelesen) u ON true
        CROSS JOIN h
        WHERE t.unternehmen_id = $1::bigint
          AND ((t.startzeit AT TIME ZONE 'Europe/Berlin')::date = h.tag
               OR t.status = 'Unterwegs'
               OR lm.zeit > now() - interval '48 hours'
               OR t.tour_id = $2::bigint)
        ORDER BY (t.tour_id = $2::bigint) IS TRUE DESC, (u.anzahl > 0) DESC, lm.zeit DESC NULLS LAST, t.startzeit, t.tournummer
        LIMIT 50
        """;

    // Verlauf einer Tour mit ihrem aktuellen Fahrer. $3 = angemeldetes Konto (für „von mir“).
    private const string VerlaufQuery = """
        SELECT n.nachricht_id, n.benutzer_id = c.fahrer_benutzer_id, n.benutzer_id = $3::bigint,
               COALESCE(b.vorname || ' ' || b.nachname, ''), n.nachrichtentext, n.uebersetzungstext,
               n.originalsprache, COALESCE(n.zielsprache, ''), n.uebersetzungsstatus,
               to_char(n.zeitstempel AT TIME ZONE 'Europe/Berlin', 'YYYY-MM-DD HH24:MI'),
               n.uebertragungsstatus, n.gelesen,
               to_char(n.gelesen_am AT TIME ZONE 'Europe/Berlin', 'YYYY-MM-DD HH24:MI')
        FROM public.tour t
        JOIN public.fahrer f ON f.fahrer_id = t.fahrer_id
        JOIN public.chat c ON c.tour_id = t.tour_id AND c.fahrer_benutzer_id = f.benutzer_id
        JOIN public.nachricht n ON n.chat_id = c.chat_id
        LEFT JOIN public.benutzer b ON b.benutzer_id = n.benutzer_id
        WHERE t.unternehmen_id = $1::bigint AND t.tour_id = $2::bigint
        ORDER BY n.zeitstempel, n.nachricht_id
        """;

    // Beim Öffnen: Nachrichten des Fahrers gelten als gelesen.
    private const string GelesenQuery = """
        UPDATE public.nachricht n SET gelesen = true, gelesen_am = now()
        FROM public.chat c
        JOIN public.tour t ON t.tour_id = c.tour_id
        JOIN public.fahrer f ON f.fahrer_id = t.fahrer_id AND f.benutzer_id = c.fahrer_benutzer_id
        WHERE n.chat_id = c.chat_id AND t.unternehmen_id = $1::bigint AND t.tour_id = $2::bigint
          AND n.benutzer_id = c.fahrer_benutzer_id AND NOT n.gelesen
        """;

    // Senden in EINEM Befehl: Chat finden oder anlegen, dann die Nachricht im Original speichern.
    // $1 Firma, $2 Tour, $3 Konto, $4 Text, $5 Sprache des Kontos.
    // Gleiche Sprache wie der Fahrer: keine Übersetzung nötig. Sonst wartet sie auf die Neon Function.
    private const string SendenQuery = """
        WITH t AS (
            SELECT t.tour_id, f.benutzer_id AS fahrer_bid, fb.sprache AS fahrer_sprache
            FROM public.tour t
            JOIN public.fahrer f ON f.fahrer_id = t.fahrer_id
            JOIN public.benutzer fb ON fb.benutzer_id = f.benutzer_id
            WHERE t.tour_id = $2::bigint AND t.unternehmen_id = $1::bigint
        ), vorhanden AS (
            SELECT c.chat_id FROM public.chat c, t
            WHERE c.tour_id = t.tour_id AND c.fahrer_benutzer_id = t.fahrer_bid
            ORDER BY c.chat_id LIMIT 1
        ), neu AS (
            INSERT INTO public.chat (status, tour_id, fahrer_benutzer_id, disponent_benutzer_id)
            SELECT 'offen', t.tour_id, t.fahrer_bid, $3::bigint FROM t
            WHERE NOT EXISTS (SELECT 1 FROM vorhanden)
            RETURNING chat_id
        ), c AS (
            SELECT chat_id FROM vorhanden UNION ALL SELECT chat_id FROM neu
        )
        INSERT INTO public.nachricht (nachrichtentext, originalsprache, zielsprache, uebersetzungsstatus,
                                      uebertragungsstatus, chat_id, benutzer_id)
        SELECT $4, $5, t.fahrer_sprache,
               CASE WHEN t.fahrer_sprache = $5 THEN 'nicht_noetig' ELSE 'ausstehend' END,
               'gesendet', c.chat_id, $3::bigint
        FROM t, c
        RETURNING nachricht_id, uebersetzungsstatus
        """;

    /// <param name="immerDabeiTourId">Diese Tour steht immer oben in der Liste, wenn sie einen Fahrer hat.</param>
    public async Task<IReadOnlyList<Gespraech>> GespraecheLadenAsync(long? immerDabeiTourId = null,
        CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var rows = await _client.Value.QueryAsync(GespraecheQuery, new string?[]
        {
            Text(zugriff.UnternehmenId),
            immerDabeiTourId is long tourId ? Text(tourId) : null
        }, cancellationToken);

        var liste = new List<Gespraech>();
        foreach (var row in rows)
        {
            liste.Add(new Gespraech(
                Id(row, 0), Required(row, 1), Required(row, 2), Id(row, 3), Required(row, 4), Required(row, 5),
                row[6] is string zeit ? Zeit(zeit) : null,
                int.Parse(Required(row, 7), CultureInfo.InvariantCulture)));
        }
        return liste;
    }

    /// <summary>Lädt den Verlauf und markiert die Nachrichten des Fahrers als gelesen.</summary>
    public async Task<IReadOnlyList<ChatNachricht>> VerlaufLadenAsync(long tourId, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        string firma = Text(zugriff.UnternehmenId);
        string tour = Text(tourId);

        var rows = await _client.Value.QueryAsync(VerlaufQuery, new string?[] { firma, tour, MeineId() }, cancellationToken);
        await _client.Value.QueryAsync(GelesenQuery, new string?[] { firma, tour }, cancellationToken);

        var liste = new List<ChatNachricht>();
        foreach (var row in rows)
        {
            liste.Add(new ChatNachricht(
                Id(row, 0), Required(row, 1) == "t", row[2] == "t", Required(row, 3), Required(row, 4), row[5],
                Required(row, 6), Required(row, 7), Required(row, 8), Zeit(Required(row, 9)),
                Required(row, 10), row[11] == "t", row[12] is string gelesenAm ? Zeit(gelesenAm) : null));
        }
        return liste;
    }

    /// <summary>
    /// Speichert die Nachricht im Original und gibt (Id, braucht Übersetzung) zurück.
    /// Die Übersetzung startet danach <see cref="UebersetzungDienst"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Leerer oder zu langer Text, oder die Tour hat keinen Fahrer (mehr).</exception>
    public async Task<(long Id, bool BrauchtUebersetzung)> SendenAsync(long tourId, string text,
        CancellationToken cancellationToken = default)
    {
        string inhalt = (text ?? string.Empty).Trim();
        if (inhalt.Length == 0)
            throw new ArgumentException("Bitte eine Nachricht eingeben.");
        if (inhalt.Length > MaximaleLaenge)
            throw new ArgumentException($"Die Nachricht darf höchstens {MaximaleLaenge} Zeichen lang sein.");

        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var benutzer = BenutzerSitzung.AktuellerBenutzer
            ?? throw new UnauthorizedAccessException("Bitte melden Sie sich erneut an.");

        var rows = await _client.Value.QueryAsync(SendenQuery, new string?[]
        {
            Text(zugriff.UnternehmenId), Text(tourId), Text(benutzer.Id), inhalt, benutzer.Sprache
        }, cancellationToken);
        if (rows.Count != 1)
            throw new ArgumentException("Die Nachricht konnte nicht gesendet werden: Die Tour hat keinen Fahrer mehr.");

        return (Id(rows[0], 0), Required(rows[0], 1) == UebersetzungsStatus.Ausstehend);
    }

    private static string? MeineId() =>
        BenutzerSitzung.AktuellerBenutzer is { } benutzer ? Text(benutzer.Id) : null;

    private static DateTime Zeit(string text) =>
        DateTime.ParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static long Id(string?[] row, int index) => long.Parse(Required(row, index), CultureInfo.InvariantCulture);

    private static string Text(long wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}
