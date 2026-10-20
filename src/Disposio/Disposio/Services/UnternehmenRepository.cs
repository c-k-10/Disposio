using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Vertrag zwischen Oberfläche und Datenbank: laden, anlegen und bearbeiten.
/// Die Oberfläche muss dadurch weder SQL noch Verbindungsdaten kennen.
/// </summary>
public interface IUnternehmenRepository
{
    Task<IReadOnlyList<Unternehmen>> LoadAsync(CancellationToken cancellationToken = default);
    Task<Unternehmen> CreateAsync(UnternehmenEntwurf entwurf, CancellationToken cancellationToken = default);
    Task<Unternehmen> UpdateAsync(long id, DateTime version, UnternehmenEntwurf entwurf,
        CancellationToken cancellationToken = default);
    Task<AdminFirmenzugang> FirmenzugangOeffnenAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>Übersetzt Unternehmen-Datensätze in SQL für unsere vorhandene PostgreSQL-Tabelle bei Neon.</summary>
public sealed class UnternehmenRepository : IUnternehmenRepository
{
    // Erst beim Laden verbinden: Ein Konfigurationsfehler wird dann vom Lade-Dialog behandelt,
    // anstatt schon beim Erstellen der Dashboard-Seite das ganze Fenster zu beenden.
    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    // Alle SELECTs haben dieselbe Spaltenreihenfolge. ReadUnternehmen liest genau diese Positionen.
    // Die Benutzerzahl wird aus den wirklichen Benutzer-Zeilen gezählt und nicht als zweite Zahl gespeichert.
    private const string Columns = """
        u.unternehmen_id, u.firmenname, u.kennung, u.admin_email,
        (SELECT count(*)::integer FROM public.benutzer b WHERE b.unternehmen_id = u.unternehmen_id),
        u.status, u.updated_at, u.adresse, u.plz, u.ort, u.telefon, u.email, u.kontaktperson
        """;

    public async Task<IReadOnlyList<Unternehmen>> LoadAsync(CancellationToken cancellationToken = default)
    {
        // Rechteprüfung gehört auch an den Datenzugriff: Eine geöffnete Seite allein reicht nicht.
        await AuthService.DashboardZugriffPruefenAsync(cancellationToken);
        // await wartet auf die Datenbank, ohne den UI-Thread zu blockieren. Das Fenster bleibt bedienbar.
        var rows = await _client.Value.QueryAsync(
            $"SELECT {Columns} FROM public.unternehmen u ORDER BY lower(u.firmenname), u.unternehmen_id",
            Array.Empty<string?>(), cancellationToken);
        var unternehmen = new List<Unternehmen>();
        foreach (var row in rows)
        {
            unternehmen.Add(ReadUnternehmen(row));
        }

        return unternehmen;
    }

    public async Task<Unternehmen> CreateAsync(UnternehmenEntwurf entwurf,
        CancellationToken cancellationToken = default)
    {
        await AuthService.DashboardZugriffPruefenAsync(cancellationToken);
        var eingaben = ValidateAndTrim(entwurf);

        // $1 bis $10 sind Platzhalter. Eingaben werden als Parameter übertragen und NICHT in SQL eingesetzt.
        // Damit bleiben z. B. Apostrophe im Firmennamen normale Daten und können keinen SQL-Befehl verändern.
        // RETURNING liefert die wirklich gespeicherte Zeile einschließlich der neu vergebenen Id zurück.
        // Der Protokolleintrag entsteht im selben Befehl: Entweder ist beides gespeichert oder nichts.
        var query = $"""
            WITH gespeichert AS (
                INSERT INTO public.unternehmen
                    (firmenname, kennung, admin_email, status, adresse, plz, ort, telefon, email,
                     kontaktperson, passwortregeln)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 'Mindestens 8 Zeichen')
                RETURNING *
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, benutzer_id)
                SELECT $11, 'Unternehmen angelegt', 'Gespeichert',
                       'Kennung ' || g.kennung || ' · Status ' || g.status, g.unternehmen_id, $12::bigint
                FROM gespeichert g
            )
            SELECT {Columns} FROM gespeichert u
            """;
        var (akteurArt, benutzerId) = Akteur();
        var parameters = new List<string?>(EntwurfParameters(eingaben)) { akteurArt, benutzerId };

        try
        {
            var rows = await _client.Value.QueryAsync(query, parameters, cancellationToken);
            if (rows.Count == 1)
            {
                return ReadUnternehmen(rows[0]);
            }

            throw new InvalidOperationException("Die Datenbank hat das neue Unternehmen nicht bestätigt.");
        }
        catch (NeonSqlException exception) when (IsDuplicateKennung(exception))
        {
            // Die Datenbank verhindert auch doppelte Kennungen, die zwei Kollegen gleichzeitig speichern.
            throw new UnternehmenKennungVergebenException(exception);
        }
    }

