using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// D11 „Frachtdokumente &amp; Abliefernachweise“: Dokumente (public.dokument) und Abliefernachweise der Fahrer-App
/// je Sendung anzeigen und neue Dokumente zuordnen. Dateien liegen bei Cloudinary (<see cref="CloudinarySpeicher"/>),
/// in der Datenbank steht nur die Adresse. Wie überall: erst Rolle Disponent prüfen, dann nur diese Firma.
/// </summary>
public sealed class FrachtdokumentRepository
{
    private const FirmenRolle Rolle = FirmenRolle.Disponent;
    public const long MaxBytes = 10 * 1024 * 1024; // wie in der Fahrer-App (DokumentEndpoints.MaxBytes)

    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    // Sendungen eines Tages – oder mit Suchbegriff: über alle Tage (Referenz, Auftragsnummer, Kunde, Dateiname).
    // Oder alle Sendungen einer Tour (aus D07 „Frachtdokumente“). $1 Firma, $2 Tag, $3 Suchmuster für ILIKE
    // (leer = keine Suche), $4 Tour-Id (leer = keine Tour).
    private const string SendungenQuery = """
        SELECT a.auftrag_id, a.auftragsnummer, a.kundenreferenz, COALESCE(k.firmenname, ''), a.anzahl, a.verpackungstyp,
               a.gewicht_kg, to_char(a.lieferdatum, 'YYYY-MM-DD'), a.lieferadresse,
               (SELECT count(*) FROM public.dokument d
                WHERE d.auftrag_id = a.auftrag_id
                   OR d.nachweis_id IN (SELECT n.nachweis_id FROM public.abliefernachweis n WHERE n.auftrag_id = a.auftrag_id)),
               EXISTS (SELECT 1 FROM public.abliefernachweis n WHERE n.auftrag_id = a.auftrag_id),
               EXISTS (SELECT 1 FROM public.abliefernachweis n WHERE n.auftrag_id = a.auftrag_id
                         AND (NULLIF(btrim(n.ausnahmegrund), '') IS NOT NULL OR n.tatsaechlich_abgelieferte_anzahl < a.anzahl))
        FROM public.auftrag a
        LEFT JOIN public.kunde k ON k.kunde_id = a.kunde_id AND k.unternehmen_id = a.unternehmen_id
        WHERE a.unternehmen_id = $1::bigint
          AND (($4::bigint IS NOT NULL AND a.tour_id = $4::bigint)
               OR ($4::bigint IS NULL AND $3 = '' AND a.abholdatum <= $2::date AND a.lieferdatum >= $2::date)
               OR ($4::bigint IS NULL AND $3 <> '' AND (a.kundenreferenz ILIKE $3 OR a.auftragsnummer ILIKE $3
                    OR k.firmenname ILIKE $3
                    OR EXISTS (SELECT 1 FROM public.dokument d WHERE d.auftrag_id = a.auftrag_id AND d.dateiname ILIKE $3))))
        ORDER BY a.lieferdatum DESC, a.auftragsnummer
        LIMIT 100
        """;

    // Dokumente einer Sendung: direkt zugeordnet (D11) oder über einen Abliefernachweis (Fahrer-App).
    // Bemerkung über to_jsonb, damit die Abfrage auch vor Migration 014 funktioniert.
    private const string DokumenteQuery = """
        SELECT d.dokument_id, d.dokumentart, d.dateiname, d.dateireferenz, d.mime_typ, d.dateigroesse_bytes,
               to_char(d.hochgeladen_am AT TIME ZONE 'Europe/Berlin', 'YYYY-MM-DD HH24:MI'),
               COALESCE(b.vorname || ' ' || b.nachname, ''),
               EXISTS (SELECT 1 FROM public.fahrer f WHERE f.benutzer_id = d.benutzer_id),
               to_jsonb(d) ->> 'bemerkung'
        FROM public.auftrag a
        JOIN public.dokument d ON d.auftrag_id = a.auftrag_id
             OR d.nachweis_id IN (SELECT n.nachweis_id FROM public.abliefernachweis n WHERE n.auftrag_id = a.auftrag_id)
        LEFT JOIN public.benutzer b ON b.benutzer_id = d.benutzer_id
        WHERE a.auftrag_id = $2::bigint AND a.unternehmen_id = $1::bigint
        ORDER BY d.hochgeladen_am, d.dokument_id
        """;

