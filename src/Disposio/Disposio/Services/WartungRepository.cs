using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>Vertrag zwischen F01, F04, F05 und der Datenbank für Wartungen.</summary>
public interface IWartungRepository
{
    Task<WartungDaten> LadenAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Wartungstermin>> OffeneTermineLadenAsync(CancellationToken cancellationToken = default);
    Task<Wartungstermin?> TerminLadenAsync(long wartungId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WartungFahrzeug>> FahrzeugeLadenAsync(CancellationToken cancellationToken = default);
    Task TerminAnlegenAsync(WartungsterminEntwurf entwurf, CancellationToken cancellationToken = default);
    Task DurchfuehrungEintragenAsync(WartungDurchfuehrung durchfuehrung, CancellationToken cancellationToken = default);
    Task<bool> TerminEntfernenAsync(long wartungId, CancellationToken cancellationToken = default);
}

/// <summary>
/// SQL für public.wartung (US-05, #89–#92). Wie im FahrzeugRepository prüft jede Methode zuerst die Rolle
/// Fuhrparkmanager und verwendet danach nur die Firma aus dieser Prüfung, nie eine Id aus der Oberfläche.
/// Eine Zeile ist ein offener Termin (status 'Geplant') oder eine durchgeführte Wartung ('Erledigt'),
/// siehe 010_fahrerzuordnung_wartung.sql. Voraussetzung: Diese Migration wurde in Neon ausgeführt.
/// </summary>
public sealed class WartungRepository : IWartungRepository
{
    private const FirmenRolle Rolle = FirmenRolle.Fuhrparkmanager;
    public const int BemerkungMaximal = 500;
    public const int KilometerMaximal = 5_000_000;
    public const decimal KostenMaximal = 1_000_000m;

    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    // Offene Termine aktiver Fahrzeuge der Firma ($1). ReadTermin liest genau diese Spalten.
    private const string TerminAbfrage = """
        SELECT w.wartung_id, w.fahrzeug_id, f.kennzeichen, w.wartungsart, to_char(w.datum, 'YYYY-MM-DD'),
               w.faellig_bei_kilometerstand, f.kilometerstand, w.beschreibung
        FROM public.wartung w
        JOIN public.fahrzeug f ON f.fahrzeug_id = w.fahrzeug_id
        WHERE f.unternehmen_id = $1::bigint AND f.aktiv = true AND w.status = 'Geplant'
        """;

    public async Task<WartungDaten> LadenAsync(CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var firma = new string?[] { Text(zugriff.UnternehmenId) };

        var termine = await TermineAsync(firma, cancellationToken);

        // #91 Historie: alle durchgeführten Wartungen, auch von inzwischen inaktiven Fahrzeugen. Neueste oben.
        var historie = (await _client.Value.QueryAsync("""
                SELECT w.wartung_id, w.fahrzeug_id, f.kennzeichen, w.wartungsart, to_char(w.datum, 'YYYY-MM-DD'),
                       w.kilometerstand_bei_durchfuehrung, w.kosten, w.beschreibung, to_char(w.naechster_termin, 'YYYY-MM-DD')
                FROM public.wartung w
                JOIN public.fahrzeug f ON f.fahrzeug_id = w.fahrzeug_id
                WHERE f.unternehmen_id = $1::bigint AND w.status = 'Erledigt'
                ORDER BY w.datum DESC, w.wartung_id DESC
                """, firma, cancellationToken))
            .Select(row => new DurchgefuehrteWartung(
                Id(row, 0), Id(row, 1), Required(row, 2), Required(row, 3), Datum(Required(row, 4)),
                // Ältere Zeilen ohne Kilometerstand (vor der Migration) zeigen 0 statt abzustürzen.
                row[5] is string km ? int.Parse(km, CultureInfo.InvariantCulture) : 0,
                decimal.Parse(Required(row, 6), CultureInfo.InvariantCulture),
                row[7] ?? string.Empty,
                row[8] is string folge ? Datum(folge) : null))
            .ToArray();

        var fahrzeuge = await FahrzeugeAsync(firma, cancellationToken);
        return new WartungDaten(termine, historie, fahrzeuge);
    }

    /// <summary>Nur die offenen Termine, zum Beispiel für die Spalte „Wartung“ in F01 (#92).</summary>
    public async Task<IReadOnlyList<Wartungstermin>> OffeneTermineLadenAsync(CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        return await TermineAsync(new string?[] { Text(zugriff.UnternehmenId) }, cancellationToken);
    }

