using System.Text;
using System.Text.Json;

namespace Disposio.Api.Services;

/// <summary>
/// Prueft das Passwort genau wie die WinUI-App (AuthService):
/// Mit Neon Auth verknuepfte Konten (auth_user_id gesetzt) prueft Neon Auth,
/// alle anderen der gespeicherte BCrypt-Hash.
/// </summary>
public sealed class PasswortPruefer
{
    private readonly HttpClient _http;

    public PasswortPruefer(HttpClient http) => _http = http;

    public async Task<bool> PasstAsync(string email, string passwort, string? authUserId, string passwortHash)
    {
        return authUserId is null
            ? BcryptPasst(passwort, passwortHash)
            : await NeonAuthPasstAsync(email, passwort, authUserId);
    }

    private static bool BcryptPasst(string passwort, string hash)
    {
        // BCrypt beruecksichtigt hoechstens 72 Bytes. Laengere Eingaben werden abgelehnt, nicht abgeschnitten.
        if (Encoding.UTF8.GetByteCount(passwort) > 72)
        {
            return false;
        }

        try
        {
            BCrypt.Net.BCrypt.InterrogateHash(hash);
            return BCrypt.Net.BCrypt.Verify(passwort, hash);
        }
        catch (Exception ex) when (ex is BCrypt.Net.HashInformationException or BCrypt.Net.SaltParseException or ArgumentException)
        {
            // Kaputter Hash ermoeglicht keine Anmeldung.
            return false;
        }
    }

    private async Task<bool> NeonAuthPasstAsync(string email, string passwort, string authUserId)
    {
        try
        {
            using var antwort = await _http.PostAsJsonAsync("sign-in/email", new { email, password = passwort });
            if (!antwort.IsSuccessStatusCode)
            {
                return false;
            }

            // Die Neon-Auth-Identitaet muss genau die sein, die mit diesem Konto verknuepft ist.
            using var doc = JsonDocument.Parse(await antwort.Content.ReadAsStringAsync());
            var id = doc.RootElement.GetProperty("user").GetProperty("id").GetString();
            return string.Equals(id, authUserId, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return false;
        }
    }
}
