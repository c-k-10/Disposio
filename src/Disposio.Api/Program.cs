using Npgsql;

// Disposio.Api - Schnittstelle zwischen Fahrer-App und Neon.
// Phase 0: nur ein Health-Check, der prueft, ob API und Datenbank erreichbar sind.

var builder = WebApplication.CreateBuilder(args);

// Verbindungsstring kommt NIE in den Code:
// lokal ueber "dotnet user-secrets", auf Render ueber die Umgebungsvariable ConnectionStrings__Neon
var neonVerbindung = builder.Configuration.GetConnectionString("Neon")
    ?? throw new InvalidOperationException("ConnectionStrings:Neon fehlt (User-Secrets oder Umgebungsvariable setzen).");

builder.Services.AddSingleton(NpgsqlDataSource.Create(neonVerbindung));

// Render gibt den Port ueber die Umgebungsvariable PORT vor
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { name = "Disposio.Api", version = "0.1" }));

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

app.Run();
