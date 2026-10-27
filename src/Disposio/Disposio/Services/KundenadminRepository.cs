using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Datenzugriff für die Ansichten des Kundenadmins (K01–K04). Jede Methode arbeitet nur mit
/// dem eigenen Unternehmen. Welches das ist, entscheidet die Sitzung, nie die Oberfläche.
/// Voraussetzung: 003_eigene_rollen.sql wurde in Neon ausgeführt.
/// </summary>
public sealed class KundenadminRepository
{
    // Wie in den anderen Repositories erst beim ersten Zugriff verbinden.
    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    /// <summary>Standardrollen, die ein Kundenadmin vergeben darf. Die Plattformrolle gehört nie dazu.</summary>
    public static IReadOnlyList<string> VergebbareRollen { get; } =
        FirmenRollenKatalog.Alle.Select(FirmenRollenKatalog.DatenbankName).ToArray();

    // ── K01 / K02: Benutzer ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FirmenBenutzer>> BenutzerLadenAsync(CancellationToken cancellationToken = default)
    {
        long firmaId = await ZugriffPruefenAsync(cancellationToken);

        // Abteilung nur, wenn sie zur selben Firma gehört (wie beim Login im AuthService).
        const string query = """
            SELECT b.benutzer_id, b.vorname, b.nachname, b.email, b.aktiv, b.sprache, b.abteilung_id,
                   a.bezeichnung,
                   COALESCE((SELECT json_agg(r.bezeichnung ORDER BY r.bezeichnung, r.rollen_id)
                             FROM public.benutzer_rolle br
                             JOIN public.rolle r ON r.rollen_id = br.rollen_id
                             WHERE br.benutzer_id = b.benutzer_id), '[]'::json),
                   COALESCE((SELECT json_agg(r.rollen_id ORDER BY r.bezeichnung, r.rollen_id)
                             FROM public.benutzer_rolle br
                             JOIN public.rolle r ON r.rollen_id = br.rollen_id
                             WHERE br.benutzer_id = b.benutzer_id), '[]'::json),
                   b.erstellungsdatum
            FROM public.benutzer b
            LEFT JOIN public.abteilung a ON a.abteilung_id = b.abteilung_id AND a.unternehmen_id = b.unternehmen_id
            WHERE b.unternehmen_id = $1::bigint
            ORDER BY lower(b.nachname), lower(b.vorname), b.benutzer_id
            """;
        var rows = await _client.Value.QueryAsync(query, new string?[] { Text(firmaId) }, cancellationToken);
        return rows.Select(row => new FirmenBenutzer(
            long.Parse(Required(row, 0), CultureInfo.InvariantCulture),
            Required(row, 1), Required(row, 2), Required(row, 3),
            // Neon liefert Wahrheitswerte als Text: t oder f.
            Required(row, 4) == "t",
            row[5] ?? "",
            row[6] is null ? null : long.Parse(row[6]!, CultureInfo.InvariantCulture),
            row[7],
            Array.AsReadOnly(JsonSerializer.Deserialize<string[]>(Required(row, 8)) ?? Array.Empty<string>()),
            Array.AsReadOnly(JsonSerializer.Deserialize<long[]>(Required(row, 9)) ?? Array.Empty<long>()),
            DateTimeOffset.Parse(Required(row, 10), CultureInfo.InvariantCulture).LocalDateTime)).ToArray();
    }

    public async Task<IReadOnlyList<AbteilungAuswahl>> AbteilungenLadenAsync(CancellationToken cancellationToken = default)
    {
        long firmaId = await ZugriffPruefenAsync(cancellationToken);
        var rows = await _client.Value.QueryAsync(
            """
            SELECT abteilung_id, bezeichnung FROM public.abteilung
            WHERE unternehmen_id = $1::bigint ORDER BY lower(bezeichnung)
            """,
            new string?[] { Text(firmaId) }, cancellationToken);
        return rows.Select(row => new AbteilungAuswahl(
            long.Parse(Required(row, 0), CultureInfo.InvariantCulture), Required(row, 1))).ToArray();
    }

