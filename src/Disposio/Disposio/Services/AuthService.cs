using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Ergebnis einer erfolgreichen Anmeldung. Ein Plattform-Admin hat keine Firma:
/// UnternehmenId ist dann null. Passwort und Hash gehören nicht in diese Sitzung.
/// </summary>
public sealed record AngemeldeterBenutzer(long Id, string Vorname, string Nachname, string Sprache,
    long? UnternehmenId, IReadOnlyList<string> Rollen)
{
    // In der Datenbank heißt die Rolle „SuperKrasserAdmin“, in der Oberfläche „MegakrasserAdmin“.
    // Beide meinen dieselbe Rolle. Ihr gespeicherter Name wird exakt geprüft.
    // Ein Konto mit Firmenzuordnung ist kein Plattform-Admin.
    public bool IstMegakrasserAdmin => UnternehmenId is null &&
        Rollen.Any(rolle => string.Equals(rolle, AuthService.MegakrasserAdminRolle, StringComparison.Ordinal));

    // Nur für die Anzeige in der Firmenansicht. Plattformkonten haben beides nicht.
    public string? Firmenname { get; init; }
    public string? Abteilung { get; init; }

    // Firmenrollen zählen nur bei einem Konto mit Firma. Ein Plattformkonto bekommt keine.
    public IReadOnlyList<FirmenRolle> FirmenRollen => UnternehmenId is null
        ? Array.Empty<FirmenRolle>()
        : FirmenRollenKatalog.AusDatenbank(Rollen);

    // Fahrer arbeiten in der Fahrer-App. Am PC braucht es mindestens eine Büro-Rolle.
    public bool HatFirmenArbeitsbereich => FirmenRollen.Any(rolle => rolle != FirmenRolle.Fahrer);

    public string Anzeigename => $"{Vorname} {Nachname}".Trim();
}

/// <summary>
/// Anmeldung = Konto finden + Passwort prüfen. Berechtigung = seine Rolle prüfen.
/// Ein richtiges Passwort allein öffnet noch kein Admin-Dashboard.
/// </summary>
public static class AuthService
{
    public const string MegakrasserAdminRolle = "SuperKrasserAdmin";

    private const string BenutzerSpalten = """
        b.benutzer_id, b.vorname, b.nachname, b.sprache, b.unternehmen_id, b.passwort_hash,
        COALESCE((SELECT json_agg(r.bezeichnung ORDER BY r.bezeichnung)
                  FROM public.benutzer_rolle br
                  JOIN public.rolle r ON r.rollen_id = br.rollen_id
                  WHERE br.benutzer_id = b.benutzer_id), '[]'::json)
        """;

