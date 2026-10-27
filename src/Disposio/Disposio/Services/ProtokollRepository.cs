using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Liest das Aktionsprotokoll. Nur lesen: Einträge werden hier nie geändert oder gelöscht.
/// Der Kundenadmin sieht nur seine Firma, der MegakrasserAdmin alle Firmen.
/// </summary>
public sealed class ProtokollRepository
{
    /// <summary>So viele Einträge lädt ein Klick auf „Mehr laden“.</summary>
    public const int Seitengroesse = 200;

    // Wie in den anderen Repositories erst beim ersten Zugriff verbinden.
    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    /// <summary>K05: Einträge der eigenen Firma. Beim Admin-Zugang die Firma, in die der Admin schaut.</summary>
    public async Task<IReadOnlyList<ProtokollEintrag>> FirmaLadenAsync(int bereitsGeladen,
        CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(FirmenRolle.Kundenadmin, cancellationToken);
        return await LadenAsync(zugriff.UnternehmenId, bereitsGeladen, cancellationToken);
    }

    /// <summary>Dashboard: Einträge aller Firmen, nur für den MegakrasserAdmin.</summary>
    public async Task<IReadOnlyList<ProtokollEintrag>> AlleLadenAsync(int bereitsGeladen,
        CancellationToken cancellationToken = default)
    {
        await AuthService.DashboardZugriffPruefenAsync(cancellationToken);
        return await LadenAsync(null, bereitsGeladen, cancellationToken);
    }

    private async Task<IReadOnlyList<ProtokollEintrag>> LadenAsync(long? firmaId, int bereitsGeladen,
        CancellationToken cancellationToken)
    {
        // Wer: Name des Kontos. Ein Plattformkonto (ohne Firma) wird als Disposio-Admin markiert.
        // Ohne Konto (Entwicklungsansicht) steht dort die Akteur-Art.
        const string query = """
            SELECT p.zeitpunkt,
                   CASE WHEN b.benutzer_id IS NULL THEN p.akteur_art
                        WHEN b.unternehmen_id IS NULL THEN b.vorname || ' ' || b.nachname || ' (Disposio-Admin)'
                        ELSE b.vorname || ' ' || b.nachname END,
                   u.firmenname, p.aktion, COALESCE(p.detail, '')
            FROM public.aktionsprotokoll p
            LEFT JOIN public.benutzer b ON b.benutzer_id = p.benutzer_id
            LEFT JOIN public.unternehmen u ON u.unternehmen_id = p.unternehmen_id
            WHERE $1::bigint IS NULL OR p.unternehmen_id = $1::bigint
            ORDER BY p.zeitpunkt DESC
            LIMIT $2::integer OFFSET $3::integer
            """;
        var rows = await _client.Value.QueryAsync(query, new string?[]
        {
            firmaId?.ToString(CultureInfo.InvariantCulture),
            Seitengroesse.ToString(CultureInfo.InvariantCulture),
            bereitsGeladen.ToString(CultureInfo.InvariantCulture)
        }, cancellationToken);

        return rows.Select(row => new ProtokollEintrag(
            DateTimeOffset.Parse(row[0] ?? "", CultureInfo.InvariantCulture).LocalDateTime,
            row[1] ?? "–",
            row[2],
            row[3] ?? "",
            row[4] ?? "")).ToArray();
    }
}