    private const string NachweiseQuery = """
        SELECT n.nachweis_id, to_char(n.zeitpunkt AT TIME ZONE 'Europe/Berlin', 'YYYY-MM-DD HH24:MI'),
               COALESCE(b.vorname || ' ' || b.nachname, ''), n.empfaengername, n.bestaetigungsart,
               n.tatsaechlich_abgelieferte_anzahl, a.anzahl, n.ausnahmegrund, n.bemerkung
        FROM public.abliefernachweis n
        JOIN public.auftrag a ON a.auftrag_id = n.auftrag_id
        LEFT JOIN public.fahrer f ON f.fahrer_id = n.fahrer_id
        LEFT JOIN public.benutzer b ON b.benutzer_id = f.benutzer_id
        WHERE a.auftrag_id = $2::bigint AND a.unternehmen_id = $1::bigint
        ORDER BY n.zeitpunkt
        """;

    // Sendungsreferenz → Sendung: Auftragsnummer oder Kundenreferenz, genau einmal in dieser Firma.
    private const string ReferenzQuery = """
        SELECT auftrag_id, auftragsnummer FROM public.auftrag
        WHERE unternehmen_id = $1::bigint
          AND (lower(auftragsnummer) = lower($2) OR lower(btrim(kundenreferenz)) = lower($2))
        LIMIT 2
        """;

    // Dokument + Protokoll in einem Befehl. $1 Firma, $2 Sendung, $3 Konto, $4 Art, $5 Dateiname, $6 Adresse,
    // $7 MIME-Typ, $8 Bytes, $9 Bemerkung (leer = keine). Braucht Migration 014 (Spalte bemerkung).
    private const string ZuordnenQuery = """
        WITH a AS (
            SELECT auftrag_id, auftragsnummer, tour_id FROM public.auftrag
            WHERE auftrag_id = $2::bigint AND unternehmen_id = $1::bigint
        ), d AS (
            INSERT INTO public.dokument (dokumentart, dateiname, dateireferenz, mime_typ, dateigroesse_bytes,
                                        benutzer_id, auftrag_id, bemerkung)
            SELECT $4, $5, $6, $7, $8::bigint, $3::bigint, a.auftrag_id, NULLIF($9, '') FROM a
            RETURNING dokument_id
        ), protokoll AS (
            INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, benutzer_id, tour_id)
            SELECT 'Benutzer', 'Dokument zugeordnet', 'Gespeichert',
                   $4 || ' „' || $5 || '“ zu Sendung ' || a.auftragsnummer, $1::bigint, $3::bigint, a.tour_id
            FROM a, d
        )
        SELECT dokument_id FROM d
        """;

    /// <param name="suche">Leer = alle Sendungen des Tages; sonst Suche über alle Tage.</param>
    /// <param name="tourId">Gesetzt = alle Sendungen dieser Tour (Tag und Suche zählen dann nicht).</param>
    public async Task<IReadOnlyList<DokumentSendung>> SendungenLadenAsync(DateOnly tag, string suche, long? tourId = null,
        CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        string muster = suche.Trim().Length == 0 ? "" : "%" + Escape(suche.Trim()) + "%";
        var rows = await _client.Value.QueryAsync(SendungenQuery, new string?[]
        {
            Text(zugriff.UnternehmenId), tag.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), muster,
            tourId is long t ? Text(t) : null
        }, cancellationToken);

