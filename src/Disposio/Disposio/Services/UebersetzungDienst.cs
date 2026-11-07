using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Disposio.Services;

/// <summary>
/// Ruft die Neon Function „uebersetzen“ auf (neon-function/functions/uebersetzen.ts). Sie übersetzt eine schon
/// gespeicherte Nachricht mit DeepL und schreibt das Ergebnis in die Datenbank. Die App schickt nur die Id;
/// der DeepL-Schlüssel liegt nur auf dem Server.
/// </summary>
public static class UebersetzungDienst
{
    // Nach dem Deploy mit "neon functions get uebersetzen" ausgeben lassen und hier eintragen.
    // internal: Auch der Systemzustand im Admin-Dashboard prüft diese Adresse.
    internal const string FunktionsUrl = "https://br-shy-darkness-b21twd96-uebersetzen.compute.c-6.eu-central-1.aws.neon.tech/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// true = übersetzt (oder nicht nötig). false = fehlgeschlagen oder Server nicht erreichbar; die Nachricht
    /// bleibt im Original gespeichert und kann mit „Erneut übersetzen“ noch einmal versucht werden.
    /// </summary>
    /// <summary>
    /// Übersetzt den Fahrertext eines offenen Winston-Vorschlags (D10). Ergebnis steht danach in
    /// winston_vorschlag.fahrertext_uebersetzt. false = gerade nicht möglich.
    /// </summary>
    public static async Task<bool> VorschlagUebersetzenAsync(long vorschlagId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var antwort = await Http.PostAsJsonAsync(FunktionsUrl, new { vorschlagId }, cancellationToken)
                .ConfigureAwait(false);
            return antwort.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public static async Task<bool> UebersetzenAsync(long nachrichtId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var antwort = await Http.PostAsJsonAsync(FunktionsUrl, new { nachrichtId }, cancellationToken)
                .ConfigureAwait(false);
            return antwort.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
