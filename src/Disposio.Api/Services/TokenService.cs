using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Disposio.Api.Services;

/// <summary>
/// Erstellt das Anmelde-Token (JWT) fuer die Fahrer-App.
/// Der Schluessel kommt lokal aus den User-Secrets (Jwt:Key), auf Render aus Jwt__Key.
/// </summary>
public sealed class TokenService
{
    public const string Aussteller = "disposio-api";
    public static readonly TimeSpan Gueltigkeit = TimeSpan.FromDays(30);

    private readonly SymmetricSecurityKey _schluessel;

    public TokenService(SymmetricSecurityKey schluessel) => _schluessel = schluessel;

    public static SymmetricSecurityKey SchluesselAus(IConfiguration konfiguration)
    {
        var key = konfiguration["Jwt:Key"];
        if (string.IsNullOrEmpty(key) || key.Length < 32)
        {
            throw new InvalidOperationException("Jwt:Key fehlt oder ist kuerzer als 32 Zeichen (User-Secrets oder Umgebungsvariable Jwt__Key setzen).");
        }
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }

    public string Erstellen(long benutzerId, long fahrerId, long unternehmenId)
    {
        var claims = new[]
        {
            new Claim(AnmeldeClaims.BenutzerId, benutzerId.ToString()),
            new Claim(AnmeldeClaims.FahrerId, fahrerId.ToString()),
            new Claim(AnmeldeClaims.UnternehmenId, unternehmenId.ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: Aussteller,
            audience: Aussteller,
            claims: claims,
            expires: DateTime.UtcNow.Add(Gueltigkeit),
            signingCredentials: new SigningCredentials(_schluessel, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

/// <summary>Namen der Claims im Token. IDs fuer Abfragen kommen nur von hier, nie aus dem Request.</summary>
public static class AnmeldeClaims
{
    public const string BenutzerId = "benutzer_id";
    public const string FahrerId = "fahrer_id";
    public const string UnternehmenId = "unternehmen_id";

    public static long Lesen(ClaimsPrincipal nutzer, string name) =>
        long.Parse(nutzer.FindFirstValue(name) ?? throw new InvalidOperationException($"Claim {name} fehlt."));
}