    /// <summary>
    /// Leere Firmenkennung: Plattform-Admin ohne Firma. Ausgefüllt: Konto der passenden Firma.
    /// null bedeutet, dass diese Kombination aus Konto und Passwort nicht gültig ist.
    /// </summary>
    public static async Task<AngemeldeterBenutzer?> LoginAsync(string firma, string email, string passwort,
        CancellationToken cancellationToken = default)
    {
        var firmenkennung = (firma ?? string.Empty).Trim();
        var kontoEmail = (email ?? string.Empty).Trim();
        if (kontoEmail.Length is 0 or > 254 || firmenkennung.Length > 200 ||
            string.IsNullOrEmpty(passwort) || Encoding.UTF8.GetByteCount(passwort) > 72)
        {
            // Klassisches BCrypt berücksichtigt höchstens 72 UTF-8-Bytes eines Passworts.
            // Längere Eingaben werden abgelehnt, damit kein Passwortrest unbemerkt abgeschnitten wird.
            return null;
        }

        string query;
        string?[] parameters;
        if (firmenkennung.Length == 0)
        {
            // Projektbesitzer erhalten KEINE erfundene Firma. EXISTS verlangt ihre Admin-Rolle.
            query = $"""
                SELECT {BenutzerSpalten}, NULL::text, NULL::text
                FROM public.benutzer b
                WHERE b.unternehmen_id IS NULL AND b.aktiv = true
                  AND lower(btrim(b.email)) = lower($1)
                  AND EXISTS (SELECT 1 FROM public.benutzer_rolle br
                              JOIN public.rolle r ON r.rollen_id = br.rollen_id
                              WHERE br.benutzer_id = b.benutzer_id AND r.bezeichnung = $2)
                LIMIT 2
                """;
            parameters = new string?[] { kontoEmail, MegakrasserAdminRolle };
        }
        else
        {
            // Kennung ist der normale Firmen-Anmeldename. Der ausgeschriebene Firmenname
            // bleibt für vorhandene Konten ebenfalls möglich. Beide Spalten existieren wirklich.
            // Firmenname und Abteilung werden nur angezeigt. Die Abteilung muss zur selben Firma gehören.
            query = $"""
                SELECT {BenutzerSpalten}, u.firmenname,
                       (SELECT a.bezeichnung FROM public.abteilung a
                        WHERE a.abteilung_id = b.abteilung_id AND a.unternehmen_id = b.unternehmen_id)
                FROM public.benutzer b
                JOIN public.unternehmen u ON u.unternehmen_id = b.unternehmen_id
                WHERE b.aktiv = true AND lower(btrim(b.email)) = lower($1)
                  AND (lower(btrim(u.kennung)) = lower($2) OR lower(btrim(u.firmenname)) = lower($2))
                LIMIT 2
                """;
            parameters = new string?[] { kontoEmail, firmenkennung };
        }

        // Eine gemeinsame Konfiguration in Database.cs statt mehrerer Zugangsdaten-Kopien.
        // $1 und $2 übertragen Eingaben als Daten. Das Klartext-Passwort geht NICHT an SQL.
        var rows = await new NeonSqlClient().QueryAsync(query, parameters, cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1)
        {
            // LIMIT 2 erkennt mehrdeutige alte Konten. Wir wählen niemals willkürlich eines davon.
            return null;
        }

        var row = rows[0];
        var storedHash = Required(row, 5);
        cancellationToken.ThrowIfCancellationRequested();

        // ConfigureAwait(false) setzt die Fortsetzung außerhalb des UI-Kontexts fort.
        // BCrypt kostet Rechenzeit; die Oberfläche muss während der Anmeldung bedienbar bleiben.
        // Verify vergleicht das eingegebene Passwort mit dem gespeicherten BCrypt-Hash.
        // Der Hash wird weder angezeigt noch in AngemeldeterBenutzer übernommen.
        // Offizielle Bibliothek: https://github.com/BcryptNet/bcrypt.net
        if (!PasswortPasst(passwort, storedHash))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var rollen = JsonSerializer.Deserialize<string[]>(Required(row, 6)) ?? Array.Empty<string>();
        var benutzer = new AngemeldeterBenutzer(
            long.Parse(Required(row, 0), CultureInfo.InvariantCulture),
            Required(row, 1), Required(row, 2), Required(row, 3),
            row[4] is null ? null : long.Parse(Required(row, 4), CultureInfo.InvariantCulture),
            Array.AsReadOnly(rollen))
        {
            Firmenname = row[7],
            Abteilung = row[8]
        };

        return firmenkennung.Length == 0 && !benutzer.IstMegakrasserAdmin ? null : benutzer;
    }

    /// <summary>
    /// Prüft vor jedem Laden oder Speichern die Dashboard-Berechtigung erneut in der Datenbank.
    /// Deaktivierte Konten und Konten mit entfernter Rolle bekommen keine weiteren Unternehmensdaten.
    /// </summary>
    public static async Task DashboardZugriffPruefenAsync(CancellationToken cancellationToken = default)
    {
#if DEBUG
        // Nur der ausdrückliche Entwicklungsstart darf die Anmeldung zum Gestalten überspringen.
        // Beim Release-Build wird dieser Abschnitt nicht mitkompiliert.
        if (BenutzerSitzung.IstEntwicklungsansicht)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }
#endif
        var benutzer = BenutzerSitzung.AktuellerBenutzer;
        if (benutzer is null || !benutzer.IstMegakrasserAdmin)
        {
            throw new UnauthorizedAccessException("Für diese Ansicht ist eine Anmeldung als MegakrasserAdmin erforderlich.");
        }

        const string query = """
            SELECT b.benutzer_id FROM public.benutzer b
            WHERE b.benutzer_id = $1::bigint AND b.aktiv = true AND b.unternehmen_id IS NULL
              AND EXISTS (SELECT 1 FROM public.benutzer_rolle br
                          JOIN public.rolle r ON r.rollen_id = br.rollen_id
                          WHERE br.benutzer_id = b.benutzer_id AND r.bezeichnung = $2)
            """;
        var rows = await new NeonSqlClient().QueryAsync(query,
            new string?[] { benutzer.Id.ToString(CultureInfo.InvariantCulture), MegakrasserAdminRolle },
            cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1 || !ReferenceEquals(benutzer, BenutzerSitzung.AktuellerBenutzer))
        {
            // Während await könnte eine Abmeldung passiert sein. Auch diesen Fall lehnen wir ab.
            throw new UnauthorizedAccessException("Die Dashboard-Berechtigung ist nicht mehr vorhanden. Bitte erneut anmelden.");
        }
    }

    private static bool PasswortPasst(string passwort, string hash)
    {
        try
        {
            // Ungültige alte Hash-Daten ermöglichen keine Anmeldung.
            BCrypt.Net.BCrypt.InterrogateHash(hash);
            return BCrypt.Net.BCrypt.Verify(passwort, hash);
        }
        catch (BCrypt.Net.HashInformationException)
        {
            return false;
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        // Kein Klartext-Vergleich als Ersatz für einen kaputten Hash.
    }

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld des Benutzerkontos fehlt ein Wert.");
}
