using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Daten für die Live-Karte des Disponenten (D08): alle aktiven Fahrer und alle Fahrzeuge der eigenen Firma.
/// Wie in den anderen Repositories wird zuerst die Rolle Disponent geprüft und danach nur die Firma
/// aus dieser Prüfung verwendet, nie eine Id aus der Oberfläche.
/// </summary>
public sealed class KartenRepository
{
    private const FirmenRolle Rolle = FirmenRolle.Disponent;

    // Erst beim ersten Zugriff verbinden, wie im UnternehmenRepository.
    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    /// <summary>Erst die Fahrer (nach Nachname), dann die Fahrzeuge (nach Kennzeichen).</summary>
    public async Task<IReadOnlyList<KartenObjekt>> LadenAsync(CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        string firma = Text(zugriff.UnternehmenId);
        var objekte = new List<KartenObjekt>();

        // Fahrer = aktive Konten der Firma mit der Rolle „Fahrer“. Abteilung nur, wenn sie zur selben Firma gehört.
        const string fahrerQuery = """
            SELECT b.benutzer_id, b.vorname, b.nachname, a.bezeichnung
            FROM public.benutzer b
            LEFT JOIN public.abteilung a ON a.abteilung_id = b.abteilung_id AND a.unternehmen_id = b.unternehmen_id
            WHERE b.unternehmen_id = $1::bigint AND b.aktiv = true
              AND EXISTS (SELECT 1 FROM public.benutzer_rolle br
                          JOIN public.rolle r ON r.rollen_id = br.rollen_id
                          WHERE br.benutzer_id = b.benutzer_id AND r.bezeichnung = $2)
            ORDER BY lower(b.nachname), lower(b.vorname), b.benutzer_id
            """;
        var fahrer = await _client.Value.QueryAsync(fahrerQuery,
            new string?[] { firma, FirmenRollenKatalog.DatenbankName(FirmenRolle.Fahrer) }, cancellationToken);
        foreach (var row in fahrer)
        {
            string vorname = Required(row, 1);
            string nachname = Required(row, 2);
            objekte.Add(new KartenObjekt(
                KartenObjektArt.Fahrer,
                long.Parse(Required(row, 0), CultureInfo.InvariantCulture),
                $"{vorname} {nachname}".Trim(),
                row[3] is string abteilung ? $"Fahrer · Abteilung {abteilung}" : "Fahrer",
                new[] { $"{vorname} {nachname}", $"{nachname} {vorname}" }));
        }

        const string fahrzeugQuery = """
            SELECT f.fahrzeug_id, f.kennzeichen, f.fahrzeugtyp, f.aufbau, f.status
            FROM public.fahrzeug f
            WHERE f.unternehmen_id = $1::bigint
            ORDER BY f.kennzeichen, f.fahrzeug_id
            """;
        var fahrzeuge = await _client.Value.QueryAsync(fahrzeugQuery, new string?[] { firma }, cancellationToken);
        foreach (var row in fahrzeuge)
        {
            string kennzeichen = Required(row, 1);
            objekte.Add(new KartenObjekt(
                KartenObjektArt.Fahrzeug,
                long.Parse(Required(row, 0), CultureInfo.InvariantCulture),
                kennzeichen,
                $"{Required(row, 2)} · {Required(row, 3)} · {Required(row, 4)}",
                new[] { kennzeichen }));
        }

        return objekte;
    }

    private static string Text(long wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}