    /// <summary>Rollen für die Auswahlliste in K02: erst die Standardrollen, dann die eigenen Rollen der Firma.</summary>
    public async Task<IReadOnlyList<RollenAuswahl>> RollenLadenAsync(CancellationToken cancellationToken = default)
    {
        long firmaId = await ZugriffPruefenAsync(cancellationToken);
        var rows = await _client.Value.QueryAsync(
            """
            SELECT rollen_id, bezeichnung, unternehmen_id IS NOT NULL FROM public.rolle
            WHERE (unternehmen_id IS NULL AND bezeichnung = ANY($2::text[])) OR unternehmen_id = $1::bigint
            """,
            new string?[] { Text(firmaId), TextArray(VergebbareRollen) }, cancellationToken);

        var rollen = rows.Select(row => new RollenAuswahl(
            long.Parse(Required(row, 0), CultureInfo.InvariantCulture), Required(row, 1), Required(row, 2) == "t")).ToList();
        // Standardrollen in der Reihenfolge des Rollenkatalogs, eigene Rollen alphabetisch.
        return rollen.Where(rolle => !rolle.IstEigene)
            .OrderBy(rolle => VergebbareRollen.ToList().IndexOf(rolle.Bezeichnung))
            .Concat(rollen.Where(rolle => rolle.IstEigene).OrderBy(rolle => rolle.Bezeichnung, StringComparer.CurrentCultureIgnoreCase))
            .ToArray();
    }

    /// <summary>Legt ein Konto im eigenen Unternehmen an: Benutzer, Rolle und Protokoll in einem Befehl.</summary>
    public async Task<long> BenutzerAnlegenAsync(FirmenBenutzerEntwurf entwurf, CancellationToken cancellationToken = default)
    {
        long firmaId = await ZugriffPruefenAsync(cancellationToken);
        var eingaben = ValidateAndTrim(entwurf, neu: true);
        await RolleUndAbteilungPruefenAsync(firmaId, null, eingaben, cancellationToken);

        // Das Startpasswort muss die Passwort-Richtlinie der Firma (K04) erfüllen.
        PasswortRichtlinie richtlinie = await RichtlinieLesenAsync(firmaId, cancellationToken);
        if (richtlinie.PasswortFehler(eingaben.Passwort!) is string passwortFehler)
            throw new ArgumentException(passwortFehler);

        // Ist die E-Mail in dieser Firma schon vergeben? Dann legen wir gar kein Neon-Auth-Konto an.
        if (await EmailVergebenAsync(firmaId, null, eingaben.Email, cancellationToken))
            throw new BenutzerEmailVergebenException(null);

        // Das Passwort wird bei Neon Auth gespeichert. Der BCrypt-Hash bleibt vorerst nur, weil die
        // Spalte passwort_hash noch gefüllt sein darf. Sobald die Spalte gelöscht wird, entfällt er.
        string hash = await Task.Run(() => BCrypt.Net.BCrypt.HashPassword(eingaben.Passwort, workFactor: 12),
            cancellationToken);

        // Gleicher Aufbau wie im BenutzerRepository: Entweder ist alles gespeichert oder nichts.
        // firmenrolle: nur eine vergebbare Standardrolle oder eine eigene Rolle DIESER Firma.
        // abteilung_ok: nur eine Abteilung der eigenen Firma.
        const string query = """
            WITH firmenrolle AS (
                SELECT rollen_id, bezeichnung FROM public.rolle
                WHERE rollen_id = $9::bigint
                  AND ((unternehmen_id IS NULL AND bezeichnung = ANY($12::text[])) OR unternehmen_id = $5::bigint)
            ), abteilung_ok AS (
                SELECT 1 AS ok WHERE $6::bigint IS NULL OR EXISTS (
                    SELECT 1 FROM public.abteilung a
                    WHERE a.abteilung_id = $6::bigint AND a.unternehmen_id = $5::bigint)
            ), neuer_benutzer AS (
                INSERT INTO public.benutzer
                    (vorname, nachname, email, passwort_hash, unternehmen_id, abteilung_id, sprache, aktiv, auth_user_id)
                SELECT $1, $2, $3, $4, $5::bigint, $6::bigint, $7, $8::boolean, $13::text
                FROM firmenrolle CROSS JOIN abteilung_ok
                WHERE NOT EXISTS (SELECT 1 FROM public.benutzer b
                                  WHERE b.unternehmen_id = $5::bigint AND lower(btrim(b.email)) = lower($3))
                RETURNING benutzer_id
            ), neue_zuordnung AS (
                INSERT INTO public.benutzer_rolle (benutzer_id, rollen_id)
                SELECT b.benutzer_id, r.rollen_id FROM neuer_benutzer b CROSS JOIN firmenrolle r
                RETURNING benutzer_id
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, benutzer_id)
                SELECT $10, 'Benutzer angelegt', 'Gespeichert', $3::text || ' · Rolle ' || r.bezeichnung,
                       $5::bigint, $11::bigint
                FROM neue_zuordnung CROSS JOIN firmenrolle r
            )
            SELECT benutzer_id FROM neue_zuordnung
            """;
        // Neon-Auth-Konto über die Neon Function anlegen. Die Funktion prüft selbst, ob die angemeldete
        // Person Kundenadmin ist. Danach gehört die Id des Kontos in die Spalte auth_user_id.
        string authId = await BenutzerKontoDienst.KontoAnlegenAsync(eingaben.Email,
            $"{eingaben.Vorname} {eingaben.Nachname}", eingaben.Passwort!, cancellationToken);

        var (akteurArt, akteurId) = Akteur();
        var parameters = new string?[]
        {
            eingaben.Vorname, eingaben.Nachname, eingaben.Email, hash, Text(firmaId),
            eingaben.AbteilungId is long abteilung ? Text(abteilung) : null,
            eingaben.Sprache, eingaben.Aktiv ? "true" : "false", Text(eingaben.RollenId),
            akteurArt, akteurId, TextArray(VergebbareRollen), authId
        };

        IReadOnlyList<string?[]> rows;
        try
        {
            rows = await SchreibenAsync(query, parameters, cancellationToken);
        }
        catch
        {
            // Speichern gescheitert: das eben angelegte Neon-Auth-Konto wieder aufräumen.
            await BenutzerKontoDienst.KontoEntfernenAsync(authId);
            throw;
        }

        if (rows.Count == 1)
            return long.Parse(Required(rows[0], 0), CultureInfo.InvariantCulture);

        await BenutzerKontoDienst.KontoEntfernenAsync(authId);
        if (await EmailVergebenAsync(firmaId, null, eingaben.Email, cancellationToken))
            throw new BenutzerEmailVergebenException(null);
        throw new InvalidOperationException("Die Datenbank hat das neue Konto nicht bestätigt. Bitte die Seite neu laden.");
    }

