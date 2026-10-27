using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Abteilungen der eigenen Firma für den Kundenadmin (K05). Jede Methode prüft zuerst die Rolle Kundenadmin
/// (beim Admin-Zugang die Admin-Rolle) und verwendet danach nur die Firma aus dieser Prüfung.
/// Abteilungen werden nie gelöscht, sondern deaktiviert: Benutzer, Fahrzeuge und Touren verweisen auf sie.
/// Voraussetzung: 006_abteilung_aktiv.sql wurde in Neon ausgeführt.
/// </summary>
public sealed class AbteilungRepository
{
    private const FirmenRolle Rolle = FirmenRolle.Kundenadmin;
    private const int MaximaleLaenge = 100;

    // Erst beim ersten Zugriff verbinden, wie im UnternehmenRepository.
    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    public async Task<IReadOnlyList<Abteilung>> LadenAsync(CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        const string query = """
            SELECT a.abteilung_id, a.bezeichnung, a.aktiv,
                   (SELECT count(*) FROM public.benutzer b WHERE b.abteilung_id = a.abteilung_id AND b.unternehmen_id = a.unternehmen_id),
                   (SELECT count(*) FROM public.fahrzeug f WHERE f.abteilung_id = a.abteilung_id AND f.unternehmen_id = a.unternehmen_id),
                   (SELECT count(*) FROM public.tour t WHERE t.abteilung_id = a.abteilung_id AND t.unternehmen_id = a.unternehmen_id
                      AND lower(t.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%')
            FROM public.abteilung a
            WHERE a.unternehmen_id = $1::bigint
            ORDER BY NOT a.aktiv, lower(a.bezeichnung), a.abteilung_id
            """;
        var rows = await AbfragenAsync(query, new string?[] { Text(zugriff.UnternehmenId) }, cancellationToken);
        return rows.Select(row => new Abteilung(
            Id(row, 0), Required(row, 1), Required(row, 2) == "t",
            Ganzzahl(row, 3), Ganzzahl(row, 4), Ganzzahl(row, 5))).ToArray();
    }

    /// <exception cref="AbteilungVergebenException">Den Namen gibt es in der Firma schon.</exception>
    public async Task AnlegenAsync(string bezeichnung, CancellationToken cancellationToken = default)
    {
        string name = Pruefen(bezeichnung);
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        const string query = """
            WITH neu AS (
                INSERT INTO public.abteilung (bezeichnung, unternehmen_id, aktiv)
                VALUES ($2, $1::bigint, true)
                RETURNING abteilung_id, bezeichnung, unternehmen_id
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
                SELECT CASE WHEN $3::bigint IS NULL THEN 'Entwicklungsansicht' ELSE 'Benutzer' END,
                       'Abteilung angelegt', 'Gespeichert', 'Abteilung ' || n.bezeichnung, n.unternehmen_id, n.abteilung_id, $3::bigint
                FROM neu n
            )
            SELECT abteilung_id FROM neu
            """;
        await AusfuehrenAsync(query, new[] { Text(zugriff.UnternehmenId), name, Konto() }, cancellationToken);
    }

    /// <exception cref="AbteilungVergebenException">Den Namen gibt es in der Firma schon.</exception>
    public async Task UmbenennenAsync(long id, string bezeichnung, CancellationToken cancellationToken = default)
    {
        string name = Pruefen(bezeichnung);
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        const string query = """
            WITH alt AS (
                SELECT bezeichnung FROM public.abteilung WHERE abteilung_id = $2::bigint AND unternehmen_id = $1::bigint
            ), geaendert AS (
                UPDATE public.abteilung SET bezeichnung = $3
                WHERE abteilung_id = $2::bigint AND unternehmen_id = $1::bigint
                RETURNING abteilung_id, unternehmen_id
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
                SELECT CASE WHEN $4::bigint IS NULL THEN 'Entwicklungsansicht' ELSE 'Benutzer' END,
                       'Abteilung umbenannt', 'Gespeichert', (SELECT bezeichnung FROM alt) || ' → ' || $3,
                       g.unternehmen_id, g.abteilung_id, $4::bigint
                FROM geaendert g
            )
            SELECT abteilung_id FROM geaendert
            """;
        await AusfuehrenAsync(query, new[] { Text(zugriff.UnternehmenId), Text(id), name, Konto() }, cancellationToken);
    }

