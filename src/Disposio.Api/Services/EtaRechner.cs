using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace Disposio.Api.Services;

/// <summary>
/// Phase 3: berechnet die voraussichtliche Ankunft (ETA) der offenen Stopps einer Tour mit der
/// TomTom Routing API (calculateRoute, Verkehr beruecksichtigt, Lkw).
/// Route: neueste GPS-Position, dann alle offenen Stopps mit Koordinaten in Reihenfolge.
/// Hoechstens alle 3 Minuten je Tour (ueber tourstopp.eta_berechnet_am).
/// Fehler werden nur geloggt, sie duerfen das Speichern der Positionen nie verhindern.
/// Schluessel: lokal User-Secret TomTom:Key, auf Render Umgebungsvariable TomTom__Key.
/// Fehlt er, wird keine ETA berechnet.
/// </summary>
public sealed class EtaRechner
{
    public const string HttpName = "TomTom";
    private static readonly TimeSpan Abstand = TimeSpan.FromMinutes(3);

    private readonly NpgsqlDataSource _db;
    private readonly IHttpClientFactory _httpFabrik;
    private readonly ILogger<EtaRechner> _log;
    private readonly string? _schluessel;

    // Letzter Versuch je Tour, damit ein TomTom-Fehler nicht bei jedem Senden neu versucht wird
    private readonly ConcurrentDictionary<long, DateTime> _letzterVersuch = new();

    public EtaRechner(NpgsqlDataSource db, IHttpClientFactory httpFabrik, IConfiguration konfiguration, ILogger<EtaRechner> log)
    {
        _db = db;
        _httpFabrik = httpFabrik;
        _log = log;
        _schluessel = konfiguration["TomTom:Key"];
        if (string.IsNullOrWhiteSpace(_schluessel))
        {
            _schluessel = null;
            _log.LogWarning("TomTom:Key fehlt. Positionen werden gespeichert, aber keine ETA berechnet.");
        }
    }

    /// <summary>Startet die Berechnung im Hintergrund. Die Antwort an die App wartet nicht darauf.</summary>
    public void Anstossen(long tourId)
    {
        if (_schluessel is null)
        {
            return;
        }
        var jetzt = DateTime.UtcNow;
        var vorher = _letzterVersuch.GetOrAdd(tourId, DateTime.MinValue);
        if (jetzt - vorher < Abstand || !_letzterVersuch.TryUpdate(tourId, jetzt, vorher))
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await BerechnenAsync(tourId);
            }
            catch (Exception ex)
            {
                // Nur Typ und Text loggen: die Adresse enthaelt den Schluessel
                _log.LogWarning("ETA fuer Tour {TourId} fehlgeschlagen: {Art} {Text}", tourId, ex.GetType().Name, ex.Message);
            }
        });
    }

    private async Task BerechnenAsync(long tourId)
    {
        await using var verbindung = await _db.OpenConnectionAsync();

        // Zuletzt berechnet vor weniger als 3 Minuten (auch nach einem Neustart der API)?
        await using (var befehl = new NpgsqlCommand("""
            SELECT max(eta_berechnet_am) > now() - $2::interval FROM public.tourstopp WHERE tour_id = $1
            """, verbindung))
        {
            befehl.Parameters.Add(new() { Value = tourId });
            befehl.Parameters.Add(new() { Value = Abstand });
            if (await befehl.ExecuteScalarAsync() is true)
            {
                return;
            }
        }

        (decimal Breite, decimal Laenge)? start = null;
        await using (var befehl = new NpgsqlCommand("""
            SELECT breitengrad, laengengrad FROM public.gps_position
            WHERE tour_id = $1 ORDER BY erfasst_am DESC LIMIT 1
            """, verbindung))
        {
            befehl.Parameters.Add(new() { Value = tourId });
            await using var leser = await befehl.ExecuteReaderAsync();
            if (await leser.ReadAsync())
            {
                start = (leser.GetDecimal(0), leser.GetDecimal(1));
            }
        }
        if (start is null)
        {
            return;
        }

        var stopps = new List<(long Id, decimal Breite, decimal Laenge)>();
        await using (var befehl = new NpgsqlCommand("""
            SELECT stopp_id, breitengrad, laengengrad FROM public.tourstopp
            WHERE tour_id = $1 AND status <> 'Erledigt' AND breitengrad IS NOT NULL AND laengengrad IS NOT NULL
            ORDER BY reihenfolge
            LIMIT 140
            """, verbindung))
        {
            befehl.Parameters.Add(new() { Value = tourId });
            await using var leser = await befehl.ExecuteReaderAsync();
            while (await leser.ReadAsync())
            {
                stopps.Add((leser.GetInt64(0), leser.GetDecimal(1), leser.GetDecimal(2)));
            }
        }
        if (stopps.Count == 0)
        {
            return;
        }

        var ankuenfte = await RouteAsync(start.Value, stopps.Select(s => (s.Breite, s.Laenge)).ToList());
        if (ankuenfte.Count != stopps.Count)
        {
            _log.LogWarning("TomTom lieferte {Anzahl} Abschnitte fuer {Stopps} Stopps (Tour {TourId})", ankuenfte.Count, stopps.Count, tourId);
            return;
        }

        await using var batch = new NpgsqlBatch(verbindung);
        for (var i = 0; i < stopps.Count; i++)
        {
            var befehl = new NpgsqlBatchCommand(
                "UPDATE public.tourstopp SET aktuelle_eta = $1, eta_berechnet_am = now() WHERE stopp_id = $2");
            befehl.Parameters.Add(new() { Value = ankuenfte[i].UtcDateTime });
            befehl.Parameters.Add(new() { Value = stopps[i].Id });
            batch.BatchCommands.Add(befehl);
        }
        await batch.ExecuteNonQueryAsync();
        _log.LogInformation("ETA fuer Tour {TourId} berechnet ({Anzahl} Stopps)", tourId, stopps.Count);
    }

    /// <summary>Ankunftszeit am Ende jedes Abschnitts (legs), also je Stopp.</summary>
    private async Task<List<DateTimeOffset>> RouteAsync((decimal Breite, decimal Laenge) start, List<(decimal Breite, decimal Laenge)> ziele)
    {
        static string Punkt((decimal Breite, decimal Laenge) p) =>
            string.Create(CultureInfo.InvariantCulture, $"{p.Breite},{p.Laenge}");

        var punkte = string.Join(':', new[] { Punkt(start) }.Concat(ziele.Select(Punkt)));
        var adresse = $"routing/1/calculateRoute/{punkte}/json?traffic=true&travelMode=truck&routeType=fastest"
                      + $"&key={Uri.EscapeDataString(_schluessel!)}";

        var http = _httpFabrik.CreateClient(HttpName);
        using var antwort = await http.GetAsync(adresse);
        if (!antwort.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"TomTom antwortet mit {(int)antwort.StatusCode}");
        }

        using var json = await JsonDocument.ParseAsync(await antwort.Content.ReadAsStreamAsync());
        var ergebnis = new List<DateTimeOffset>();
        foreach (var abschnitt in json.RootElement.GetProperty("routes")[0].GetProperty("legs").EnumerateArray())
        {
            ergebnis.Add(DateTimeOffset.Parse(
                abschnitt.GetProperty("summary").GetProperty("arrivalTime").GetString()!, CultureInfo.InvariantCulture));
        }
        return ergebnis;
    }
}
