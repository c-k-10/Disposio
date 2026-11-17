using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Disposio.Services;

/// <summary>
/// Lädt Frachtdokumente (D11) zu Cloudinary hoch – derselbe Speicher und dieselbe Ablage wie die Fahrer-App
/// (src/Disposio.Api/Services/BildSpeicher.cs): Ordner disposio/&lt;unternehmen_id&gt;/&lt;art&gt;/, signierter Upload.
/// Zugang wie die Datenbank als Windows-Benutzervariable DISPOSIO_CLOUDINARY_URL im Format
/// cloudinary://key:secret@cloud (steht im Cloudinary-Dashboard; derselbe Wert wie Cloudinary__Url der API).
/// Ohne Variable kann D11 Dokumente anzeigen, aber keine hochladen.
/// </summary>
internal static class CloudinarySpeicher
{
    internal const string UmgebungsVariable = "DISPOSIO_CLOUDINARY_URL";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>true, wenn ein gültiger Zugang eingetragen ist.</summary>
    public static bool Verfuegbar => Lesen() is not null;

    /// <summary>Lädt die Datei hoch und gibt die https-Adresse zurück.</summary>
    /// <param name="ordner">z. B. disposio/6/lieferschein</param>
    /// <param name="name">Dateiname ohne Endung bei Cloudinary (eindeutig, z. B. eine Guid)</param>
    /// <exception cref="InvalidOperationException">Kein Zugang eingerichtet oder Cloudinary lehnt ab.</exception>
    public static async Task<string> HochladenAsync(byte[] inhalt, string mimeTyp, string ordner, string name,
        CancellationToken cancellationToken = default)
    {
        var (schluessel, geheimnis, cloud) = Lesen()
            ?? throw new InvalidOperationException(
                $"Der Dokumentenspeicher ist nicht eingerichtet (Windows-Benutzervariable {UmgebungsVariable} fehlt).");
        string zeit = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        // Signatur wie in BildSpeicher: Parameter alphabetisch, mit & verbunden, Geheimnis angehängt, SHA-1 als Hex.
        string zuSignieren = $"folder={ordner}&overwrite=false&public_id={name}&timestamp={zeit}{geheimnis}";
        string signatur = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(zuSignieren))).ToLowerInvariant();

        using var formular = new MultipartFormDataContent();
        var datei = new ByteArrayContent(inhalt);
        datei.Headers.ContentType = new MediaTypeHeaderValue(mimeTyp);
        formular.Add(datei, "file", name);
        formular.Add(new StringContent(schluessel), "api_key");
        formular.Add(new StringContent(zeit), "timestamp");
        formular.Add(new StringContent(ordner), "folder");
        formular.Add(new StringContent(name), "public_id");
        formular.Add(new StringContent("false"), "overwrite");
        formular.Add(new StringContent(signatur), "signature");

        // „image/upload“ nimmt auch PDF an; Cloudinary kann daraus später eine Bildvorschau erzeugen.
        using var antwort = await Http.PostAsync(
            $"https://api.cloudinary.com/v1_1/{Uri.EscapeDataString(cloud)}/image/upload", formular, cancellationToken);
        string text = await antwort.Content.ReadAsStringAsync(cancellationToken);
        if (!antwort.IsSuccessStatusCode)
            throw new InvalidOperationException($"Der Dokumentenspeicher hat den Upload abgelehnt ({(int)antwort.StatusCode}).");

        using var json = JsonDocument.Parse(text);
        return json.RootElement.GetProperty("secure_url").GetString()
               ?? throw new InvalidOperationException("Der Dokumentenspeicher hat keine Adresse geliefert.");
    }

    /// <summary>
    /// Löscht eine hochgeladene Datei wieder (beim Entfernen eines Dokuments). Nur Adressen aus diesem Speicher;
    /// alles andere wird ignoriert. Wirft bei Fehlern nicht – die Zuordnung in der Datenbank ist dann schon weg,
    /// eine übrig gebliebene Datei bei Cloudinary schadet nicht.
    /// </summary>
    public static async Task LoeschenAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            var zugang = Lesen();
            if (zugang is null)
                return;
            var (schluessel, geheimnis, cloud) = zugang.Value;
            string? publicId = PublicIdVon(url, cloud);
            if (publicId is null)
                return;
            string zeit = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            string zuSignieren = $"public_id={publicId}&timestamp={zeit}{geheimnis}";
            string signatur = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(zuSignieren))).ToLowerInvariant();

            using var formular = new MultipartFormDataContent
            {
                { new StringContent(publicId), "public_id" },
                { new StringContent(schluessel), "api_key" },
                { new StringContent(zeit), "timestamp" },
                { new StringContent(signatur), "signature" }
            };
            using var antwort = await Http.PostAsync(
                $"https://api.cloudinary.com/v1_1/{Uri.EscapeDataString(cloud)}/image/destroy", formular, cancellationToken);
        }
        catch (Exception)
        {
            // Absichtlich still: siehe Beschreibung.
        }
    }

    // https://res.cloudinary.com/<cloud>/image/upload/v1712345678/disposio/6/lieferschein/<guid>.pdf
    //   -> disposio/6/lieferschein/<guid>   (nur Dateien aus unserem Ordner „disposio/“)
    private static string? PublicIdVon(string url, string cloud)
    {
        string marke = $"res.cloudinary.com/{cloud}/image/upload/";
        int stelle = url.IndexOf(marke, StringComparison.OrdinalIgnoreCase);
        if (stelle < 0)
            return null;
        string rest = url[(stelle + marke.Length)..];
        int frage = rest.IndexOfAny(new[] { '?', '#' });
        if (frage >= 0)
            rest = rest[..frage];
        // Versionsangabe „v123/“ überspringen
        int strich = rest.IndexOf('/');
        if (strich > 1 && rest[0] == 'v' && rest[1..strich].All(char.IsDigit))
            rest = rest[(strich + 1)..];
        int punkt = rest.LastIndexOf('.');
        if (punkt > rest.LastIndexOf('/'))
            rest = rest[..punkt];
        return rest.StartsWith("disposio/", StringComparison.Ordinal) ? Uri.UnescapeDataString(rest) : null;
    }

    /// <summary>
    /// Adresse für die Vorschau in der App: Bilder verkleinert, PDF als Bild der ersten Seite (Cloudinary rechnet
    /// das um, wenn man die Endung auf .jpg ändert). Andere Adressen unverändert.
    /// </summary>
    public static string VorschauAdresse(string url, string mimeTyp)
    {
        const string marke = "/image/upload/";
        int stelle = url.IndexOf(marke, StringComparison.Ordinal);
        if (!url.Contains("res.cloudinary.com", StringComparison.OrdinalIgnoreCase) || stelle < 0)
            return url;

        bool pdf = mimeTyp == "application/pdf" || url.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        string umwandlung = pdf ? "pg_1,w_1200/" : "w_1200/";
        string neu = url.Insert(stelle + marke.Length, umwandlung);
        return pdf && neu.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? neu[..^4] + ".jpg" : neu;
    }

    private static (string Schluessel, string Geheimnis, string Cloud)? Lesen()
    {
        string? url = Environment.GetEnvironmentVariable(UmgebungsVariable);
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "cloudinary")
            return null;
        string[] teile = uri.UserInfo.Split(':', 2);
        if (teile.Length != 2 || teile[0].Length == 0 || teile[1].Length == 0 || uri.Host.Length == 0)
            return null;
        return (Uri.UnescapeDataString(teile[0]), Uri.UnescapeDataString(teile[1]), uri.Host);
    }
}
