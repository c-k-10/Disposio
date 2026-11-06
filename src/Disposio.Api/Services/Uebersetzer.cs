using System.Net.Http.Headers;
using System.Text.Json;

namespace Disposio.Api.Services;

/// <summary>
/// Phase 4: uebersetzt Chat-Nachrichten mit DeepL (Free oder Pro, erkannt am Schluessel).
/// Schluessel: lokal User-Secret DeepL:Key, auf Render Umgebungsvariable DeepL__Key.
/// Fehlt er oder antwortet DeepL mit einem Fehler, kommt null zurueck: die Nachricht wird dann
/// im Original gezeigt, Senden klappt immer.
/// </summary>
public sealed class Uebersetzer
{
    public const string HttpName = "DeepL";

    // Nach einem Fehler eine Minute Ruhe, damit das Abfragen alle 5 s DeepL nicht dauernd anfragt
    private static readonly TimeSpan Pause = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _httpFabrik;
    private readonly ILogger<Uebersetzer> _log;
    private readonly string? _schluessel;
    private DateTime _gesperrtBis = DateTime.MinValue;

    public Uebersetzer(IHttpClientFactory httpFabrik, IConfiguration konfiguration, ILogger<Uebersetzer> log)
    {
        _httpFabrik = httpFabrik;
        _log = log;
        _schluessel = konfiguration["DeepL:Key"];
        if (string.IsNullOrWhiteSpace(_schluessel))
        {
            _schluessel = null;
            _log.LogWarning("DeepL:Key fehlt. Chat-Nachrichten werden nicht uebersetzt.");
        }
    }

    /// <summary>true, wenn ein Schluessel eingetragen ist.</summary>
    public bool Verfuegbar => _schluessel is not null;

    /// <summary>
    /// Uebersetzt alle Texte von [quelle] nach [ziel] (de, en, pl) in einer Anfrage.
    /// null bei fehlendem Schluessel oder Fehler.
    /// </summary>
    public async Task<List<string>?> UebersetzenAsync(IReadOnlyList<string> texte, string quelle, string ziel)
    {
        if (_schluessel is null || texte.Count == 0 || DateTime.UtcNow < _gesperrtBis)
        {
            return null;
        }

        try
        {
            // Free-Schluessel enden auf ":fx" und haben eine eigene Adresse
            var basis = _schluessel.EndsWith(":fx", StringComparison.Ordinal)
                ? "https://api-free.deepl.com/" : "https://api.deepl.com/";
            using var anfrage = new HttpRequestMessage(HttpMethod.Post, basis + "v2/translate")
            {
                Content = JsonContent.Create(new
                {
                    text = texte,
                    source_lang = quelle.ToUpperInvariant(),
                    // Englisch als Ziel braucht eine Variante
                    target_lang = ziel == "en" ? "EN-GB" : ziel.ToUpperInvariant(),
                }),
            };
            anfrage.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", _schluessel);

            var http = _httpFabrik.CreateClient(HttpName);
            using var antwort = await http.SendAsync(anfrage);
            if (!antwort.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"DeepL antwortet mit {(int)antwort.StatusCode}");
            }

            using var json = await JsonDocument.ParseAsync(await antwort.Content.ReadAsStreamAsync());
            var ergebnis = json.RootElement.GetProperty("translations").EnumerateArray()
                .Select(t => t.GetProperty("text").GetString() ?? "")
                .ToList();
            return ergebnis.Count == texte.Count ? ergebnis : null;
        }
        catch (Exception ex)
        {
            _gesperrtBis = DateTime.UtcNow.Add(Pause);
            _log.LogWarning("Uebersetzung {Quelle}->{Ziel} fehlgeschlagen: {Art} {Text}", quelle, ziel, ex.GetType().Name, ex.Message);
            return null;
        }
    }
}