    public async Task<Unternehmen> UpdateAsync(long id, DateTime version, UnternehmenEntwurf entwurf,
        CancellationToken cancellationToken = default)
    {
        await AuthService.DashboardZugriffPruefenAsync(cancellationToken);
        var eingaben = ValidateAndTrim(entwurf);
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "Ein gespeichertes Unternehmen braucht eine gültige Id.");
        }

        if (version.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Die Version muss der unveränderte UTC-Zeitstempel aus der Datenbank sein.", nameof(version));
        }

        // Id identifiziert das Unternehmen. Version ist der Zeitpunkt seiner letzten Änderung.
        // WHERE prüft beides: Hat jemand inzwischen gespeichert, überschreiben wir seine Änderung nicht.
        // Bestehende Passwortregeln und die Mindestlänge bleiben beim Bearbeiten erhalten.
        // Alle Teile eines WITH-Befehls sehen denselben Datenstand: „vorher“ liest also noch die
        // alten Werte. Daraus ergibt sich, welche Felder das Protokoll als geändert nennt.
        var query = $"""
            WITH vorher AS (
                SELECT * FROM public.unternehmen
                WHERE unternehmen_id = $11::bigint AND updated_at = $12::timestamptz
            ), gespeichert AS (
                UPDATE public.unternehmen
                SET firmenname = $1, kennung = $2, admin_email = $3, status = $4,
                    adresse = $5, plz = $6, ort = $7, telefon = $8, email = $9, kontaktperson = $10,
                    updated_at = GREATEST(clock_timestamp(), updated_at + interval '1 microsecond')
                WHERE unternehmen_id = $11::bigint AND updated_at = $12::timestamptz
                RETURNING *
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, benutzer_id)
                SELECT $13, 'Unternehmen bearbeitet', 'Gespeichert',
                       COALESCE('Geändert: ' || NULLIF(concat_ws(', ',
                           CASE WHEN v.firmenname IS DISTINCT FROM g.firmenname THEN 'Firmenname' END,
                           CASE WHEN v.kennung IS DISTINCT FROM g.kennung THEN 'Kennung' END,
                           CASE WHEN v.admin_email IS DISTINCT FROM g.admin_email THEN 'Admin-E-Mail' END,
                           CASE WHEN v.status IS DISTINCT FROM g.status THEN 'Status ' || v.status || ' → ' || g.status END,
                           CASE WHEN (v.adresse, v.plz, v.ort) IS DISTINCT FROM (g.adresse, g.plz, g.ort) THEN 'Adresse' END,
                           CASE WHEN v.telefon IS DISTINCT FROM g.telefon THEN 'Telefon' END,
                           CASE WHEN v.email IS DISTINCT FROM g.email THEN 'Firmen-E-Mail' END,
                           CASE WHEN v.kontaktperson IS DISTINCT FROM g.kontaktperson THEN 'Kontaktperson' END), ''),
                           'Ohne inhaltliche Änderung gespeichert'),
                       g.unternehmen_id, $14::bigint
                FROM gespeichert g JOIN vorher v ON v.unternehmen_id = g.unternehmen_id
            )
            SELECT {Columns} FROM gespeichert u
            """;
        var (akteurArt, benutzerId) = Akteur();
        var parameters = new List<string?>(EntwurfParameters(eingaben))
        {
            id.ToString(CultureInfo.InvariantCulture),
            version.ToString("O", CultureInfo.InvariantCulture),
            akteurArt,
            benutzerId
        };

