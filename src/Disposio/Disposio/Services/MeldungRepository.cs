using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// D15 „Meldungen bearbeiten“: Tank- und Schadensmeldungen der Fahrer-App sichten und bearbeiten.
/// Wie überall: erst die Rolle Disponent prüfen, dann nur Meldungen zu Fahrzeugen dieser Firma.
/// Speichern ändert Meldung, Fahrzeugstatus und Aktionsprotokoll in einem Befehl: entweder alles oder nichts.
/// </summary>
public sealed class MeldungRepository
{
    private const FirmenRolle Rolle = FirmenRolle.Disponent;

    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    // Alle offenen Meldungen bis zum Datum und erledigte der letzten 30 Tage davor. $1 Firma, $2 Datum.
    // Offen wie in D01: Status enthält nicht erledigt / abgeschlossen / geschlossen / repariert.
    private const string MeldungenQuery = """
        SELECT x.art, x.id, x.titel, x.hinweis, x.status, x.notiz, x.bearbeiter, x.kosten, x.liter,
               to_char(x.datum, 'YYYY-MM-DD'), fz.fahrzeug_id, fz.kennzeichen, fz.status,
               NULLIF(btrim(concat_ws(' ', b.vorname, b.nachname)), ''),
               d.dateireferenz, d.mime_typ, d.dateiname, COALESCE(d.anzahl, 0)
        FROM (
            SELECT 'Schaden' AS art, s.schaden_id AS id, s.schadensart AS titel, s.beschreibung AS hinweis, s.status,
                   s.bearbeitungsnotiz AS notiz, s.bearbeiter_benutzer_id AS bearbeiter, s.kosten, NULL::numeric AS liter,
                   s.datum, s.fahrzeug_id, s.fahrer_id
            FROM public.schadensmeldung s
            UNION ALL
            SELECT 'Tankvorgang', t.tankvorgang_id, t.kraftstoff,
                   concat_ws(' · ', NULLIF(btrim(t.tankstelle), ''), 'km ' || t.kilometerstand::text),
                   t.bearbeitungsstatus, t.bearbeitungsnotiz, t.bearbeiter_benutzer_id, t.kosten, t.liter,
                   t.datum, t.fahrzeug_id, t.fahrer_id
            FROM public.tankvorgang t
        ) x
        JOIN public.fahrzeug fz ON fz.fahrzeug_id = x.fahrzeug_id AND fz.unternehmen_id = $1::bigint
        LEFT JOIN public.fahrer fa ON fa.fahrer_id = x.fahrer_id
        LEFT JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id AND b.unternehmen_id = $1::bigint
        LEFT JOIN LATERAL (
            SELECT dk.dateireferenz, dk.mime_typ, dk.dateiname, count(*) OVER () AS anzahl
            FROM public.dokument dk
            WHERE (x.art = 'Schaden' AND dk.schaden_id = x.id) OR (x.art = 'Tankvorgang' AND dk.tankvorgang_id = x.id)
            ORDER BY dk.hochgeladen_am DESC, dk.dokument_id DESC
            LIMIT 1
        ) d ON true
        WHERE x.datum <= $2::date
          AND (x.datum >= $2::date - 30 OR lower(x.status) NOT SIMILAR TO '%(erledigt|abgeschlossen|geschlossen|repariert)%')
        ORDER BY x.datum DESC, x.art, x.id DESC
        LIMIT 300
        """;

    // Wer zuständig sein kann: aktive Büro-Konten der Firma (Disponent, Abteilungsleiter, Fuhrparkmanager).
    private const string BearbeiterQuery = """
        SELECT b.benutzer_id, COALESCE(NULLIF(btrim(concat_ws(' ', b.vorname, b.nachname)), ''), 'Konto ' || b.benutzer_id),
               string_agg(DISTINCT r.bezeichnung, ', ' ORDER BY r.bezeichnung)
        FROM public.benutzer b
        JOIN public.benutzer_rolle br ON br.benutzer_id = b.benutzer_id
        JOIN public.rolle r ON r.rollen_id = br.rollen_id AND (r.unternehmen_id = b.unternehmen_id OR r.unternehmen_id IS NULL)
        WHERE b.unternehmen_id = $1::bigint AND b.aktiv = true
          AND r.bezeichnung IN ('Disponent', 'Abteilungsleiter', 'Fuhrparkmanager')
        GROUP BY b.benutzer_id, b.vorname, b.nachname
        ORDER BY lower(b.nachname), lower(b.vorname)
        """;

