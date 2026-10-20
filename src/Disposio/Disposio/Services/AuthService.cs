using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Disposio.Services;

/// <summary>
/// Ergebnis einer erfolgreichen Anmeldung. Ein Plattform-Admin hat keine Firma:
/// UnternehmenId ist dann null. Passwort und Auth-Token gehören nicht in diese Sitzung.
/// </summary>
public sealed record AngemeldeterBenutzer(long Id, string Vorname, string Nachname, string Sprache,
    long? UnternehmenId, IReadOnlyList<string> Rollen)
{
    // In der Datenbank heißt die Rolle „SuperKrasserAdmin“, in der Oberfläche „MegakrasserAdmin“.
    // Beide meinen dieselbe Rolle. Ihr gespeicherter Name wird exakt geprüft.
    // Ein Konto mit Firmenzuordnung ist kein Plattform-Admin.
    public bool IstMegakrasserAdmin => UnternehmenId is null &&
        Rollen.Any(rolle => string.Equals(rolle, AuthService.MegakrasserAdminRolle, StringComparison.Ordinal));

    public string Anzeigename => $"{Vorname} {Nachname}".Trim();
}

/// <summary>
/// Anmeldung = Identität bei Neon Auth prüfen + Fachkonto (Firma, Rolle) in der eigenen Tabelle finden.
/// Berechtigung = seine Rolle prüfen. Ein richtiges Passwort allein öffnet noch kein Admin-Dashboard.
/// </summary>
public static class AuthService
{
    public const string MegakrasserAdminRolle = "SuperKrasserAdmin";

    // Reihenfolge der Spalten: 0 id, 1 vorname, 2 nachname, 3 sprache, 4 unternehmen_id,
    // 5 auth_user_id (früher passwort_hash), 6 Rollen als JSON.
    private const string BenutzerSpalten = """
        b.benutzer_id, b.vorname, b.nachname, b.sprache, b.unternehmen_id, b.auth_user_id,
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
        var kontoEmail = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (kontoEmail.Length is 0 or > 254 || firmenkennung.Length > 200 ||
            string.IsNullOrEmpty(passwort) || passwort.Length > 128)
        {
            return null;
        }

        // 1. Identität prüfen. Das Passwort geht nur an Neon Auth, nie an SQL.
        var anmeldung = await NeonAuthClient.Instanz.AnmeldenAsync(kontoEmail, passwort, cancellationToken)
            .ConfigureAwait(false);
        if (!anmeldung.Erfolg || anmeldung.Nutzer is null)
        {
            return null;
        }
        var authId = anmeldung.Nutzer.Id;

        // 2. Fachkonto in der eigenen Tabelle suchen (wie bisher, nur ohne Passwort-Hash).
        string query;
        string?[] parameters;
        if (firmenkennung.Length == 0)
        {
            // Projektbesitzer erhalten KEINE erfundene Firma. EXISTS verlangt ihre Admin-Rolle.
            query = $"""
                SELECT {BenutzerSpalten}
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
            query = $"""
                SELECT {BenutzerSpalten}
                FROM public.benutzer b
                JOIN public.unternehmen u ON u.unternehmen_id = b.unternehmen_id
                WHERE b.aktiv = true AND lower(btrim(b.email)) = lower($1)
                  AND (lower(btrim(u.kennung)) = lower($2) OR lower(btrim(u.firmenname)) = lower($2))
                LIMIT 2
                """;
            parameters = new string?[] { kontoEmail, firmenkennung };
        }

        var rows = await new NeonSqlClient().QueryAsync(query, parameters, cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1)
        {
            // LIMIT 2 erkennt mehrdeutige alte Konten. Wir wählen niemals willkürlich eines davon.
            return null;
        }

        var row = rows[0];
        var benutzerId = Required(row, 0);
        var gespeicherteAuthId = row[5];

        // 3. Konto mit der Neon-Auth-Identität verknüpfen.
        if (gespeicherteAuthId is null)
        {
            // Erster Login nach der Umstellung: ID einmalig eintragen. Die Bedingung
            // "auth_user_id IS NULL" verhindert, dass ein bereits verknüpftes Konto überschrieben wird.
            var verknuepft = await new NeonSqlClient().QueryAsync("""
                UPDATE public.benutzer SET auth_user_id = $2
                WHERE benutzer_id = $1::bigint AND auth_user_id IS NULL
                RETURNING benutzer_id
                """,
                new string?[] { benutzerId, authId }, cancellationToken).ConfigureAwait(false);
            if (verknuepft.Count != 1)
            {
                return null;
            }
        }
        else if (!string.Equals(gespeicherteAuthId, authId, StringComparison.Ordinal))
        {
            // E-Mail passt, aber das Neon-Auth-Konto gehört zu einer anderen Identität.
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var rollen = JsonSerializer.Deserialize<string[]>(Required(row, 6)) ?? Array.Empty<string>();
        var benutzer = new AngemeldeterBenutzer(
            long.Parse(benutzerId, CultureInfo.InvariantCulture),
            Required(row, 1), Required(row, 2), Required(row, 3),
            row[4] is null ? null : long.Parse(Required(row, 4), CultureInfo.InvariantCulture),
            Array.AsReadOnly(rollen));

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

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld des Benutzerkontos fehlt ein Wert.");
}