        try
        {
            var rows = await _client.Value.QueryAsync(query, parameters, cancellationToken);
            if (rows.Count == 1)
            {
                return ReadUnternehmen(rows[0]);
            }

            // Keine Zeile bedeutet: gelöscht oder seit dem Laden geändert. Erst neu laden, dann erneut bearbeiten.
            throw new UnternehmenKonfliktException();
        }
        catch (NeonSqlException exception) when (IsDuplicateKennung(exception))
        {
            throw new UnternehmenKennungVergebenException(exception);
        }
    }

    /// <summary>
    /// „Einloggen“ im Dashboard: Ein MegakrasserAdmin öffnet die Firmenansicht einer Kundenfirma.
    /// Das ist ein Blick in fremde Firmendaten und wird deshalb immer im Aktionsprotokoll vermerkt.
    /// </summary>
    public async Task<AdminFirmenzugang> FirmenzugangOeffnenAsync(long id, CancellationToken cancellationToken = default)
    {
        await AuthService.DashboardZugriffPruefenAsync(cancellationToken);
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "Ein gespeichertes Unternehmen braucht eine gültige Id.");
        }

        // Firma lesen und Protokolleintrag schreiben geschehen in einem Befehl: Ohne Protokoll kein Zugang.
        // Ein INSERT im WITH-Teil läuft auch dann, wenn die letzte Abfrage ihn nicht ausliest.
        const string query = """
            WITH firma AS (
                SELECT unternehmen_id, firmenname FROM public.unternehmen WHERE unternehmen_id = $1::bigint
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, benutzer_id)
                SELECT $2, 'Firmenansicht als Admin geöffnet', 'Geöffnet',
                       'Admin-Zugang über das Dashboard', f.unternehmen_id, $3::bigint
                FROM firma f
            )
            SELECT unternehmen_id, firmenname FROM firma
            """;
        var (akteurArt, benutzerId) = Akteur();
        var rows = await _client.Value.QueryAsync(query,
            new string?[] { id.ToString(CultureInfo.InvariantCulture), akteurArt, benutzerId }, cancellationToken);
        if (rows.Count != 1)
        {
            // Keine Zeile bedeutet: Das Unternehmen wurde inzwischen gelöscht.
            throw new UnternehmenKonfliktException();
        }

        return new AdminFirmenzugang(long.Parse(Required(rows[0], 0), CultureInfo.InvariantCulture), Required(rows[0], 1));
    }

    /// <summary>
    /// Wer gerade speichert, für das Aktionsprotokoll: das angemeldete Konto. Jeder MegakrasserAdmin
    /// hat ein eigenes Konto, dadurch ist jede Änderung einer Person zugeordnet. Die Entwicklungsansicht
    /// (nur Debug-Build) hat kein Konto und wird als solche vermerkt.
    /// </summary>
    private static (string AkteurArt, string? BenutzerId) Akteur()
    {
        var benutzer = BenutzerSitzung.AktuellerBenutzer;
        return benutzer is null
            ? ("Entwicklungsansicht", null)
            : ("Benutzer", benutzer.Id.ToString(CultureInfo.InvariantCulture));
    }

    private static Unternehmen ReadUnternehmen(string?[] row)
    {
        // Die Nummern sind die Positionen der SELECT-Spalten, beginnend bei 0.
        // Neons Textantwort wird hier in C#-Typen umgewandelt. Die Oberfläche bekommt fertige Datensätze.
        return new Unternehmen(
            long.Parse(Required(row, 0), CultureInfo.InvariantCulture),
            Required(row, 1), Required(row, 2), Required(row, 3),
            int.Parse(Required(row, 4), CultureInfo.InvariantCulture), Required(row, 5),
            DateTimeOffset.Parse(Required(row, 6), CultureInfo.InvariantCulture).UtcDateTime,
            Required(row, 7), Required(row, 8), Required(row, 9), Required(row, 10), Required(row, 11), Required(row, 12));
    }

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");

    private static string?[] EntwurfParameters(UnternehmenEntwurf entwurf) =>
        // Bei $1, $2 usw. entscheidet die Reihenfolge dieser Parameter, welcher Wert eingesetzt wird.
        new string?[] { entwurf.Firmenname, entwurf.Kennung, entwurf.AdminEmail, entwurf.Status,
            entwurf.Adresse, entwurf.Plz, entwurf.Ort, entwurf.Telefon, entwurf.KontaktEmail, entwurf.Kontaktperson };

    private static UnternehmenEntwurf ValidateAndTrim(UnternehmenEntwurf entwurf)
    {
        ArgumentNullException.ThrowIfNull(entwurf);
        var firmenname = RequiredText(entwurf.Firmenname, "Firmenname", 200);
        var kennung = RequiredText(entwurf.Kennung, "Unternehmenskennung", 100);
        var adminEmail = ValidateEmail(entwurf.AdminEmail, "E-Mail des Kundenadmins");
        var status = (entwurf.Status ?? string.Empty).Trim();
        var adresse = RequiredText(entwurf.Adresse, "Adresse", 300);
        var plz = RequiredText(entwurf.Plz, "Postleitzahl", 20);
        var ort = RequiredText(entwurf.Ort, "Ort", 150);
        var telefon = RequiredText(entwurf.Telefon, "Telefon", 50);
        var kontaktperson = RequiredText(entwurf.Kontaktperson, "Kontaktperson", 200);
        // Die Oberfläche darf die Firmen-E-Mail leer lassen; dann verwenden wir die angegebene Admin-E-Mail.
        var kontaktEmail = ValidateEmail(string.IsNullOrWhiteSpace(entwurf.KontaktEmail)
            ? adminEmail : entwurf.KontaktEmail, "Firmen-E-Mail");

        // Auch der Datenzugriff prüft die Eingaben: Eine spätere andere Oberfläche braucht dieselben Regeln.
        if (status is not ("Aktiv" or "Testphase"))
        {
            throw new ArgumentException("Als Status sind Aktiv und Testphase erlaubt.");
        }

        return new UnternehmenEntwurf(firmenname, kennung, adminEmail, status, adresse, plz, ort,
            telefon, kontaktEmail, kontaktperson);
    }

    private static string RequiredText(string? text, string field, int maximumLength)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0 || value.Length > maximumLength)
        {
            throw new ArgumentException($"{field} muss ausgefüllt sein und darf höchstens {maximumLength} Zeichen enthalten.");
        }

        return value;
    }

    private static string ValidateEmail(string? text, string field)
    {
        var email = RequiredText(text, field, 254);
        if (!MailAddress.TryCreate(email, out var address) || !string.Equals(address.Address, email, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{field}: Bitte eine gültige E-Mail-Adresse ohne zusätzlichen Anzeigenamen eingeben.");
        }

        return email;
    }

    private static bool IsDuplicateKennung(NeonSqlException exception) =>
        exception.Code == "23505" && exception.ConstraintName == "ux_unternehmen_kennung";
}

/// <summary>Eine verständliche Fehlermeldung anstelle des technischen PostgreSQL-Fehlertexts.</summary>
public sealed class UnternehmenKennungVergebenException : Exception
{
    public UnternehmenKennungVergebenException(Exception innerException)
        : base("Diese Unternehmenskennung ist bereits vergeben.", innerException) { }
}

/// <summary>Verhindert, dass beim Bearbeiten eine neuere Änderung eines Kollegen verloren geht.</summary>
public sealed class UnternehmenKonfliktException : Exception
{
    public UnternehmenKonfliktException()
        : base("Dieses Unternehmen wurde inzwischen geändert oder gelöscht. Bitte die Liste neu laden und erneut bearbeiten.") { }
}