    /// <summary>
    /// Ändert Kontodaten, Rolle, Abteilung, Sprache und Status. Die Rolle wird ersetzt:
    /// Danach hat das Konto genau die gewählte Rolle, wie im Wireframe vorgesehen.
    /// </summary>
    public async Task BenutzerAendernAsync(long benutzerId, FirmenBenutzerEntwurf entwurf,
        CancellationToken cancellationToken = default)
    {
        long firmaId = await ZugriffPruefenAsync(cancellationToken);
        var eingaben = ValidateAndTrim(entwurf, neu: false);
        await RolleUndAbteilungPruefenAsync(firmaId, benutzerId, eingaben, cancellationToken);

        // ziel: Nur ein Konto der eigenen Firma kann geändert werden, auch wenn jemand eine fremde Id schickt.
        const string query = """
            WITH ziel AS (
                SELECT benutzer_id FROM public.benutzer
                WHERE benutzer_id = $1::bigint AND unternehmen_id = $2::bigint
            ), firmenrolle AS (
                SELECT rollen_id, bezeichnung FROM public.rolle
                WHERE rollen_id = $9::bigint
                  AND ((unternehmen_id IS NULL AND bezeichnung = ANY($12::text[])) OR unternehmen_id = $2::bigint)
            ), abteilung_ok AS (
                SELECT 1 AS ok WHERE $6::bigint IS NULL OR EXISTS (
                    SELECT 1 FROM public.abteilung a
                    WHERE a.abteilung_id = $6::bigint AND a.unternehmen_id = $2::bigint)
            ), geaendert AS (
                UPDATE public.benutzer b
                SET vorname = $3, nachname = $4, email = $5, abteilung_id = $6::bigint,
                    sprache = $7, aktiv = $8::boolean
                FROM ziel z, firmenrolle, abteilung_ok
                WHERE b.benutzer_id = z.benutzer_id
                  AND NOT EXISTS (SELECT 1 FROM public.benutzer x
                                  WHERE x.unternehmen_id = $2::bigint AND x.benutzer_id <> $1::bigint
                                    AND lower(btrim(x.email)) = lower($5))
                RETURNING b.benutzer_id
            ), alte_rollen AS (
                DELETE FROM public.benutzer_rolle br USING geaendert g, firmenrolle r
                WHERE br.benutzer_id = g.benutzer_id AND br.rollen_id <> r.rollen_id
                RETURNING br.benutzer_id
            ), neue_rolle AS (
                INSERT INTO public.benutzer_rolle (benutzer_id, rollen_id)
                SELECT g.benutzer_id, r.rollen_id FROM geaendert g CROSS JOIN firmenrolle r
                ON CONFLICT DO NOTHING
                RETURNING benutzer_id
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, benutzer_id)
                SELECT $10, 'Benutzer bearbeitet', 'Gespeichert',
                       $5::text || ' · Rolle ' || r.bezeichnung ||
                       CASE WHEN $8::boolean THEN ' · aktiv' ELSE ' · deaktiviert' END,
                       $2::bigint, $11::bigint
                FROM geaendert CROSS JOIN firmenrolle r
            )
            SELECT benutzer_id FROM geaendert
            """;
        var (akteurArt, akteurId) = Akteur();
        var parameters = new string?[]
        {
            Text(benutzerId), Text(firmaId), eingaben.Vorname, eingaben.Nachname, eingaben.Email,
            eingaben.AbteilungId is long abteilung ? Text(abteilung) : null,
            eingaben.Sprache, eingaben.Aktiv ? "true" : "false", Text(eingaben.RollenId),
            akteurArt, akteurId, TextArray(VergebbareRollen)
        };

        var rows = await SchreibenAsync(query, parameters, cancellationToken);
        if (rows.Count == 1)
            return;

        if (await EmailVergebenAsync(firmaId, benutzerId, eingaben.Email, cancellationToken))
            throw new BenutzerEmailVergebenException(null);
        throw new InvalidOperationException("Das Konto wurde nicht gefunden. Bitte die Liste neu laden.");
    }

