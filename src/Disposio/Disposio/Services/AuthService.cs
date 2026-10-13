using System;
using System.Threading.Tasks;
using Npgsql;

namespace Disposio.Services
{
    public record AngemeldeterBenutzer(long Id, string Vorname, string Nachname, string Sprache, long UnternehmenId);

    public static class AuthService
    {
        // Connection String aus Umgebungsvariable, nicht im Code speichern
        private const string ConnString =
        "Host=ep-curly-queen-b2qa2c1e-pooler.c-6.eu-central-1.aws.neon.tech; Database=neondb; Username=neondb_owner; Password=npg_4pc0hjEnJIwk; SSL Mode=VerifyFull; Channel Binding=Require;";

        // Gibt den Benutzer zurück oder null, wenn die Anmeldung fehlschlägt
        public static async Task<AngemeldeterBenutzer?> LoginAsync(string firma, string email, string passwort)
        {
            const string sql = @"
                SELECT b.benutzer_id, b.vorname, b.nachname, b.sprache, b.unternehmen_id, b.passwort_hash
                FROM benutzer b
                JOIN unternehmen u ON u.unternehmen_id = b.unternehmen_id
                WHERE lower(u.firmenname) = lower(@firma)
                  AND lower(b.email) = lower(@email)
                  AND b.aktiv = true";

            await using var conn = new NpgsqlConnection(ConnString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("firma", firma);
            cmd.Parameters.AddWithValue("email", email);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            var hash = reader.GetString(5);
            if (!BCrypt.Net.BCrypt.Verify(passwort, hash))
                return null;

            return new AngemeldeterBenutzer(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4));
        }
    }
}
