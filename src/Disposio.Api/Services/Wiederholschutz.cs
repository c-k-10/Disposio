using System.Collections.Concurrent;

namespace Disposio.Api.Services;

/// <summary>
/// Phase 5: merkt sich je Benutzer, welche clientId (UUID aus der Warteschlange der App) schon gespeichert wurde.
/// Sendet die App nach einem Netzabbruch erneut, kommt der vorhandene Datensatz zurueck statt eines zweiten.
/// Nur im Speicher (das ERM hat keine Spalte dafuer). Nach einem Neustart der API greift die Pruefung
/// in der Datenbank (gleicher Fahrer, gleiche Werte bzw. Zeitpunkt), siehe die Endpunkte.
/// </summary>
public sealed class Wiederholschutz
{
    private static readonly TimeSpan Dauer = TimeSpan.FromDays(3);
    private const int AufraeumenAb = 5000;

    private readonly ConcurrentDictionary<string, (long Id, DateTime Bis)> _ids = new();

    /// <summary>Gueltige clientId: eine UUID. Gibt sie einheitlich klein geschrieben zurueck, sonst null.</summary>
    public static string? Pruefen(string? clientId) =>
        Guid.TryParse(clientId, out var guid) ? guid.ToString("D") : null;

    public long? Finden(string art, long benutzerId, string clientId) =>
        _ids.TryGetValue(Schluessel(art, benutzerId, clientId), out var eintrag) && eintrag.Bis > DateTime.UtcNow
            ? eintrag.Id
            : null;

    public void Merken(string art, long benutzerId, string clientId, long id)
    {
        var jetzt = DateTime.UtcNow;
        if (_ids.Count > AufraeumenAb)
        {
            foreach (var alt in _ids.Where(e => e.Value.Bis <= jetzt).Select(e => e.Key).ToList())
            {
                _ids.TryRemove(alt, out _);
            }
        }
        _ids[Schluessel(art, benutzerId, clientId)] = (id, jetzt.Add(Dauer));
    }

    private static string Schluessel(string art, long benutzerId, string clientId) => $"{art}:{benutzerId}:{clientId}";
}