    // $1 Firma, $2 Art, $3 Id, $4 Status, $5 Notiz, $6 Zuständig, $7 Kosten, $8 Liter (nur Tank), $9 Fahrzeugstatus,
    // $10 angemeldetes Konto, $11 Abteilung. Nur Meldungen zu Fahrzeugen der Firma, nur Zuständige der Firma.
    private const string SpeichernQuery = """
        WITH bearbeiter AS (
            SELECT b.benutzer_id FROM public.benutzer b
            WHERE b.benutzer_id = $6::bigint AND b.unternehmen_id = $1::bigint AND b.aktiv = true
        ), s AS (
            UPDATE public.schadensmeldung m
            SET status = $4, bearbeitungsnotiz = $5, bearbeiter_benutzer_id = $6::bigint, kosten = $7::numeric
            FROM public.fahrzeug fz, bearbeiter
            WHERE $2 = 'Schaden' AND m.schaden_id = $3::bigint
              AND fz.fahrzeug_id = m.fahrzeug_id AND fz.unternehmen_id = $1::bigint
            RETURNING m.fahrzeug_id, 'Schaden ' || m.schadensart AS titel
        ), t AS (
            UPDATE public.tankvorgang m
            SET bearbeitungsstatus = $4, bearbeitungsnotiz = $5, bearbeiter_benutzer_id = $6::bigint, kosten = $7::numeric,
                liter = COALESCE($8::numeric, m.liter)
            FROM public.fahrzeug fz, bearbeiter
            WHERE $2 = 'Tankvorgang' AND m.tankvorgang_id = $3::bigint
              AND fz.fahrzeug_id = m.fahrzeug_id AND fz.unternehmen_id = $1::bigint
            RETURNING m.fahrzeug_id, 'Tankvorgang ' || m.kraftstoff AS titel
        ), m AS (
            SELECT * FROM s UNION ALL SELECT * FROM t
        ), f AS (
            UPDATE public.fahrzeug fz SET status = $9
            FROM m
            WHERE fz.fahrzeug_id = m.fahrzeug_id AND fz.unternehmen_id = $1::bigint
            RETURNING fz.kennzeichen
        ), p AS (
            INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
            SELECT 'Benutzer', 'Meldung bearbeitet', $4, m.titel || ' · ' || f.kennzeichen || ' · Fahrzeug ' || $9,
                   $1::bigint, $11::bigint, $10::bigint
            FROM m, f
        )
        SELECT count(*) FROM m
        """;

