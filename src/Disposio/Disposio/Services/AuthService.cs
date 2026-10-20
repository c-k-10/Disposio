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
/// Ergebnis der Rechteprüfung für Firmendaten: Nur Daten dieser Firma dürfen gelesen oder geändert
/// werden. AbteilungId ist die Abteilung des Kontos (falls zugeordnet); beim Admin-Zugang null.
/// Ansichten des Abteilungsleiters zeigen damit nur die eigene Abteilung.
/// </summary>
public sealed record FirmenZugriff(long UnternehmenId, long? AbteilungId);

/// <summary>
/// Anmeldung = Fachkonto (Firma, Rolle) in der eigenen Tabelle finden + Passwort prüfen.
/// Mit Neon Auth verknüpfte Konten prüft Neon Auth, alle anderen der gespeicherte BCrypt-Hash.
/// Berechtigung = seine Rolle prüfen. Ein richtiges Passwort allein öffnet noch kein Admin-Dashboard.
/// </summary>
public static class AuthService
{
    public const string MegakrasserAdminRolle = "SuperKrasserAdmin";

    // Reihenfolge der Spalten: 0 id, 1 vorname, 2 nachname, 3 sprache, 4 unternehmen_id,
    // 5 auth_user_id, 6 passwort_hash, 7 Rollen als JSON.
    private const string BenutzerSpalten = """
        b.benutzer_id, b.vorname, b.nachname, b.sprache, b.unternehmen_id, b.auth_user_id, b.passwort_hash,
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

        // 1. Fachkonto in der eigenen Tabelle suchen. Das Passwort geht dabei NICHT an SQL.
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

        var rows = await new NeonSqlClient().QueryAsync(query, parameters, cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1)
        {
            // LIMIT 2 erkennt mehrdeutige alte Konten. Wir wählen niemals willkürlich eines davon.
            return null;
        }

        var row = rows[0];
        var benutzerId = Required(row, 0);
        var gespeicherteAuthId = row[5];

        if (gespeicherteAuthId is not null)
        {
            // 2a. Schon mit Neon Auth verknüpft: Nur Neon Auth prüft das Passwort. Dadurch wirkt
            // auch ein über „Passwort vergessen“ neu gesetztes Passwort sofort.
            var anmeldung = await NeonAuthClient.Instanz.AnmeldenAsync(kontoEmail, passwort, cancellationToken)
                .ConfigureAwait(false);
            if (!anmeldung.Erfolg || anmeldung.Nutzer is null ||
                !string.Equals(gespeicherteAuthId, anmeldung.Nutzer.Id, StringComparison.Ordinal))
            {
                // Falsches Passwort, oder die E-Mail gehört bei Neon Auth zu einer anderen Identität.
                return null;
            }
        }
        else
        {
            // 2b. Noch nicht verknüpft: Das bisherige Passwort (BCrypt-Hash) muss stimmen.
            // Wichtig: Bei Neon Auth kann sich jeder selbst registrieren. Eine Neon-Auth-Anmeldung
            // allein beweist deshalb NICHT, dass jemand Besitzer dieses Kontos ist.
            if (!PasswortPasst(passwort, Required(row, 6)))
            {
                return null;
            }

            // Erst wenn das bisherige Passwort bewiesen ist, wird das Konto mit einem Neon-Auth-Konto
            // derselben E-Mail und desselben Passworts verknüpft. Danach gilt Schritt 2a.
            // Ohne passendes Neon-Auth-Konto bleibt es beim bisherigen Passwort.
            var anmeldung = await NeonAuthClient.Instanz.AnmeldenAsync(kontoEmail, passwort, cancellationToken)
                .ConfigureAwait(false);
            if (anmeldung.Erfolg && anmeldung.Nutzer is not null)
            {
                // "auth_user_id IS NULL" verhindert, dass ein bereits verknüpftes Konto überschrieben wird.
                // NOT EXISTS: Eine Neon-Auth-Identität gehört zu höchstens einem Konto (eindeutiger Index).
                // Hat dieselbe E-Mail schon ein Konto in einer anderen Firma verknüpft, bleibt dieses
                // Konto beim bisherigen Passwort, statt dass die Anmeldung mit einem Fehler abbricht.
                await new NeonSqlClient().QueryAsync("""
                    UPDATE public.benutzer SET auth_user_id = $2
                    WHERE benutzer_id = $1::bigint AND auth_user_id IS NULL
                      AND NOT EXISTS (SELECT 1 FROM public.benutzer andere WHERE andere.auth_user_id = $2)
                    """,
                    new string?[] { benutzerId, anmeldung.Nutzer.Id }, cancellationToken).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var rollen = JsonSerializer.Deserialize<string[]>(Required(row, 7)) ?? Array.Empty<string>();
        var benutzer = new AngemeldeterBenutzer(
            long.Parse(benutzerId, CultureInfo.InvariantCulture),
            Required(row, 1), Required(row, 2), Required(row, 3),
            row[4] is null ? null : long.Parse(Required(row, 4), CultureInfo.InvariantCulture),
            Array.AsReadOnly(rollen))
        {
            Firmenname = row[8],
            Abteilung = row[9]
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

    /// <summary>
    /// Prüft vor jedem Laden oder Speichern von Firmendaten erneut in Neon, ob das Konto die Rolle
    /// noch hat, und liefert die Firma, deren Daten gelesen werden dürfen. Beim Admin-Zugang wird
    /// stattdessen die MegakrasserAdmin-Rolle geprüft. Jede Firmenansicht mit Daten ruft das zuerst auf
    /// und verwendet danach nur die zurückgegebene UnternehmenId in ihren SQL-Abfragen.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Rolle entzogen, Konto deaktiviert oder abgemeldet.</exception>
    /// <exception cref="InvalidOperationException">Debug-Vorschau ohne Firma: Es gibt keine Firmendaten.</exception>
    public static async Task<FirmenZugriff> FirmenZugriffPruefenAsync(FirmenRolle rolle,
        CancellationToken cancellationToken = default)
    {
        if (BenutzerSitzung.IstFirmenVorschau)
        {
            throw new InvalidOperationException("In der Vorschau ohne Anmeldung werden keine Firmendaten geladen.");
        }

        if (BenutzerSitzung.Firmenzugang is AdminFirmenzugang zugang)
        {
            // Ein MegakrasserAdmin schaut in eine Kundenfirma. Er hat dort alle Büro-Rollen,
            // aber nur solange seine Admin-Rolle besteht. Abteilungsgrenzen gelten für ihn nicht.
            await DashboardZugriffPruefenAsync(cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(zugang, BenutzerSitzung.Firmenzugang))
            {
                throw new UnauthorizedAccessException("Der Admin-Zugang zu dieser Firma ist beendet.");
            }

            return new FirmenZugriff(zugang.UnternehmenId, null);
        }

        var benutzer = BenutzerSitzung.AktuellerBenutzer;
        if (benutzer?.UnternehmenId is not long unternehmenId || !benutzer.FirmenRollen.Contains(rolle))
        {
            throw new UnauthorizedAccessException("Für diese Ansicht fehlt Ihrem Konto die passende Rolle.");
        }

        // Konto aktiv, gleiche Firma und Rolle noch vorhanden? Die Abteilung wird für die Ansichten
        // des Abteilungsleiters mitgeliefert, damit sie nur die eigene Abteilung zeigen.
        const string query = """
            SELECT b.unternehmen_id, b.abteilung_id FROM public.benutzer b
            WHERE b.benutzer_id = $1::bigint AND b.aktiv = true AND b.unternehmen_id = $2::bigint
              AND EXISTS (SELECT 1 FROM public.benutzer_rolle br
                          JOIN public.rolle r ON r.rollen_id = br.rollen_id
                          WHERE br.benutzer_id = b.benutzer_id AND r.bezeichnung = $3)
            """;
        var rows = await new NeonSqlClient().QueryAsync(query, new string?[]
        {
            benutzer.Id.ToString(CultureInfo.InvariantCulture),
            unternehmenId.ToString(CultureInfo.InvariantCulture),
            FirmenRollenKatalog.DatenbankName(rolle)
        }, cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1 || !ReferenceEquals(benutzer, BenutzerSitzung.AktuellerBenutzer) ||
            BenutzerSitzung.Firmenzugang is not null)
        {
            // Während await könnte eine Abmeldung passiert sein. Auch diesen Fall lehnen wir ab.
            throw new UnauthorizedAccessException("Die Berechtigung für diese Ansicht ist nicht mehr vorhanden. Bitte melden Sie sich erneut an.");
        }

        long? abteilungId = rows[0][1] is string abteilung ? long.Parse(abteilung, CultureInfo.InvariantCulture) : null;
        return new FirmenZugriff(unternehmenId, abteilungId);
    }

    private static bool PasswortPasst(string passwort, string hash)
    {
        // Klassisches BCrypt berücksichtigt höchstens 72 UTF-8-Bytes eines Passworts.
        // Längere Eingaben werden abgelehnt, damit kein Passwortrest unbemerkt abgeschnitten wird.
        if (Encoding.UTF8.GetByteCount(passwort) > 72)
        {
            return false;
        }

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
