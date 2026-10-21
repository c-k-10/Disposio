using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Disposio.Services;

public sealed record AuthNutzer(string Id, string Email);
public sealed record LoginErgebnis(bool Erfolg, AuthNutzer? Nutzer = null, string? Fehler = null);

public sealed record AuthErgebnis(bool Erfolg, string? Fehler = null);
public sealed record AnlageErgebnis(bool Erfolg, string? AuthId = null, string? Fehler = null);

/// <summary>
/// Dünner Client für die Neon-Auth-REST-API (Better Auth).
/// Die Pfade stammen aus dem Email-OTP- und dem Admin-Plugin von Better Auth. Bitte gegen
/// {BasisUrl}/reference in deinem Projekt prüfen.
/// </summary>
public sealed class NeonAuthClient
{
    // Neon Console -> Project -> Branch -> Auth -> Auth URL, z. B.
    // https://ep-curly-queen-b2qa2c1e.neonauth.c-6.eu-central-1.aws.neon.tech/neondb/auth
    // Die URL ist öffentlich (kein Geheimnis), darf also in der App stehen.
    private const string BasisUrl = "https://ep-curly-queen-b2qa2c1e.neonauth.c-6.eu-central-1.aws.neon.tech/neondb/auth";

    public static NeonAuthClient Instanz { get; } = new();

    // Der HttpClient hält das Sitzungs-Cookie der Anmeldung. Das braucht das Admin-Plugin.
    // Beim Abmelden wird er durch einen neuen ersetzt (SitzungVerwerfen).
    private HttpClient _http;

    private NeonAuthClient()
    {
        _http = NeuerClient();
    }

    private static HttpClient NeuerClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true
        })
        {
            BaseAddress = new Uri(BasisUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(20)
        };

        client.DefaultRequestHeaders.Add("Origin", "http://localhost:3000");
        return client;
    }

    /// <summary>
    /// Verwirft das Sitzungs-Cookie. Beim Abmelden aufrufen, damit die Admin-Sitzung
    /// nicht für den nächsten Nutzer in der App erhalten bleibt.
    /// </summary>
    public void SitzungVerwerfen()
    {
        var alt = _http;
        _http = NeuerClient();
        alt.Dispose();
    }

    /// <summary>Schickt einen Einmalcode für "Passwort vergessen" an die E-Mail-Adresse.</summary>
    public Task<AuthErgebnis> CodeSendenAsync(string email) =>
        PostAsync("email-otp/send-verification-otp", new { email, type = "forget-password" });

    /// <summary>Setzt das Passwort mit dem Code aus der E-Mail zurück.</summary>
    public Task<AuthErgebnis> PasswortZuruecksetzenAsync(string email, string code, string neuesPasswort) =>
        PostAsync("email-otp/reset-password", new { email, otp = code, password = neuesPasswort });

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

    /// <summary>
    /// Holt ein kurzlebiges JWT (laut Neon-Doku 15 Minuten gültig) für die angemeldete Sitzung.
    /// Das JWT beweist der Neon Function, wer anfragt. Null, wenn keine Sitzung besteht.
    /// </summary>
    public async Task<string?> JwtHolenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Laut Neon-Doku steht das JWT im Header "set-auth-jwt" der get-session-Antwort.
            using var sitzung = await _http.GetAsync("get-session", cancellationToken);
            if (sitzung.IsSuccessStatusCode && sitzung.Headers.TryGetValues("set-auth-jwt", out var werte))
                return werte.FirstOrDefault();

            // Ersatzweg: Better-Auth-JWT-Plugin, Route "token".
            using var tokenAntwort = await _http.GetAsync("token", cancellationToken);
            if (!tokenAntwort.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await tokenAntwort.Content.ReadAsStringAsync(cancellationToken));
            return doc.RootElement.GetProperty("token").GetString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Legt einen Nutzer bei Neon Auth an, mit festem Startpasswort. Funktioniert nur, wenn der
    /// aktuell angemeldete Nutzer in Neon Auth die Rolle "admin" hat. Das prüft Neon, nicht die App.
    /// Das Passwort wird weder gespeichert noch ausgegeben.
    /// </summary>
    public async Task<AnlageErgebnis> BenutzerAnlegenAsync(string email, string name, string passwort,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var antwort = await _http.PostAsJsonAsync("admin/create-user",
                new { email = email.Trim().ToLowerInvariant(), password = passwort, name, role = "user" },
                cancellationToken);
            var text = await antwort.Content.ReadAsStringAsync(cancellationToken);

            if (!antwort.IsSuccessStatusCode)
                return new AnlageErgebnis(false, Fehler: FehlerAusAntwort(text, (int)antwort.StatusCode));

            using var doc = JsonDocument.Parse(text);
            var id = doc.RootElement.GetProperty("user").GetProperty("id").GetString();
            return id is null
                ? new AnlageErgebnis(false, Fehler: "Antwort enthielt keine Nutzer-ID.")
                : new AnlageErgebnis(true, id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or JsonException or KeyNotFoundException)
        {
            return new AnlageErgebnis(false, Fehler: "Anlegen fehlgeschlagen. Bitte erneut versuchen.");
        }
    }

    /// <summary>
    /// Entfernt einen Nutzer bei Neon Auth wieder, zum Beispiel wenn der Eintrag in der eigenen
    /// Tabelle nach dem Anlegen fehlgeschlagen ist. Braucht ebenfalls die Admin-Sitzung.
    /// </summary>
    public Task<AuthErgebnis> BenutzerEntfernenAsync(string authId) =>
        PostAsync("admin/remove-user", new { userId = authId });

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
}
