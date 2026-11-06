using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Disposio.Api.Services;

/// <summary>
/// Phase 5: laedt Fotos, Unterschriften und Belege signiert zu Cloudinary hoch.
/// Einstellung: lokal User-Secret Cloudinary:Url, auf Render Umgebungsvariable Cloudinary__Url,
/// jeweils im Format cloudinary://key:secret@cloud (steht so im Cloudinary-Dashboard).
/// Ohne Einstellung antwortet POST /dokumente mit 503, alles andere laeuft.
/// Ablage: Ordner disposio/&lt;unternehmen_id&gt;/&lt;art&gt;/, Dateiname = clientId der App.
/// </summary>
public sealed class BildSpeicher
{
    public const string HttpName = "Cloudinary";

    private readonly IHttpClientFactory _httpFabrik;
    private readonly ILogger<BildSpeicher> _log;
    private readonly (string Schluessel, string Geheimnis, string Cloud)? _zugang;

    public BildSpeicher(IHttpClientFactory httpFabrik, IConfiguration konfiguration, ILogger<BildSpeicher> log)
    {
        _httpFabrik = httpFabrik;
        _log = log;
        var url = konfiguration["Cloudinary:Url"];
        if (string.IsNullOrWhiteSpace(url))
        {
            url = konfiguration["CLOUDINARY_URL"];
        }
        _zugang = Lesen(url);
        if (_zugang is null)
        {
            _log.LogWarning("Cloudinary:Url fehlt oder ist ungueltig. Fotos und Belege koennen nicht hochgeladen werden.");
        }
    }

    /// <summary>true, wenn ein gueltiger Zugang eingetragen ist.</summary>
    public bool Verfuegbar => _zugang is not null;

    /// <summary>Laedt das Bild hoch und gibt die https-Adresse zurueck. Wirft bei Fehlern.</summary>
    public async Task<string> HochladenAsync(byte[] inhalt, string mimeTyp, string ordner, string name)
    {
        var (schluessel, geheimnis, cloud) = _zugang ?? throw new InvalidOperationException("Cloudinary ist nicht eingerichtet.");
        var zeit = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        // Signatur: Parameter alphabetisch, mit & verbunden, Geheimnis angehaengt, SHA-1 als Hex
        var zuSignieren = $"folder={ordner}&overwrite=true&public_id={name}&timestamp={zeit}{geheimnis}";
        var signatur = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(zuSignieren))).ToLowerInvariant();

        using var formular = new MultipartFormDataContent();
        var datei = new ByteArrayContent(inhalt);
        datei.Headers.ContentType = new MediaTypeHeaderValue(mimeTyp);
        formular.Add(datei, "file", name);
        formular.Add(new StringContent(schluessel), "api_key");
        formular.Add(new StringContent(zeit), "timestamp");
        formular.Add(new StringContent(ordner), "folder");
        formular.Add(new StringContent(name), "public_id");
        formular.Add(new StringContent("true"), "overwrite");
        formular.Add(new StringContent(signatur), "signature");

        var http = _httpFabrik.CreateClient(HttpName);
        using var antwort = await http.PostAsync($"https://api.cloudinary.com/v1_1/{Uri.EscapeDataString(cloud)}/image/upload", formular);
        var text = await antwort.Content.ReadAsStringAsync();
        if (!antwort.IsSuccessStatusCode)
        {
            // Die Antwort enthaelt keine Schluessel, nur die Fehlermeldung von Cloudinary
            _log.LogWarning("Cloudinary-Upload fehlgeschlagen: {Status} {Text}", (int)antwort.StatusCode, text);
            throw new InvalidOperationException($"Cloudinary antwortet mit {(int)antwort.StatusCode}");
        }

        using var json = JsonDocument.Parse(text);
        return json.RootElement.GetProperty("secure_url").GetString()
               ?? throw new InvalidOperationException("Cloudinary hat keine Adresse geliefert.");
    }

    private static (string, string, string)? Lesen(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != "cloudinary")
        {
            return null;
        }
        var teile = uri.UserInfo.Split(':', 2);
        if (teile.Length != 2 || teile[0].Length == 0 || teile[1].Length == 0 || uri.Host.Length == 0)
        {
            return null;
        }
        return (Uri.UnescapeDataString(teile[0]), Uri.UnescapeDataString(teile[1]), uri.Host);
    }
}