    /// <summary>
    /// Aktiviert oder deaktiviert eine Abteilung. Die letzte aktive Abteilung kann nicht deaktiviert werden,
    /// weil jede neue Tour eine Abteilung braucht.
    /// </summary>
    public async Task AktivSetzenAsync(long id, bool aktiv, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        const string query = """
            WITH geaendert AS (
                UPDATE public.abteilung a SET aktiv = $3::boolean
                WHERE a.abteilung_id = $2::bigint AND a.unternehmen_id = $1::bigint
                  AND ($3::boolean OR EXISTS (SELECT 1 FROM public.abteilung x
                                              WHERE x.unternehmen_id = $1::bigint AND x.aktiv
                                                AND x.abteilung_id <> $2::bigint))
                RETURNING a.abteilung_id, a.bezeichnung, a.unternehmen_id
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
                SELECT CASE WHEN $4::bigint IS NULL THEN 'Entwicklungsansicht' ELSE 'Benutzer' END,
                       CASE WHEN $3::boolean THEN 'Abteilung aktiviert' ELSE 'Abteilung deaktiviert' END,
                       'Gespeichert', 'Abteilung ' || g.bezeichnung, g.unternehmen_id, g.abteilung_id, $4::bigint
                FROM geaendert g
            )
            SELECT abteilung_id FROM geaendert
            """;
        var rows = await AbfragenAsync(query,
            new[] { Text(zugriff.UnternehmenId), Text(id), aktiv ? "true" : "false", Konto() }, cancellationToken);
        if (rows.Count != 1)
            throw new ArgumentException(aktiv
                ? "Die Abteilung gibt es nicht mehr. Bitte laden Sie die Liste neu."
                : "Die letzte aktive Abteilung kann nicht deaktiviert werden. Jede neue Tour braucht eine Abteilung.");
    }

    // Fehlt die Spalte aktiv (Code 42703 = Spalte unbekannt), wurde 006_abteilung_aktiv.sql noch nicht ausgeführt.
    private async Task<IReadOnlyList<string?[]>> AbfragenAsync(string query, IReadOnlyList<string?> parameter,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _client.Value.QueryAsync(query, parameter, cancellationToken);
        }
        catch (NeonSqlException exception) when (exception.Code == "42703")
        {
            throw new MigrationFehltException(exception);
        }
    }

    private async Task AusfuehrenAsync(string query, string?[] parameter, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await AbfragenAsync(query, parameter, cancellationToken);
            if (rows.Count != 1)
                throw new ArgumentException("Die Abteilung gibt es nicht mehr. Bitte laden Sie die Liste neu.");
        }
        catch (NeonSqlException exception) when (exception.Code == "23505")
        {
            // Die Datenbank prüft eindeutige Namen je Firma (UNIQUE unternehmen_id, bezeichnung).
            throw new AbteilungVergebenException(exception);
        }
    }

    private static string Pruefen(string? bezeichnung)
    {
        string name = string.Join(' ', (bezeichnung ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0 || name.Length > MaximaleLaenge)
            throw new ArgumentException($"Bitte einen Namen mit 1 bis {MaximaleLaenge} Zeichen eingeben.");
        return name;
    }

    // Wer ändert: das angemeldete Konto (beim Admin-Zugang der MegakrasserAdmin selbst).
    private static string? Konto() => BenutzerSitzung.AktuellerBenutzer is { } benutzer ? Text(benutzer.Id) : null;

    private static int Ganzzahl(string?[] row, int index) => int.Parse(Required(row, index), CultureInfo.InvariantCulture);

    private static long Id(string?[] row, int index) => long.Parse(Required(row, index), CultureInfo.InvariantCulture);

    private static string Text(long wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}

/// <summary>Die Datenbank kennt abteilung.aktiv noch nicht: Das Migrationsskript fehlt.</summary>
public sealed class MigrationFehltException : Exception
{
    public MigrationFehltException(Exception innerException)
        : base("In der Datenbank fehlt noch die Spalte „aktiv“ für Abteilungen. Bitte Services/006_abteilung_aktiv.sql " +
               "einmal im Neon SQL Editor ausführen und die Ansicht danach neu öffnen.", innerException) { }
}

/// <summary>Eine verständliche Meldung statt des technischen PostgreSQL-Fehlers.</summary>
public sealed class AbteilungVergebenException : Exception
{
    public AbteilungVergebenException(Exception innerException)
        : base("Eine Abteilung mit diesem Namen gibt es in Ihrem Unternehmen schon.", innerException) { }
}