        return rows.Select(r => new DokumentSendung(
            Id(r, 0), Req(r, 1), r[2], Req(r, 3), Zahl(r, 4), Req(r, 5),
            decimal.Parse(Req(r, 6), CultureInfo.InvariantCulture),
            DateOnly.ParseExact(Req(r, 7), "yyyy-MM-dd", CultureInfo.InvariantCulture), Req(r, 8),
            Zahl(r, 9), r[10] == "t", r[11] == "t")).ToList();
    }

    public async Task<SendungDokumente> DokumenteLadenAsync(long auftragId, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var parameter = new string?[] { Text(zugriff.UnternehmenId), Text(auftragId) };
        var dokTask = _client.Value.QueryAsync(DokumenteQuery, parameter, cancellationToken);
        var nwTask = _client.Value.QueryAsync(NachweiseQuery, parameter, cancellationToken);
        await Task.WhenAll(dokTask, nwTask);

        var dokumente = (await dokTask).Select(r => new Frachtdokument(
            Id(r, 0), Req(r, 1), Req(r, 2), Req(r, 3), Req(r, 4), long.Parse(Req(r, 5), CultureInfo.InvariantCulture),
            Zeit(Req(r, 6)), Req(r, 7), r[8] == "t", r[9])).ToList();
        var nachweise = (await nwTask).Select(r => new Abliefernachweis(
            Id(r, 0), Zeit(Req(r, 1)), Req(r, 2), r[3], Req(r, 4), Zahl(r, 5), Zahl(r, 6), r[7], r[8])).ToList();
        return new SendungDokumente(dokumente, nachweise);
    }

    /// <summary>Prüft, lädt die Datei zu Cloudinary hoch und ordnet sie der Sendung zu.</summary>
    /// <returns>Die Id der Sendung (für die Anzeige danach).</returns>
    /// <exception cref="ArgumentException">Eingaben ungültig oder Sendung nicht eindeutig.</exception>
    /// <exception cref="InvalidOperationException">Dokumentenspeicher nicht eingerichtet oder Upload fehlgeschlagen.</exception>
    public async Task<long> ZuordnenAsync(string referenz, string art, string dateiname, byte[] inhalt, string? bemerkung,
        CancellationToken cancellationToken = default)
    {
        string refText = (referenz ?? "").Trim();
        if (refText.Length == 0)
            throw new ArgumentException("Bitte die Sendungsreferenz eingeben.");
        if (!DokumentStatus.Arten.Contains(art))
            throw new ArgumentException("Bitte eine Dokumentart wählen.");
        if (inhalt.Length == 0)
            throw new ArgumentException("Bitte eine Datei auswählen.");
        if (inhalt.Length > MaxBytes)
            throw new ArgumentException("Die Datei ist größer als 10 MB.");
        string bem = (bemerkung ?? "").Trim();
        if (bem.Length > 500)
            throw new ArgumentException("Die Bemerkung darf höchstens 500 Zeichen haben.");

        // Nur echte PDF-, JPEG- oder PNG-Dateien, egal welche Endung sie haben.
        string mime = inhalt switch
        {
            [0x25, 0x50, 0x44, 0x46, ..] => "application/pdf",
            [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
            [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
            _ => throw new ArgumentException("Nur PDF-, JPG- oder PNG-Dateien sind erlaubt.")
        };

        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var benutzer = BenutzerSitzung.AktuellerBenutzer
            ?? throw new UnauthorizedAccessException("Bitte melden Sie sich erneut an.");
        string firma = Text(zugriff.UnternehmenId);

        var treffer = await _client.Value.QueryAsync(ReferenzQuery, new string?[] { firma, refText }, cancellationToken);
        if (treffer.Count == 0)
            throw new ArgumentException($"Keine Sendung mit der Referenz „{refText}“ gefunden.");
        if (treffer.Count > 1)
            throw new ArgumentException($"Die Referenz „{refText}“ passt zu mehreren Sendungen. Bitte die Auftragsnummer (A-…) verwenden.");
        long auftragId = Id(treffer[0], 0);

        // Erst hochladen, dann speichern. Scheitert das Speichern, bleibt höchstens eine unbenutzte Datei bei Cloudinary.
        string ordner = $"disposio/{zugriff.UnternehmenId.ToString(CultureInfo.InvariantCulture)}/{Ordnername(art)}";
        string adresse = await CloudinarySpeicher.HochladenAsync(inhalt, mime, ordner, Guid.NewGuid().ToString(), cancellationToken);

        string name = System.IO.Path.GetFileName(dateiname);
        if (name.Length > 200)
            name = name[..200];
        var rows = await _client.Value.QueryAsync(ZuordnenQuery, new string?[]
        {
            firma, Text(auftragId), Text(benutzer.Id), art, name, adresse, mime,
            inhalt.LongLength.ToString(CultureInfo.InvariantCulture), bem
        }, cancellationToken);
        if (rows.Count != 1)
            throw new ArgumentException("Die Sendung gibt es nicht mehr.");
        return auftragId;
    }

    // „Frachtbrief (CMR)“ -> „frachtbrief-cmr“ (Ordner bei Cloudinary, wie „lieferschein“ in der Fahrer-App)
    private static string Ordnername(string art) => new string(art.ToLowerInvariant()
        .Select(z => char.IsLetterOrDigit(z) ? z : '-').ToArray()).Trim('-').Replace("--", "-");

    // % und _ im Suchbegriff wörtlich nehmen
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static DateTime Zeit(string text) => DateTime.ParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static int Zahl(string?[] row, int index) => int.Parse(Req(row, index), CultureInfo.InvariantCulture);

    private static long Id(string?[] row, int index) => long.Parse(Req(row, index), CultureInfo.InvariantCulture);

    private static string Text(long wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Req(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}
