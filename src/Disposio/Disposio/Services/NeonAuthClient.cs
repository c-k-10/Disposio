using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;



namespace Disposio.Services;

public sealed record AuthNutzer(string Id, string Email);
public sealed record LoginErgebnis(bool Erfolg, AuthNutzer? Nutzer = null, string? Fehler = null);

public sealed record AuthErgebnis(bool Erfolg, string? Fehler = null);

/// <summary>
/// Dünner Client für die Neon-Auth-REST-API (Better Auth).
/// Die Pfade stammen aus dem Email-OTP-Plugin von Better Auth. Bitte gegen
/// {BasisUrl}/reference in deinem Projekt prüfen.
/// </summary>
public sealed class NeonAuthClient
{
    // Neon Console -> Project -> Branch -> Auth -> Auth URL, z. B.
    // https://ep-curly-queen-b2qa2c1e.neonauth.c-6.eu-central-1.aws.neon.tech/neondb/auth
    // Die URL ist öffentlich (kein Geheimnis), darf also in der App stehen.
    private const string BasisUrl = "https://ep-curly-queen-b2qa2c1e.neonauth.c-6.eu-central-1.aws.neon.tech/neondb/auth";

    public static NeonAuthClient Instanz { get; } = new();

    private readonly HttpClient _http;


    private NeonAuthClient()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(BasisUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(20)
        };

        _http.DefaultRequestHeaders.Add("Origin", "http://localhost:3000");   
    }

    /// <summary>Schickt einen Einmalcode für "Passwort vergessen" an die E-Mail-Adresse.</summary>
    public Task<AuthErgebnis> CodeSendenAsync(string email) =>
        PostAsync("email-otp/send-verification-otp", new { email, type = "forget-password" });

    /// <summary>Setzt das Passwort mit dem Code aus der E-Mail zurück.</summary>
    public Task<AuthErgebnis> PasswortZuruecksetzenAsync(string email, string code, string neuesPasswort) =>
        PostAsync("email-otp/reset-password", new { email, otp = code, password = neuesPasswort });

    private async Task<AuthErgebnis> PostAsync(string pfad, object body)
    {
        try
        {
            using var antwort = await _http.PostAsJsonAsync(pfad, body);
            if (antwort.IsSuccessStatusCode)
                return new AuthErgebnis(true);

            var text = await antwort.Content.ReadAsStringAsync();
            return new AuthErgebnis(false, FehlerAusAntwort(text, (int)antwort.StatusCode));
        }
        catch (TaskCanceledException)
        {
            return new AuthErgebnis(false, "Zeitüberschreitung. Bitte erneut versuchen.");
        }
        catch (HttpRequestException)
        {
            return new AuthErgebnis(false, "Keine Verbindung zum Server.");
        }
    }

    private static string FehlerAusAntwort(string json, int statusCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(msg.GetString()))
            {
                return msg.GetString()!;
            }
        }
        catch (JsonException) { /* keine JSON-Antwort */ }

        return $"Anfrage fehlgeschlagen (HTTP {statusCode}).";
    }

    public async Task<LoginErgebnis> AnmeldenAsync(string email, string passwort,
    CancellationToken cancellationToken = default)
    {
        try
        {
            using var antwort = await _http.PostAsJsonAsync("sign-in/email",
                new { email, password = passwort }, cancellationToken);
            var text = await antwort.Content.ReadAsStringAsync(cancellationToken);

            if (!antwort.IsSuccessStatusCode)
                return new LoginErgebnis(false, Fehler: FehlerAusAntwort(text, (int)antwort.StatusCode));

            using var doc = JsonDocument.Parse(text);
            var user = doc.RootElement.GetProperty("user");
            return new LoginErgebnis(true,
                new AuthNutzer(user.GetProperty("id").GetString()!, user.GetProperty("email").GetString()!));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or JsonException or KeyNotFoundException)
        {
            return new LoginErgebnis(false, Fehler: "Anmeldung fehlgeschlagen. Bitte erneut versuchen.");
        }
    }
}
