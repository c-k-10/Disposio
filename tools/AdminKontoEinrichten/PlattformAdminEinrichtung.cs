using System.Net.Mail;
using System.Text;
using Disposio.Services;

namespace Disposio.AdminEinrichtung;

/// <summary>
/// Legt ein NEUES Plattformkonto mit Rolle an. Bestehende Konten werden weder
/// überschrieben noch hochgestuft. Dieses Werkzeug ist für die Projektbesitzer.
/// </summary>
internal static class PlattformAdminEinrichtung
{
    /// <summary>
    /// Jeder Projektbesitzer bekommt ein eigenes Konto mit seinem Namen. Nur so zeigt das
    /// Aktionsprotokoll später, welcher MegakrasserAdmin etwas geändert hat.
    /// </summary>
    internal static async Task<long> AnlegenAsync(string email, string vorname, string nachname, string passwort)
    {
        email = email.Trim().ToLowerInvariant();
        if (email.Length > 254 || !MailAddress.TryCreate(email, out var adresse) || adresse.Address != email)
            throw new ArgumentException("Bitte eine gültige E-Mail-Adresse eingeben.");
        vorname = vorname.Trim();
        nachname = nachname.Trim();
        if (vorname.Length is 0 or > 100 || nachname.Length is 0 or > 100)
            throw new ArgumentException("Vorname und Nachname müssen ausgefüllt sein (höchstens 100 Zeichen).");
        if (passwort.Length < 15 || Encoding.UTF8.GetByteCount(passwort) > 72)
            throw new ArgumentException("Das Passwort braucht mindestens 15 Zeichen und höchstens 72 UTF-8-Bytes.");

        // Hash = ein aufwendig berechneter Prüfwert, kein verschlüsseltes Klartext-Passwort.
        // BCrypt erzeugt selbst einen zufälligen Salt. Gleiche Passwörter ergeben dadurch
        // unterschiedliche Hashes. Arbeitsfaktor 12 macht das Durchprobieren aufwendiger.
        string hash = BCrypt.Net.BCrypt.HashPassword(passwort, workFactor: 12);

        // Ein einziger SQL-Befehl erledigt Benutzer UND Rolle atomar: Entweder sind beide
        // gespeichert oder keines von beiden. Ein halbfertiges Admin-Konto bleibt nicht zurück.
        // Die Rolle muss zuvor durch 002_plattform_admin.sql vorhanden sein.
        const string query = """
            WITH adminrolle AS (
                SELECT rollen_id FROM public.rolle WHERE bezeichnung = 'SuperKrasserAdmin'
            ), neuer_benutzer AS (
                INSERT INTO public.benutzer
                    (vorname, nachname, email, passwort_hash, unternehmen_id, abteilung_id)
                SELECT $3, $4, $1, $2, NULL, NULL FROM adminrolle
                RETURNING benutzer_id
            ), neue_zuordnung AS (
                INSERT INTO public.benutzer_rolle (benutzer_id, rollen_id)
                SELECT b.benutzer_id, r.rollen_id FROM neuer_benutzer b CROSS JOIN adminrolle r
                RETURNING benutzer_id
            )
            SELECT benutzer_id FROM neue_zuordnung
            """;
        // Nur der Hash geht in die Datenbank. Das Passwort wird nicht als SQL-Parameter gesendet.
        var rows = await new NeonSqlClient().QueryAsync(query, new string?[] { email, hash, vorname, nachname });
        if (rows.Count != 1 || !long.TryParse(rows[0][0], out long id))
            throw new InvalidOperationException("Die Admin-Rolle fehlt. Zuerst die Datenbankmigration ausführen.");
        return id;
    }
}
