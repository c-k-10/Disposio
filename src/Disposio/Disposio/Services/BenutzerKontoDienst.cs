using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Disposio.Services;

/// <summary>
/// Ruft die Neon Function auf, die Neon-Auth-Konten anlegt. Die Funktion prüft selbst, ob die
/// aufrufende Person Kundenadmin (oder Plattform-Admin) ist. Das Startpasswort geht nur per HTTPS
/// an die Funktion und wird nirgends gespeichert oder ausgegeben.
/// Fehlermeldungen sind absichtlich lesbar: KundenadminRepository gibt sie an die Oberfläche weiter.
/// </summary>
public static class BenutzerKontoDienst
{
    // Nach dem Deploy mit "neon functions get konto" ausgeben lassen und hier eintragen.
    private const string FunktionsUrl = "https://br-shy-darkness-b21twd96-konto.compute.c-6.eu-central-1.aws.neon.tech/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Legt das Konto bei Neon Auth an und liefert dessen Id (für benutzer.auth_user_id).</summary>
    public static async Task<string> KontoAnlegenAsync(string email, string name, string passwort,
        CancellationToken cancellationToken = default)
    {
        using var antwort = await SendenAsync(new { aktion = "anlegen", email, name, passwort }, cancellationToken);
        string text = await antwort.Content.ReadAsStringAsync(cancellationToken);

        if (antwort.IsSuccessStatusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                string? id = doc.RootElement.GetProperty("authId").GetString();
                if (!string.IsNullOrEmpty(id))
                    return id;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException) { }
            throw new InvalidOperationException("Die Antwort des Servers war unvollständig. Bitte erneut versuchen.");
        }

        throw FehlerAus(antwort.StatusCode, text);
    }

    /// <summary>Räumt ein Konto wieder auf, wenn das Speichern in der Datenbank gescheitert ist.</summary>
    public static async Task KontoEntfernenAsync(string authId)
    {
        try
        {
            using var antwort = await SendenAsync(new { aktion = "entfernen", authId }, CancellationToken.None);
        }
        catch
        {
            // Aufräumen ist nur ein Versuch. Ein übrig gebliebenes Konto ohne Eintrag in "benutzer"
            // kann sich nicht anmelden und lässt sich später entfernen.
        }
    }

    private static async Task<HttpResponseMessage> SendenAsync(object body, CancellationToken cancellationToken)
    {
        string? jwt = await NeonAuthClient.Instanz.JwtHolenAsync(cancellationToken);
        if (string.IsNullOrEmpty(jwt))
            throw new UnauthorizedAccessException("Die Sitzung ist abgelaufen. Bitte melden Sie sich erneut an.");

        using var anfrage = new HttpRequestMessage(HttpMethod.Post, FunktionsUrl)
        {
            Content = JsonContent.Create(body)
        };
        anfrage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        try
        {
            return await Http.SendAsync(anfrage, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException(
                "Der Server für neue Konten ist gerade nicht erreichbar. Bitte kurz warten und erneut versuchen.", ex);
        }
    }

    private static Exception FehlerAus(HttpStatusCode status, string text)
    {
        if (status == HttpStatusCode.Unauthorized)
            return new UnauthorizedAccessException("Die Sitzung ist abgelaufen. Bitte melden Sie sich erneut an.");
        if (status == HttpStatusCode.Forbidden)
            return new InvalidOperationException("Für das Anlegen von Konten fehlt die Berechtigung.");

        string meldung = "Das Konto konnte nicht angelegt werden.";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("fehler", out var f) && f.ValueKind == JsonValueKind.String)
                meldung = f.GetString() ?? meldung;
        }
        catch (JsonException) { }

        return new InvalidOperationException(meldung);
    }
}