    /// <summary>„Konto deaktivieren“ aus K02. Vorgänge des Kontos bleiben zugeordnet; nur die Anmeldung endet.</summary>
    public async Task KontoStatusSetzenAsync(long benutzerId, bool aktiv, CancellationToken cancellationToken = default)
    {
        long firmaId = await ZugriffPruefenAsync(cancellationToken);
        if (!aktiv && BenutzerSitzung.AktuellerBenutzer?.Id == benutzerId)
            throw new ArgumentException("Das eigene Konto kann nicht deaktiviert werden.");

        const string query = """
            WITH geaendert AS (
                UPDATE public.benutzer SET aktiv = $3::boolean
                WHERE benutzer_id = $1::bigint AND unternehmen_id = $2::bigint
                RETURNING benutzer_id, email
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, benutzer_id)
                SELECT $4, CASE WHEN $3::boolean THEN 'Konto aktiviert' ELSE 'Konto deaktiviert' END,
                       'Gespeichert', g.email, $2::bigint, $5::bigint
                FROM geaendert g
            )
            SELECT benutzer_id FROM geaendert
            """;
        var (akteurArt, akteurId) = Akteur();
        var rows = await _client.Value.QueryAsync(query,
            new string?[] { Text(benutzerId), Text(firmaId), aktiv ? "true" : "false", akteurArt, akteurId },
            cancellationToken);
        if (rows.Count != 1)
            throw new InvalidOperationException("Das Konto wurde nicht gefunden. Bitte die Liste neu laden.");
    }

    // ── K03: Eigene Rollen und Berechtigungen ────────────────────────────────────────

    public async Task<IReadOnlyList<Berechtigung>> BerechtigungenLadenAsync(CancellationToken cancellationToken = default)
    {
        await ZugriffPruefenAsync(cancellationToken);
        var rows = await _client.Value.QueryAsync(
            "SELECT berechtigung_id, schluessel, bezeichnung, bereich FROM public.berechtigung ORDER BY berechtigung_id",
            Array.Empty<string?>(), cancellationToken);
        return rows.Select(row => new Berechtigung(
            long.Parse(Required(row, 0), CultureInfo.InvariantCulture), Required(row, 1), Required(row, 2), Required(row, 3))).ToArray();
    }

