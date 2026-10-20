using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Vertrag zwischen Oberfläche und Datenbank für Benutzerkonten einer Kundenfirma.
/// Die Oberfläche kennt dadurch weder SQL noch BCrypt.
/// </summary>
public interface IBenutzerRepository
{
    Task<long> KundenadminAnlegenAsync(BenutzerEntwurf entwurf, CancellationToken cancellationToken = default);
}

/// <summary>Legt Benutzer in BENUTZER an und ordnet ihnen in BENUTZER_ROLLE eine Rolle zu.</summary>
public sealed class BenutzerRepository : IBenutzerRepository
{
    // Genaue Schreibweise aus der Tabelle ROLLE.
    public const string KundenadminRolle = "Kundenadmin";

    // Gilt, bis wir Passwort_Mindestlänge des Unternehmens auslesen.
    private const int PasswortMindestlaenge = 8;

    // Wie im UnternehmenRepository erst beim ersten Zugriff verbinden.
    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    /// <summary>Nur der MegakrasserAdmin darf das: Er legt den ersten Admin einer Kundenfirma an.</summary>
    public async Task<long> KundenadminAnlegenAsync(BenutzerEntwurf entwurf,
        CancellationToken cancellationToken = default)
    {
        await AuthService.DashboardZugriffPruefenAsync(cancellationToken);
        return await AnlegenAsync(entwurf, KundenadminRolle, cancellationToken);
    }

    // Gemeinsamer Teil. Später ruft auch „Kundenadmin legt Mitarbeiter an“ diese Methode auf,
    // dann mit eigener Rechteprüfung und einer anderen Rolle.
    private async Task<long> AnlegenAsync(BenutzerEntwurf entwurf, string rolle,
        CancellationToken cancellationToken)
    {
        var eingaben = ValidateAndTrim(entwurf);

        // BCrypt kostet bewusst Rechenzeit. Task.Run hält währenddessen das Fenster bedienbar.
        // Gleicher Arbeitsfaktor wie beim Admin-Werkzeug.
        string hash = await Task.Run(() => BCrypt.Net.BCrypt.HashPassword(eingaben.Passwort, workFactor: 12),
            cancellationToken);

        // Ein einziger SQL-Befehl speichert Benutzer UND Rolle: entweder beides oder nichts.
        // firma/firmenrolle prüfen, dass Unternehmen und Rolle wirklich existieren.
        // NOT EXISTS verhindert „Max@firma.de“ und „max@firma.de“ in derselben Firma,
        // sonst fände die Anmeldung zwei Konten und würde beide ablehnen.
        const string query = """
            WITH firma AS (
                SELECT unternehmen_id FROM public.unternehmen WHERE unternehmen_id = $4::bigint
            ), firmenrolle AS (
                SELECT rollen_id FROM public.rolle WHERE bezeichnung = $6
            ), neuer_benutzer AS (
                INSERT INTO public.benutzer
                    (vorname, nachname, email, passwort_hash, unternehmen_id, abteilung_id)
                SELECT $1, $2, $3, $5, f.unternehmen_id, NULL
                FROM firma f CROSS JOIN firmenrolle
                WHERE NOT EXISTS (SELECT 1 FROM public.benutzer b
                                  WHERE b.unternehmen_id = $4::bigint
                                    AND lower(btrim(b.email)) = lower($3))
                RETURNING benutzer_id
            ), neue_zuordnung AS (
                INSERT INTO public.benutzer_rolle (benutzer_id, rollen_id)
                SELECT b.benutzer_id, r.rollen_id FROM neuer_benutzer b CROSS JOIN firmenrolle r
                RETURNING benutzer_id
            )
            SELECT benutzer_id FROM neue_zuordnung
            """;

        var parameters = new string?[]
        {
            eingaben.Vorname, eingaben.Nachname, eingaben.Email,
            eingaben.UnternehmenId.ToString(CultureInfo.InvariantCulture),
            hash, rolle
        };

        IReadOnlyList<string?[]> rows;
        try
        {
            rows = await _client.Value.QueryAsync(query, parameters, cancellationToken);
        }
        catch (NeonSqlException exception) when (exception.Code == "23505")
        {
            // Eindeutiger Schlüssel (Unternehmen_ID, E-Mail) aus dem ERM, z. B. bei gleichzeitigem Speichern.
            throw new BenutzerEmailVergebenException(exception);
        }

        if (rows.Count == 1)
        {
            return long.Parse(Required(rows[0], 0), CultureInfo.InvariantCulture);
        }

        // Keine Zeile: E-Mail schon vergeben, oder Firma/Rolle existiert nicht.
        if (await EmailVergebenAsync(eingaben.UnternehmenId, eingaben.Email, cancellationToken))
        {
            throw new BenutzerEmailVergebenException(null);
        }

        throw new InvalidOperationException($"Unternehmen oder Rolle „{rolle}“ wurde nicht gefunden.");
    }

    private async Task<bool> EmailVergebenAsync(long unternehmenId, string email, CancellationToken cancellationToken)
    {
        var rows = await _client.Value.QueryAsync(
            """
            SELECT 1 FROM public.benutzer
            WHERE unternehmen_id = $1::bigint AND lower(btrim(email)) = lower($2)
            """,
            new string?[] { unternehmenId.ToString(CultureInfo.InvariantCulture), email }, cancellationToken);
        return rows.Count > 0;
    }

    private static BenutzerEntwurf ValidateAndTrim(BenutzerEntwurf entwurf)
    {
        ArgumentNullException.ThrowIfNull(entwurf);
        if (entwurf.UnternehmenId <= 0)
            throw new ArgumentException("Bitte zuerst ein gespeichertes Unternehmen auswählen.");

        var vorname = RequiredText(entwurf.Vorname, "Vorname", 100);
        var nachname = RequiredText(entwurf.Nachname, "Nachname", 100);
        var email = RequiredText(entwurf.Email, "E-Mail", 254).ToLowerInvariant();
        if (!MailAddress.TryCreate(email, out var adresse) || !string.Equals(adresse.Address, email, StringComparison.Ordinal))
            throw new ArgumentException("E-Mail: Bitte eine gültige E-Mail-Adresse eingeben.");

        // Passwort NICHT trimmen: Leerzeichen können gewollt sein.
        var passwort = entwurf.Passwort ?? string.Empty;
        if (passwort.Length < PasswortMindestlaenge)
            throw new ArgumentException($"Das Passwort braucht mindestens {PasswortMindestlaenge} Zeichen.");
        if (Encoding.UTF8.GetByteCount(passwort) > 72)
            throw new ArgumentException("Das Passwort ist zu lang (höchstens 72 UTF-8-Bytes).");

        return entwurf with { Vorname = vorname, Nachname = nachname, Email = email, Passwort = passwort };
    }

    private static string RequiredText(string? text, string field, int maximumLength)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0 || value.Length > maximumLength)
            throw new ArgumentException($"{field} muss ausgefüllt sein und darf höchstens {maximumLength} Zeichen enthalten.");
        return value;
    }

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}

/// <summary>Verständliche Meldung statt des technischen PostgreSQL-Fehlers.</summary>
public sealed class BenutzerEmailVergebenException : Exception
{
    public BenutzerEmailVergebenException(Exception? innerException)
        : base("In diesem Unternehmen gibt es bereits ein Konto mit dieser E-Mail-Adresse.", innerException) { }
}