    /// <summary>Ein offener Termin für „Erledigt eintragen“ in F05. Null, wenn er nicht (mehr) offen ist.</summary>
    public async Task<Wartungstermin?> TerminLadenAsync(long wartungId, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var rows = await _client.Value.QueryAsync($"{TerminAbfrage} AND w.wartung_id = $2::bigint",
            new string?[] { Text(zugriff.UnternehmenId), Text(wartungId) }, cancellationToken);
        return rows.Count == 1 ? ReadTermin(rows[0]) : null;
    }

    public async Task<IReadOnlyList<WartungFahrzeug>> FahrzeugeLadenAsync(CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        return await FahrzeugeAsync(new string?[] { Text(zugriff.UnternehmenId) }, cancellationToken);
    }

    /// <summary>#89: Für ein aktives Fahrzeug der Firma einen Wartungstermin anlegen.</summary>
    public async Task TerminAnlegenAsync(WartungsterminEntwurf entwurf, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entwurf);
        ArtPruefen(entwurf.Wartungsart);
        DatumPruefen(entwurf.Termin, "Der Termin");
        if (entwurf.FaelligBeiKm is int faelligKm)
            KilometerPruefen(faelligKm, "Der Fälligkeits-Kilometerstand");
        string bemerkung = BemerkungPruefen(entwurf.Bemerkung);

        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        const string query = """
            WITH fz AS (
                SELECT fahrzeug_id, kennzeichen, unternehmen_id, abteilung_id FROM public.fahrzeug
                WHERE fahrzeug_id = $1::bigint AND unternehmen_id = $2::bigint AND aktiv = true
            ), neu AS (
                INSERT INTO public.wartung
                    (wartungsart, datum, beschreibung, kosten, naechster_termin, faellig_bei_kilometerstand, status, fahrzeug_id)
                SELECT $3::text, $4::date, $5::text, 0, NULL, $6::integer, 'Geplant', fz.fahrzeug_id FROM fz
                RETURNING wartung_id
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
                SELECT CASE WHEN $7::bigint IS NULL THEN 'Entwicklungsansicht' ELSE 'Benutzer' END,
                       'Wartungstermin angelegt', 'Gespeichert',
                       $3::text || ' am ' || to_char($4::date, 'DD.MM.YYYY') || ' · Fahrzeug ' || fz.kennzeichen,
                       fz.unternehmen_id, fz.abteilung_id, $7::bigint
                FROM fz
            )
            SELECT wartung_id FROM neu
            """;
        var rows = await _client.Value.QueryAsync(query, new[]
        {
            Text(entwurf.FahrzeugId), Text(zugriff.UnternehmenId), entwurf.Wartungsart, Text(entwurf.Termin), bemerkung,
            entwurf.FaelligBeiKm is int km ? Text(km) : null, BenutzerId()
        }, cancellationToken);

        if (rows.Count != 1)
            throw new ArgumentException("Das Fahrzeug gibt es nicht mehr oder es ist inaktiv. Bitte laden Sie die Ansicht neu.");
    }

    /// <summary>
    /// Durchgeführte Wartung festhalten: Mit TerminId wird dieser offene Termin erledigt, ohne TerminId wird eine
    /// Wartung nachgetragen. Optional entsteht gleich der Folgetermin. Ist der eingetragene Kilometerstand höher
    /// als der des Fahrzeugs, wird er dort übernommen. Alles in einem Befehl: ganz oder gar nicht.
    /// </summary>
    public async Task DurchfuehrungEintragenAsync(WartungDurchfuehrung durchfuehrung, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(durchfuehrung);
        ArtPruefen(durchfuehrung.Wartungsart);
        DatumPruefen(durchfuehrung.Datum, "Das Datum der Durchführung");
        if (durchfuehrung.Datum > WartungsRegeln.Heute())
            throw new ArgumentException("Eine durchgeführte Wartung kann nicht in der Zukunft liegen.");
        KilometerPruefen(durchfuehrung.Kilometerstand, "Der Kilometerstand");
        if (durchfuehrung.Kosten < 0 || durchfuehrung.Kosten > KostenMaximal || decimal.Round(durchfuehrung.Kosten, 2) != durchfuehrung.Kosten)
            throw new ArgumentException("Die Kosten müssen zwischen 0 und 1.000.000 € liegen (höchstens zwei Nachkommastellen).");
        if (durchfuehrung.Folgetermin is DateOnly folge)
        {
            DatumPruefen(folge, "Der Folgetermin");
            if (folge <= durchfuehrung.Datum)
                throw new ArgumentException("Der Folgetermin muss nach dem Tag der Durchführung liegen.");
        }
        if (durchfuehrung.FolgeterminBeiKm is int folgeKm)
        {
            if (durchfuehrung.Folgetermin is null)
                throw new ArgumentException("Für einen Folgetermin nach Kilometerstand bitte auch ein Datum wählen.");
            KilometerPruefen(folgeKm, "Der Kilometerstand des Folgetermins");
            if (folgeKm <= durchfuehrung.Kilometerstand)
                throw new ArgumentException("Der Kilometerstand des Folgetermins muss über dem jetzigen Kilometerstand liegen.");
        }
        string bemerkung = BemerkungPruefen(durchfuehrung.Bemerkung);

        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);