    public async Task<IReadOnlyList<EigeneRolleDaten>> EigeneRollenLadenAsync(CancellationToken cancellationToken = default)
    {
        long firmaId = await ZugriffPruefenAsync(cancellationToken);
        const string query = """
            SELECT r.rollen_id, r.bezeichnung,
                   COALESCE((SELECT json_agg(rb.berechtigung_id ORDER BY rb.berechtigung_id)
                             FROM public.rolle_berechtigung rb WHERE rb.rollen_id = r.rollen_id), '[]'::json),
                   (SELECT count(*) FROM public.benutzer_rolle br WHERE br.rollen_id = r.rollen_id)
            FROM public.rolle r
            WHERE r.unternehmen_id = $1::bigint
            ORDER BY lower(r.bezeichnung), r.rollen_id
            """;
        var rows = await _client.Value.QueryAsync(query, new string?[] { Text(firmaId) }, cancellationToken);
        return rows.Select(row => new EigeneRolleDaten(
            long.Parse(Required(row, 0), CultureInfo.InvariantCulture), Required(row, 1),
            Array.AsReadOnly(JsonSerializer.Deserialize<long[]>(Required(row, 2)) ?? Array.Empty<long>()),
            int.Parse(Required(row, 3), CultureInfo.InvariantCulture))).ToArray();
    }

    /// <summary>
    /// Legt eine eigene Rolle an (rolleId null) oder ändert Name und Berechtigungen einer vorhandenen.
    /// Die Häkchen werden ersetzt: Danach hat die Rolle genau die übergebenen Berechtigungen.
    /// </summary>
    public async Task<long> EigeneRolleSpeichernAsync(long? rolleId, string name, IReadOnlyCollection<long> berechtigungIds,
        CancellationToken cancellationToken = default)
    {
        long firmaId = await ZugriffPruefenAsync(cancellationToken);
        string bezeichnung = RequiredText(name, "Rollenname", EigeneRollenRegeln.MaximaleLaenge);
        if (EigeneRollenRegeln.IstReservierterName(bezeichnung))
            throw new ArgumentException($"„{bezeichnung}“ ist schon eine Standardrolle. Bitte einen anderen Namen wählen.");
        if (berechtigungIds.Count == 0)
            throw new ArgumentException("Bitte mindestens eine Berechtigung auswählen.");

        // Nur Berechtigungen, die es wirklich gibt, landen in rolle_berechtigung (JOIN auf berechtigung).
        string query = rolleId is null
            ? """
              WITH ziel AS (
                  INSERT INTO public.rolle (bezeichnung, unternehmen_id) VALUES ($1, $2::bigint)
                  RETURNING rollen_id
              ), rechte AS (
                  INSERT INTO public.rolle_berechtigung (rollen_id, berechtigung_id)
                  SELECT z.rollen_id, b.berechtigung_id
                  FROM ziel z JOIN public.berechtigung b ON b.berechtigung_id = ANY($3::bigint[])
                  RETURNING rollen_id
              ), protokoll AS (
                  INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, benutzer_id)
                  SELECT $4, 'Rolle angelegt', 'Gespeichert',
                         $1::text || ' · ' || cardinality($3::bigint[]) || ' Berechtigungen', $2::bigint, $5::bigint
                  FROM ziel
              )
              SELECT rollen_id FROM ziel
              """
            : """
              WITH ziel AS (
                  UPDATE public.rolle SET bezeichnung = $1
                  WHERE rollen_id = $6::bigint AND unternehmen_id = $2::bigint
                  RETURNING rollen_id
              ), alte_rechte AS (
                  DELETE FROM public.rolle_berechtigung rb USING ziel z
                  WHERE rb.rollen_id = z.rollen_id AND NOT (rb.berechtigung_id = ANY($3::bigint[]))
                  RETURNING rb.rollen_id
              ), neue_rechte AS (
                  INSERT INTO public.rolle_berechtigung (rollen_id, berechtigung_id)
                  SELECT z.rollen_id, b.berechtigung_id
                  FROM ziel z JOIN public.berechtigung b ON b.berechtigung_id = ANY($3::bigint[])
                  ON CONFLICT DO NOTHING
                  RETURNING rollen_id
              ), protokoll AS (
                  INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, benutzer_id)
                  SELECT $4, 'Rolle bearbeitet', 'Gespeichert',
                         $1::text || ' · ' || cardinality($3::bigint[]) || ' Berechtigungen', $2::bigint, $5::bigint
                  FROM ziel
              )
              SELECT rollen_id FROM ziel
              """;
        var (akteurArt, akteurId) = Akteur();
        var parameters = new List<string?>
        {
            bezeichnung, Text(firmaId), LongArray(berechtigungIds.Distinct()), akteurArt, akteurId
        };
        if (rolleId is long id)
            parameters.Add(Text(id));

        IReadOnlyList<string?[]> rows;
        try
        {
            rows = await _client.Value.QueryAsync(query, parameters, cancellationToken);
        }
        catch (NeonSqlException exception) when (exception.Code == "23505")
        {
            throw new ArgumentException($"Eine Rolle „{bezeichnung}“ gibt es in Ihrem Unternehmen schon.", exception);
        }

        if (rows.Count != 1)
            throw new InvalidOperationException("Die Rolle wurde nicht gefunden. Bitte die Seite neu laden.");
        return long.Parse(Required(rows[0], 0), CultureInfo.InvariantCulture);
    }

