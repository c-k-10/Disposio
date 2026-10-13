using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Disposio.Services;

/// <summary>
/// Sendet parametrisierte SQL-Abfragen an Neons offiziellen HTTPS-SQL-Endpunkt.
/// Das ist derselbe PostgreSQL-Server und dieselbe Datenbank wie in Database.cs.
/// </summary>
internal sealed class NeonSqlClient
{
    // Ein HttpClient wird wiederverwendet. HTTPS (Port 443) funktioniert auch in Netzen,
    // in denen der direkte PostgreSQL-Port 5432 gesperrt ist. Keine zusätzliche Bibliothek nötig.
    // Keine automatischen Weiterleitungen: Der Header mit Zugangsdaten darf nicht an ein anderes Ziel gehen.
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };

    private readonly Uri _endpoint;
    private readonly string _connectionUri;

    public NeonSqlClient()
    {
        var settings = Database.GetSettings();
        var host = settings.Host
            ?? throw new InvalidOperationException("In der Datenbankverbindung fehlt der Neon-Host.");
        var username = settings.Username
            ?? throw new InvalidOperationException("In der Datenbankverbindung fehlt der Benutzername.");
        var database = settings.Database
            ?? throw new InvalidOperationException("In der Datenbankverbindung fehlt der Datenbankname.");
        var firstDot = host.IndexOf('.');
        if (firstDot < 0 || !host.EndsWith(".neon.tech", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Der HTTPS-Datenzugriff benötigt einen Neon-Datenbankhost.");
        }

        // Neon bildet den regionalen API-Host, indem der erste Teil des DB-Hosts durch „api“ ersetzt wird.
        // Protokoll und Header wie im offiziellen Neon-Treiber:
        // https://github.com/neondatabase/serverless/blob/v1.0.2/src/httpQuery.ts
        // https://github.com/neondatabase/serverless/blob/v1.0.2/src/shims/net/index.ts
        _endpoint = new Uri($"https://api{host[firstDot..]}/sql");

        // Die API erwartet eine PostgreSQL-URI im Header. Sonderzeichen in Benutzername, Passwort und
        // Datenbankname werden kodiert. Dieser Header darf niemals in Logs oder Fehlermeldungen erscheinen.
        _connectionUri = $"postgresql://{Uri.EscapeDataString(username)}:" +
            $"{Uri.EscapeDataString(settings.Password ?? string.Empty)}@{host}:{settings.Port}/" +
            $"{Uri.EscapeDataString(database)}?sslmode=verify-full&channel_binding=require";
    }

    public async Task<IReadOnlyList<string?[]>> QueryAsync(string query, IReadOnlyList<string?> parameters,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.TryAddWithoutValidation("Neon-Connection-String", _connectionUri);
        // PostgreSQL-Werte werden als Text in Arrays zurückgegeben. Die festen Positionen lesen wir im Repository.
        request.Headers.TryAddWithoutValidation("Neon-Raw-Text-Output", "true");
        request.Headers.TryAddWithoutValidation("Neon-Array-Mode", "true");
        var payload = JsonSerializer.Serialize(new { query, @params = parameters });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        try
        {
            using var response = await Http.SendAsync(request, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var result = document.RootElement;

            if (!response.IsSuccessStatusCode)
            {
                // Den Servertext übernehmen wir bewusst nicht: Er kann SQL oder einzelne Eingabewerte enthalten.
                throw new NeonSqlException(ReadOptionalString(result, "code"), ReadOptionalString(result, "constraint"));
            }

            if (!result.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Die Datenbank hat eine unerwartete Antwort geliefert.");
            }

            var values = new List<string?[]>();
            foreach (var row in rows.EnumerateArray())
            {
                var fields = new List<string?>();
                foreach (var field in row.EnumerateArray())
                {
                    fields.Add(field.ValueKind == JsonValueKind.Null ? null : field.GetString());
                }

                values.Add(fields.ToArray());
            }

            return values;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Die Datenbank hat innerhalb von 30 Sekunden nicht geantwortet.", exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Die Antwort der Datenbank konnte nicht gelesen werden.", exception);
        }
        // Keine automatische Wiederholung: Bei einem Verbindungsabbruch kann ein INSERT schon gespeichert sein.
        // Blindes Wiederholen könnte dasselbe Unternehmen zweimal anlegen. Die Oberfläche bietet neu Laden an.
    }

    private static string? ReadOptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

/// <summary>Enthält nur Fehlercode und Constraint-Namen, niemals Zugangsdaten oder SQL-Servertexte.</summary>
public sealed class NeonSqlException : Exception
{
    public string? Code { get; }
    public string? ConstraintName { get; }

    public NeonSqlException(string? code, string? constraintName)
        : base("Die Datenbank hat die Abfrage abgelehnt.")
    {
        Code = code;
        ConstraintName = constraintName;
    }
}