    public async Task<MeldungenDaten> LadenAsync(DateOnly bis, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        string firma = Text(zugriff.UnternehmenId);

        var meldungenTask = _client.Value.QueryAsync(MeldungenQuery,
            new string?[] { firma, bis.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, cancellationToken);
        var bearbeiterTask = _client.Value.QueryAsync(BearbeiterQuery, new string?[] { firma }, cancellationToken);
        await Task.WhenAll(meldungenTask, bearbeiterTask);

        var meldungen = (await meldungenTask).Select(row => new Meldung(
                Required(row, 0),
                Id(row, 1),
                row[2] ?? "",
                row[3] ?? "",
                row[4] ?? "",
                row[5],
                row[6] is string bearbeiter ? long.Parse(bearbeiter, CultureInfo.InvariantCulture) : null,
                Zahl(row[7]) ?? 0,
                Zahl(row[8]),
                DateOnly.ParseExact(Required(row, 9), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Id(row, 10),
                row[11] ?? "",
                row[12] ?? "",
                row[13],
                row[14],
                row[15],
                row[16],
                int.Parse(Required(row, 17), CultureInfo.InvariantCulture)))
            .ToArray();

        var bearbeiter = (await bearbeiterTask)
            .Select(row => new MeldungBearbeiter(Id(row, 0), Required(row, 1), row[2] ?? ""))
            .ToArray();

        return new MeldungenDaten(meldungen, bearbeiter);
    }

    /// <exception cref="ArgumentException">Ungültige Eingaben, oder Meldung bzw. Zuständiger gehören nicht (mehr) zur Firma.</exception>
    public async Task SpeichernAsync(MeldungBearbeitung e, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(e);
        // Noch einmal unabhängig von der Oberfläche prüfen.
        if (e.Art is not (MeldungWerte.ArtSchaden or MeldungWerte.ArtTank))
            throw new ArgumentException("Unbekannte Art der Meldung.");
        string status = Pflichttext(e.Status, "Bearbeitungsstatus", 50);
        string notiz = Pflichttext(e.Notiz, "Rückfrage / Bearbeitungsnotiz", 2000);
        string fahrzeugStatus = Pflichttext(e.FahrzeugStatus, "Verfügbarkeit nach Prüfung", 50);
        if (e.Kosten is < 0 or > 1_000_000)
            throw new ArgumentException("Der Betrag muss zwischen 0 und 1.000.000 € liegen.");
        if (e.Art == MeldungWerte.ArtTank && e.Liter is not (> 0 and <= 5000))
            throw new ArgumentException("Die Menge muss zwischen 0 und 5.000 Litern liegen.");

        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var parameter = new string?[]
        {
            Text(zugriff.UnternehmenId),                                                  // $1
            e.Art,                                                                        // $2
            Text(e.Id),                                                                   // $3
            status,                                                                       // $4
            notiz,                                                                        // $5
            Text(e.BearbeiterId),                                                         // $6
            e.Kosten.ToString(CultureInfo.InvariantCulture),                              // $7
            e.Art == MeldungWerte.ArtTank ? e.Liter?.ToString(CultureInfo.InvariantCulture) : null, // $8
            fahrzeugStatus,                                                               // $9
            BenutzerSitzung.AktuellerBenutzer is { } benutzer ? Text(benutzer.Id) : null, // $10
            zugriff.AbteilungId is long abteilung ? Text(abteilung) : null                // $11
        };

        try
        {
            var rows = await _client.Value.QueryAsync(SpeichernQuery, parameter, cancellationToken);
            if (rows.Count != 1 || Required(rows[0], 0) != "1")
                throw new ArgumentException("Die Meldung konnte nicht gespeichert werden: Sie oder die zuständige Person " +
                                            "gibt es in Ihrem Unternehmen nicht (mehr). Bitte laden Sie die Liste neu.");
        }
        catch (NeonSqlException exception) when (exception.Code == "23514")
        {
            // Eine CHECK-Regel der Datenbank (z. B. erlaubte Statuswerte der Fahrer-App) lehnt den Wert ab.
            throw new ArgumentException("Die Datenbank lehnt einen der gewählten Werte ab (Bearbeitungsstatus oder " +
                                        "Fahrzeugstatus). Bitte einen anderen Wert wählen.", exception);
        }
    }

    private static string Pflichttext(string? text, string feld, int maximaleLaenge)
    {
        string wert = (text ?? string.Empty).Trim();
        if (wert.Length == 0 || wert.Length > maximaleLaenge)
            throw new ArgumentException($"{feld} muss ausgefüllt sein und darf höchstens {maximaleLaenge} Zeichen enthalten.");
        return wert;
    }

    // Neon liefert Zahlen mit Punkt als Dezimaltrenner. Deshalb InvariantCulture.
    private static decimal? Zahl(string? text) =>
        text is null ? null : decimal.Parse(text, CultureInfo.InvariantCulture);

    private static long Id(string?[] row, int index) => long.Parse(Required(row, index), CultureInfo.InvariantCulture);

    private static string Text(long wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}
