using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Live-Positionen für die Karte des Disponenten (D08): je aktiver Tour der eigenen Firma
/// (Status Unterwegs oder Pausiert) der neueste Punkt aus gps_position und die Stopps mit ETA.
/// Wie im KartenRepository wird zuerst die Rolle Disponent geprüft und danach nur die Firma
/// aus dieser Prüfung verwendet, nie eine Id aus der Oberfläche.
/// </summary>
public sealed class LivePositionRepository
{
    private const FirmenRolle Rolle = FirmenRolle.Disponent;

    // Erst beim ersten Zugriff verbinden, wie im UnternehmenRepository.
    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    public async Task<IReadOnlyList<LivePosition>> LadenAsync(CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        string firma = zugriff.UnternehmenId.ToString(CultureInfo.InvariantCulture);

        // Touren ohne Punkt erscheinen nicht. Fahrer und Fahrzeug nur, wenn sie zur selben Firma gehören.
        // Zeiten werden in deutscher Zeit als „HH:mm“ geliefert.
        const string query = """
            SELECT t.tour_id, t.tournummer, t.status, b.benutzer_id, b.vorname, b.nachname,
                   fz.fahrzeug_id, fz.kennzeichen, p.breitengrad, p.laengengrad,
                   to_char(p.erfasst_am AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
                   greatest(0, floor(extract(epoch FROM now() - p.erfasst_am)))::bigint,
                   (SELECT json_agg(json_build_object(
                               'reihenfolge', s.reihenfolge,
                               'bezeichnung', s.bezeichnung,
                               'status', s.status,
                               'eta', to_char(s.aktuelle_eta AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
                               'bis', to_char(s.zeitfenster_bis AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
                               'verspaetet', s.status <> 'Erledigt' AND s.aktuelle_eta IS NOT NULL
                                             AND s.aktuelle_eta > s.zeitfenster_bis)
                           ORDER BY s.reihenfolge)
                    FROM public.tourstopp s WHERE s.tour_id = t.tour_id)
            FROM public.tour t
            JOIN LATERAL (SELECT g.breitengrad, g.laengengrad, g.erfasst_am
                          FROM public.gps_position g
                          WHERE g.tour_id = t.tour_id
                          ORDER BY g.erfasst_am DESC
                          LIMIT 1) p ON true
            LEFT JOIN public.fahrer f ON f.fahrer_id = t.fahrer_id
            LEFT JOIN public.benutzer b ON b.benutzer_id = f.benutzer_id AND b.unternehmen_id = t.unternehmen_id
            LEFT JOIN public.fahrzeug fz ON fz.fahrzeug_id = t.fahrzeug_id AND fz.unternehmen_id = t.unternehmen_id
            WHERE t.unternehmen_id = $1::bigint AND t.status IN ($2, $3)
            ORDER BY t.tournummer, t.tour_id
            """;
        var rows = await _client.Value.QueryAsync(query,
            new string?[] { firma, LivePosition.StatusUnterwegs, LivePosition.StatusPausiert }, cancellationToken);

        var positionen = new List<LivePosition>();
        foreach (var row in rows)
        {
            string fahrer = $"{row[4]} {row[5]}".Trim();
            positionen.Add(new LivePosition(
                long.Parse(Required(row, 0), CultureInfo.InvariantCulture),
                Required(row, 1),
                Required(row, 2),
                fahrer.Length == 0 ? "Ohne Fahrer" : fahrer,
                row[3] is string benutzerId ? $"{KartenObjektArt.Fahrer}:{benutzerId}" : null,
                row[7],
                row[6] is string fahrzeugId ? $"{KartenObjektArt.Fahrzeug}:{fahrzeugId}" : null,
                double.Parse(Required(row, 8), CultureInfo.InvariantCulture),
                double.Parse(Required(row, 9), CultureInfo.InvariantCulture),
                Required(row, 10),
                long.Parse(Required(row, 11), CultureInfo.InvariantCulture),
                StoppsLesen(row[12])));
        }
        return positionen;
    }

    private static IReadOnlyList<LiveStopp> StoppsLesen(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return Array.Empty<LiveStopp>();

        using var dokument = JsonDocument.Parse(json);
        return dokument.RootElement.EnumerateArray().Select(s => new LiveStopp(
            s.GetProperty("reihenfolge").GetInt32(),
            s.GetProperty("bezeichnung").GetString() ?? "",
            s.GetProperty("status").GetString() ?? "",
            s.GetProperty("eta").GetString(),
            s.GetProperty("bis").GetString() ?? "",
            s.GetProperty("verspaetet").GetBoolean())).ToArray();
    }

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}