    /// <summary>Löscht eine eigene Rolle. Solange Konten sie haben, bleibt sie bestehen.</summary>
    public async Task EigeneRolleLoeschenAsync(long rolleId, CancellationToken cancellationToken = default)
    {
        long firmaId = await ZugriffPruefenAsync(cancellationToken);
        const string query = """
            WITH geloescht AS (
                DELETE FROM public.rolle r
                WHERE r.rollen_id = $1::bigint AND r.unternehmen_id = $2::bigint
                  AND NOT EXISTS (SELECT 1 FROM public.benutzer_rolle br WHERE br.rollen_id = r.rollen_id)
                RETURNING r.rollen_id, r.bezeichnung
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, benutzer_id)
                SELECT $3, 'Rolle gelöscht', 'Gespeichert', g.bezeichnung, $2::bigint, $4::bigint
                FROM geloescht g
            )
            SELECT rollen_id FROM geloescht
            """;
        var (akteurArt, akteurId) = Akteur();
        var rows = await _client.Value.QueryAsync(query,
            new string?[] { Text(rolleId), Text(firmaId), akteurArt, akteurId }, cancellationToken);
        if (rows.Count == 1)
            return;

        var benutzer = await _client.Value.QueryAsync(
            "SELECT 1 FROM public.benutzer_rolle WHERE rollen_id = $1::bigint LIMIT 1",
            new string?[] { Text(rolleId) }, cancellationToken);
        throw new InvalidOperationException(benutzer.Count > 0
            ? "Diese Rolle ist noch Benutzern zugewiesen. Bitte ihnen zuerst in K01 eine andere Rolle geben."
            : "Die Rolle wurde nicht gefunden. Bitte die Seite neu laden.");
    }

    // ── K04: Passwort-Richtlinien ────────────────────────────────────────────────────

    public async Task<PasswortRichtlinie> PasswortRichtlinieLadenAsync(CancellationToken cancellationToken = default)
    {
        long firmaId = await ZugriffPruefenAsync(cancellationToken);
        return await RichtlinieLesenAsync(firmaId, cancellationToken);
    }

    public async Task PasswortRichtlinieSpeichernAsync(PasswortRichtlinie richtlinie, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(richtlinie);
        long firmaId = await ZugriffPruefenAsync(cancellationToken);
        richtlinie.Pruefen();

        // updated_at mitsetzen: Das Admin-Dashboard erkennt daran, dass sich das Unternehmen geändert hat.
        const string query = """
            WITH geaendert AS (
                UPDATE public.unternehmen
                SET passwort_mindestlaenge = $2::integer, passwortregeln = $3, updated_at = clock_timestamp()
                WHERE unternehmen_id = $1::bigint
                RETURNING unternehmen_id
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, benutzer_id)
                SELECT $4, 'Passwort-Richtlinie geändert', 'Gespeichert',
                       'Mindestlänge ' || $2::integer || ' · ' || $3::text, $1::bigint, $5::bigint
                FROM geaendert
            )
            SELECT unternehmen_id FROM geaendert
            """;
        var (akteurArt, akteurId) = Akteur();
        var rows = await _client.Value.QueryAsync(query,
            new string?[]
            {
                Text(firmaId), richtlinie.Mindestlaenge.ToString(CultureInfo.InvariantCulture),
                richtlinie.RegelnAlsText(), akteurArt, akteurId
            },
            cancellationToken);
        if (rows.Count != 1)
            throw new InvalidOperationException("Das Unternehmen wurde nicht gefunden.");
    }

