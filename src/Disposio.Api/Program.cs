using Disposio.Api.Endpoints;
using Disposio.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

// Disposio.Api - Schnittstelle zwischen Fahrer-App und Neon.
// Phase 0: Health-Check. Phase 1: Anmeldung (M01) und Profil (M09). Phase 2: Touren (M02 bis M05).
// Phase 3: GPS-Positionen, ETA mit TomTom, Abschluss (M10, M12). Phase 4: Chat mit Uebersetzung (M08).
// Phase 5: Ablieferung (M06/M07), Dokumente ueber Cloudinary, Tanken (M14), Schaden (M15).

var builder = WebApplication.CreateBuilder(args);

// Verbindungsstring kommt NIE in den Code:
// lokal ueber "dotnet user-secrets", auf Render ueber die Umgebungsvariable ConnectionStrings__Neon
var neonVerbindung = builder.Configuration.GetConnectionString("Neon")
    ?? throw new InvalidOperationException("ConnectionStrings:Neon fehlt (User-Secrets oder Umgebungsvariable setzen).");

builder.Services.AddSingleton(NpgsqlDataSource.Create(neonVerbindung));

// Token-Schluessel: lokal User-Secret Jwt:Key, auf Render Umgebungsvariable Jwt__Key
var jwtSchluessel = TokenService.SchluesselAus(builder.Configuration);
builder.Services.AddSingleton(new TokenService(jwtSchluessel));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(optionen =>
    {
        // Claim-Namen wie benutzer_id unveraendert lassen
        optionen.MapInboundClaims = false;
        optionen.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = TokenService.Aussteller,
            ValidAudience = TokenService.Aussteller,
            IssuerSigningKey = jwtSchluessel,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });
builder.Services.AddAuthorization();

// Neon Auth prueft Passwoerter verknuepfter Konten (gleiche URL wie in der WinUI-App, kein Geheimnis)
builder.Services.AddHttpClient<PasswortPruefer>(client =>
{
    var basis = builder.Configuration["NeonAuth:BasisUrl"]
        ?? throw new InvalidOperationException("NeonAuth:BasisUrl fehlt in appsettings.json.");
    client.BaseAddress = new Uri(basis.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.Add("Origin", "http://localhost:3000");
});

// TomTom Routing API fuer die ETA. Schluessel: lokal User-Secret TomTom:Key, auf Render TomTom__Key.
// Ohne Schluessel laeuft alles ausser der ETA.
builder.Services.AddHttpClient(EtaRechner.HttpName, client =>
{
    client.BaseAddress = new Uri("https://api.tomtom.com/");
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddSingleton<EtaRechner>();
builder.Services.AddHostedService<PositionenAufraeumen>();

// DeepL fuer die Chat-Uebersetzung. Schluessel: lokal User-Secret DeepL:Key, auf Render DeepL__Key.
// Ohne Schluessel laeuft der Chat, nur ohne Uebersetzung.
builder.Services.AddHttpClient(Uebersetzer.HttpName, client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<Uebersetzer>();

// Cloudinary fuer Fotos, Unterschriften und Belege. Zugang: lokal User-Secret Cloudinary:Url,
// auf Render Cloudinary__Url (cloudinary://key:secret@cloud). Ohne Zugang antwortet nur POST /dokumente mit 503.
builder.Services.AddHttpClient(BildSpeicher.HttpName, client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<BildSpeicher>();
builder.Services.AddSingleton<Wiederholschutz>();

// Render gibt den Port ueber die Umgebungsvariable PORT vor
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { name = "Disposio.Api", version = "0.6" }));

app.MapGet("/health", async (NpgsqlDataSource db) =>
{
    try
    {
        await using var befehl = db.CreateCommand("SELECT 1");
        await befehl.ExecuteScalarAsync();
        return Results.Ok(new { api = "ok", datenbank = "ok", zeit = DateTime.UtcNow });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Datenbank nicht erreichbar");
        return Results.Json(new { api = "ok", datenbank = "fehler" }, statusCode: 503);
    }
});

app.MapAuthEndpoints();
app.MapProfilEndpoints();
app.MapTourEndpoints();
app.MapPositionEndpoints();
app.MapChatEndpoints();
app.MapAblieferungEndpoints();
app.MapDokumentEndpoints();
app.MapMeldungEndpoints();

app.Run();