        // $3 = Termin-Id oder NULL. Genau einer der beiden Wege („erledigt“ oder „nachgetragen“) liefert eine Zeile.
        // Ein fremder oder schon erledigter Termin findet „termin“ nicht, dann entsteht gar nichts.
        const string query = """
            WITH fz AS (
                SELECT fahrzeug_id, kennzeichen, unternehmen_id, abteilung_id FROM public.fahrzeug
                WHERE fahrzeug_id = $1::bigint AND unternehmen_id = $2::bigint
            ), termin AS (
                SELECT w.wartung_id FROM public.wartung w JOIN fz ON fz.fahrzeug_id = w.fahrzeug_id
                WHERE w.wartung_id = $3::bigint AND w.status = 'Geplant'
            ), erledigt AS (
                UPDATE public.wartung w
                SET status = 'Erledigt', datum = $5::date, kilometerstand_bei_durchfuehrung = $6::integer,
                    kosten = $7::numeric, beschreibung = $8::text, naechster_termin = $9::date
                FROM termin t
                WHERE w.wartung_id = t.wartung_id
                RETURNING w.wartung_id, w.wartungsart
            ), nachgetragen AS (
                INSERT INTO public.wartung
                    (wartungsart, datum, beschreibung, kosten, naechster_termin, faellig_bei_kilometerstand, status,
                     kilometerstand_bei_durchfuehrung, fahrzeug_id)
                SELECT $4::text, $5::date, $8::text, $7::numeric, $9::date, NULL, 'Erledigt', $6::integer, fz.fahrzeug_id
                FROM fz WHERE $3::bigint IS NULL
                RETURNING wartung_id, wartungsart
            ), ergebnis AS (
                SELECT wartung_id, wartungsart FROM erledigt
                UNION ALL
                SELECT wartung_id, wartungsart FROM nachgetragen
            ), folgetermin AS (
                INSERT INTO public.wartung
                    (wartungsart, datum, beschreibung, kosten, naechster_termin, faellig_bei_kilometerstand, status, fahrzeug_id)
                SELECT e.wartungsart, $9::date, '', 0, NULL, $10::integer, 'Geplant', fz.fahrzeug_id
                FROM ergebnis e, fz WHERE $9::date IS NOT NULL
                RETURNING wartung_id
            ), kilometer AS (
                UPDATE public.fahrzeug f SET kilometerstand = $6::integer
                FROM ergebnis e, fz
                WHERE f.fahrzeug_id = fz.fahrzeug_id AND f.kilometerstand < $6::integer
                RETURNING f.fahrzeug_id
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
                SELECT CASE WHEN $11::bigint IS NULL THEN 'Entwicklungsansicht' ELSE 'Benutzer' END,
                       'Wartung eingetragen', 'Gespeichert',
                       e.wartungsart || ' am ' || to_char($5::date, 'DD.MM.YYYY') || ' · Fahrzeug ' || fz.kennzeichen
                           || ' · ' || $6::integer || ' km',
                       fz.unternehmen_id, fz.abteilung_id, $11::bigint
                FROM ergebnis e, fz
            )
            SELECT wartung_id FROM ergebnis
            """;
        var rows = await _client.Value.QueryAsync(query, new[]
        {
            Text(durchfuehrung.FahrzeugId), Text(zugriff.UnternehmenId),
            durchfuehrung.TerminId is long terminId ? Text(terminId) : null,
            durchfuehrung.Wartungsart, Text(durchfuehrung.Datum), Text(durchfuehrung.Kilometerstand),
            durchfuehrung.Kosten.ToString(CultureInfo.InvariantCulture), bemerkung,
            durchfuehrung.Folgetermin is DateOnly termin ? Text(termin) : null,
            durchfuehrung.FolgeterminBeiKm is int km ? Text(km) : null,
            BenutzerId()
        }, cancellationToken);