    private async Task<PasswortRichtlinie> RichtlinieLesenAsync(long firmaId, CancellationToken cancellationToken)
    {
        var rows = await _client.Value.QueryAsync(
            "SELECT passwort_mindestlaenge, passwortregeln FROM public.unternehmen WHERE unternehmen_id = $1::bigint",
            new string?[] { Text(firmaId) }, cancellationToken);
        if (rows.Count != 1)
            throw new InvalidOperationException("Das Unternehmen wurde nicht gefunden.");
        return PasswortRichtlinie.AusDatenbank(int.Parse(Required(rows[0], 0), CultureInfo.InvariantCulture), rows[0][1]);
    }

    // ── Berechtigung des Kundenadmins ────────────────────────────────────────────────

    /// <summary>
    /// Prüft vor jedem Laden oder Speichern erneut in Neon, ob das Konto noch Kundenadmin seiner Firma ist,
    /// und liefert die Id dieser Firma. Ein MegakrasserAdmin mit Admin-Zugang darf die Firma ebenfalls verwalten.
    /// </summary>
    private async Task<long> ZugriffPruefenAsync(CancellationToken cancellationToken)
    {
        if (BenutzerSitzung.Firmenzugang is AdminFirmenzugang zugang)
        {
            await AuthService.DashboardZugriffPruefenAsync(cancellationToken);
            return zugang.UnternehmenId;
        }

        if (BenutzerSitzung.IstFirmenVorschau)
            throw new InvalidOperationException("Die Entwicklungsvorschau lädt keine Firmendaten. Bitte mit einem Kundenadmin-Konto anmelden.");

        var benutzer = BenutzerSitzung.AktuellerBenutzer;
        if (benutzer?.UnternehmenId is not long firmaId || !benutzer.FirmenRollen.Contains(FirmenRolle.Kundenadmin))
            throw new UnauthorizedAccessException("Für diese Ansicht ist die Rolle Kundenadmin erforderlich.");

        // r.unternehmen_id IS NULL: nur die echte Standardrolle zählt, keine gleichnamige eigene Rolle.
        // Eine im Dashboard gesperrte Firma (Status „Inaktiv“) bekommt keine Daten mehr.
        const string query = $"""
            SELECT b.benutzer_id FROM public.benutzer b
            WHERE b.benutzer_id = $1::bigint AND b.unternehmen_id = $2::bigint AND b.aktiv = true
              AND {AuthService.FirmaNichtGesperrt}
              AND EXISTS (SELECT 1 FROM public.benutzer_rolle br
                          JOIN public.rolle r ON r.rollen_id = br.rollen_id
                          WHERE br.benutzer_id = b.benutzer_id AND r.bezeichnung = $3 AND r.unternehmen_id IS NULL)
            """;
        var rows = await _client.Value.QueryAsync(query,
            new string?[] { Text(benutzer.Id), Text(firmaId), FirmenRollenKatalog.DatenbankName(FirmenRolle.Kundenadmin) },
            cancellationToken);
        if (rows.Count != 1 || !ReferenceEquals(benutzer, BenutzerSitzung.AktuellerBenutzer))
            throw new UnauthorizedAccessException("Die Kundenadmin-Berechtigung ist nicht mehr vorhanden. Bitte erneut anmelden.");

        return firmaId;
    }

    /// <summary>
    /// Prüft vor dem Speichern, ob Rolle und Abteilung zur Firma passen, und schützt das eigene Konto:
    /// Wer sich selbst deaktiviert oder die eigene Kundenadmin-Rolle entfernt, sperrt sich aus.
    /// </summary>
    private async Task RolleUndAbteilungPruefenAsync(long firmaId, long? benutzerId, FirmenBenutzerEntwurf eingaben,
        CancellationToken cancellationToken)
    {
        var rows = await _client.Value.QueryAsync(
            """
            SELECT bezeichnung, unternehmen_id IS NULL FROM public.rolle
            WHERE rollen_id = $1::bigint
              AND ((unternehmen_id IS NULL AND bezeichnung = ANY($3::text[])) OR unternehmen_id = $2::bigint)
            """,
            new string?[] { Text(eingaben.RollenId), Text(firmaId), TextArray(VergebbareRollen) }, cancellationToken);
        if (rows.Count != 1)
            throw new ArgumentException("Die gewählte Rolle gibt es nicht mehr. Bitte die Seite neu laden.");

        string rolle = Required(rows[0], 0);
        bool standard = Required(rows[0], 1) == "t";
        if (standard && rolle == FirmenRollenKatalog.DatenbankName(FirmenRolle.Abteilungsleiter) && eingaben.AbteilungId is null)
            throw new ArgumentException("Ein Abteilungsleiter braucht eine Abteilung.");

        if (benutzerId is not null && BenutzerSitzung.AktuellerBenutzer?.Id == benutzerId)
        {
            if (!eingaben.Aktiv)
                throw new ArgumentException("Das eigene Konto kann nicht deaktiviert werden.");
            if (!standard || rolle != FirmenRollenKatalog.DatenbankName(FirmenRolle.Kundenadmin))
                throw new ArgumentException("Die eigene Rolle Kundenadmin kann nicht entfernt werden.");
        }
    }

    // Wie im UnternehmenRepository: Wer speichert, steht im Aktionsprotokoll.
    private static (string AkteurArt, string? BenutzerId) Akteur()
    {
        var benutzer = BenutzerSitzung.AktuellerBenutzer;
        return benutzer is null ? ("Entwicklungsansicht", null) : ("Benutzer", Text(benutzer.Id));
    }

    // ── Hilfen ───────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<string?[]>> SchreibenAsync(string query, string?[] parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _client.Value.QueryAsync(query, parameters, cancellationToken);
        }
        catch (NeonSqlException exception) when (exception.Code == "23505")
        {
            // Eindeutiger Schlüssel (Unternehmen_ID, E-Mail) aus dem ERM, z. B. bei gleichzeitigem Speichern.
            throw new BenutzerEmailVergebenException(exception);
        }
    }

    private async Task<bool> EmailVergebenAsync(long firmaId, long? benutzerId, string email, CancellationToken cancellationToken)
    {
        var rows = await _client.Value.QueryAsync(
            """
            SELECT 1 FROM public.benutzer
            WHERE unternehmen_id = $1::bigint AND lower(btrim(email)) = lower($2)
              AND benutzer_id IS DISTINCT FROM $3::bigint
            """,
            new string?[] { Text(firmaId), email, benutzerId is long id ? Text(id) : null }, cancellationToken);
        return rows.Count > 0;
    }

    private static FirmenBenutzerEntwurf ValidateAndTrim(FirmenBenutzerEntwurf entwurf, bool neu)
    {
        ArgumentNullException.ThrowIfNull(entwurf);
        var vorname = RequiredText(entwurf.Vorname, "Vorname", 100);
        var nachname = RequiredText(entwurf.Nachname, "Nachname", 100);
        var email = RequiredText(entwurf.Email, "E-Mail", 254).ToLowerInvariant();
        if (!MailAddress.TryCreate(email, out var adresse) || !string.Equals(adresse.Address, email, StringComparison.Ordinal))
            throw new ArgumentException("E-Mail: Bitte eine gültige E-Mail-Adresse eingeben.");
        if (!SprachKatalog.IstGueltig(entwurf.Sprache))
            throw new ArgumentException("Bitte eine Sprache auswählen.");

        string? passwort = null;
        if (neu)
        {
            // Passwort NICHT trimmen: Leerzeichen können gewollt sein. Die Mindestlänge prüft die Richtlinie.
            passwort = entwurf.Passwort ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(passwort) > 72)
                throw new ArgumentException("Das Startpasswort ist zu lang (höchstens 72 UTF-8-Bytes).");
        }

        return entwurf with { Vorname = vorname, Nachname = nachname, Email = email, Passwort = passwort };
    }

    private static string RequiredText(string? text, string field, int maximumLength)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0 || value.Length > maximumLength)
            throw new ArgumentException($"{field} muss ausgefüllt sein und darf höchstens {maximumLength} Zeichen enthalten.");
        return value;
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    // PostgreSQL-Array als Text, z. B. {"Disponent","Fahrer"}. Es wird als Parameter übertragen, nicht in SQL eingesetzt.
    private static string TextArray(IEnumerable<string> werte) =>
        "{" + string.Join(",", werte.Select(wert => "\"" + wert.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "}";

    private static string LongArray(IEnumerable<long> werte) =>
        "{" + string.Join(",", werte.Select(Text)) + "}";

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}