        if (rows.Count != 1)
            throw new ArgumentException(durchfuehrung.TerminId is null
                ? "Das Fahrzeug gibt es nicht mehr. Bitte laden Sie die Ansicht neu."
                : "Der Termin wurde inzwischen erledigt oder entfernt. Bitte laden Sie die Ansicht neu.");
    }

    /// <summary>Einen falsch angelegten offenen Termin entfernen. Durchgeführte Wartungen bleiben immer erhalten.</summary>
    public async Task<bool> TerminEntfernenAsync(long wartungId, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        const string query = """
            WITH entfernt AS (
                DELETE FROM public.wartung w
                USING public.fahrzeug f
                WHERE w.wartung_id = $1::bigint AND w.status = 'Geplant'
                  AND f.fahrzeug_id = w.fahrzeug_id AND f.unternehmen_id = $2::bigint
                RETURNING w.wartung_id, w.wartungsart, w.datum, f.kennzeichen, f.unternehmen_id, f.abteilung_id
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
                SELECT CASE WHEN $3::bigint IS NULL THEN 'Entwicklungsansicht' ELSE 'Benutzer' END,
                       'Wartungstermin entfernt', 'Gespeichert',
                       e.wartungsart || ' am ' || to_char(e.datum, 'DD.MM.YYYY') || ' · Fahrzeug ' || e.kennzeichen,
                       e.unternehmen_id, e.abteilung_id, $3::bigint
                FROM entfernt e
            )
            SELECT wartung_id FROM entfernt
            """;
        var rows = await _client.Value.QueryAsync(query,
            new[] { Text(wartungId), Text(zugriff.UnternehmenId), BenutzerId() }, cancellationToken);
        return rows.Count == 1;
    }

    private async Task<IReadOnlyList<Wartungstermin>> TermineAsync(string?[] firma, CancellationToken cancellationToken) =>
        (await _client.Value.QueryAsync($"{TerminAbfrage} ORDER BY w.datum, f.kennzeichen, w.wartung_id", firma, cancellationToken))
            .Select(ReadTermin)
            .ToArray();

    // Alle Fahrzeuge der Firma, aktive zuerst. F05 bietet nur aktive an, der Historienfilter in F04 alle.
    private async Task<IReadOnlyList<WartungFahrzeug>> FahrzeugeAsync(string?[] firma, CancellationToken cancellationToken) =>
        (await _client.Value.QueryAsync("""
                SELECT fahrzeug_id, kennzeichen, kilometerstand, aktiv FROM public.fahrzeug
                WHERE unternehmen_id = $1::bigint ORDER BY NOT aktiv, kennzeichen, fahrzeug_id
                """, firma, cancellationToken))
            .Select(row => new WartungFahrzeug(Id(row, 0), Required(row, 1),
                int.Parse(Required(row, 2), CultureInfo.InvariantCulture), Required(row, 3) == "t"))
            .ToArray();

    private static Wartungstermin ReadTermin(string?[] row) => new(
        Id(row, 0), Id(row, 1), Required(row, 2), Required(row, 3), Datum(Required(row, 4)),
        row[5] is string km ? int.Parse(km, CultureInfo.InvariantCulture) : null,
        int.Parse(Required(row, 6), CultureInfo.InvariantCulture),
        row[7] ?? string.Empty);

    private static void ArtPruefen(string? wartungsart)
    {
        if (wartungsart is null || !WartungsRegeln.Wartungsarten.Contains(wartungsart, StringComparer.Ordinal))
            throw new ArgumentException("Bitte eine Wartungsart auswählen.");
    }

    private static void DatumPruefen(DateOnly datum, string feld)
    {
        if (datum.Year < 2000 || datum > WartungsRegeln.Heute().AddYears(10))
            throw new ArgumentException($"{feld} muss zwischen dem Jahr 2000 und zehn Jahren in der Zukunft liegen.");
    }

    private static void KilometerPruefen(int kilometer, string feld)
    {
        if (kilometer is < 0 or > KilometerMaximal)
            throw new ArgumentException($"{feld} muss zwischen 0 und 5.000.000 km liegen.");
    }

    // Die Spalte beschreibung ist Pflicht. Ohne Bemerkung steht dort ein leerer Text.
    private static string BemerkungPruefen(string? bemerkung)
    {
        string text = (bemerkung ?? string.Empty).Trim();
        if (text.Length > BemerkungMaximal)
            throw new ArgumentException($"Die Bemerkung darf höchstens {BemerkungMaximal} Zeichen lang sein.");
        return text;
    }

    private static DateOnly Datum(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? BenutzerId() => BenutzerSitzung.AktuellerBenutzer is { } benutzer ? Text(benutzer.Id) : null;

    private static long Id(string?[] row, int index) => long.Parse(Required(row, index), CultureInfo.InvariantCulture);

    private static string Text(long wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Text(int wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Text(DateOnly datum) => datum.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}
