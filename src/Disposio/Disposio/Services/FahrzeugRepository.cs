using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>Vertrag zwischen den Fuhrpark-Ansichten und der Datenbank.</summary>
public interface IFahrzeugRepository
{
    Task<IReadOnlyList<Fahrzeug>> LadenAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AbteilungAuswahl>> AbteilungenLadenAsync(CancellationToken cancellationToken = default);
    Task<Fahrzeug> AnlegenAsync(FahrzeugEntwurf entwurf, CancellationToken cancellationToken = default);
}

/// <summary>
/// SQL für public.fahrzeug. Jede Methode prüft zuerst die Rolle Fuhrparkmanager (beim Admin-Zugang die
/// Admin-Rolle) und verwendet danach nur die Firma aus dieser Prüfung, nie eine Id aus der Oberfläche.
/// </summary>
public sealed class FahrzeugRepository : IFahrzeugRepository
{
    private const FirmenRolle Rolle = FirmenRolle.Fuhrparkmanager;

    // Erst beim ersten Zugriff verbinden, wie im UnternehmenRepository.
    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    // Alle SELECTs haben dieselbe Spaltenreihenfolge. ReadFahrzeug liest genau diese Positionen.
    private const string Spalten = """
        f.fahrzeug_id, f.kennzeichen, f.fahrzeugtyp, f.hersteller, f.modell, f.baujahr, f.kilometerstand,
        f.status, f.tuev_datum, f.aufbau, f.nutzlast_kg, f.nutzbare_lademeter, f.ep_stellplaetze,
        f.solltemperatur_c, (SELECT a.bezeichnung FROM public.abteilung a WHERE a.abteilung_id = f.abteilung_id)
        """;

    public async Task<IReadOnlyList<Fahrzeug>> LadenAsync(CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var rows = await _client.Value.QueryAsync(
            $"SELECT {Spalten} FROM public.fahrzeug f WHERE f.unternehmen_id = $1::bigint ORDER BY f.kennzeichen, f.fahrzeug_id",
            new string?[] { Text(zugriff.UnternehmenId) }, cancellationToken);

        var fahrzeuge = new List<Fahrzeug>();
        foreach (var row in rows)
        {
            fahrzeuge.Add(ReadFahrzeug(row));
        }

        return fahrzeuge;
    }

    public async Task<IReadOnlyList<AbteilungAuswahl>> AbteilungenLadenAsync(CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var rows = await _client.Value.QueryAsync(
            // Deaktivierte Abteilungen (K05) bleiben wählbar, damit bestehende Zuordnungen erhalten bleiben,
            // stehen aber gekennzeichnet am Ende. Voraussetzung: 006_abteilung_aktiv.sql.
            """
            SELECT abteilung_id, CASE WHEN aktiv THEN bezeichnung ELSE bezeichnung || ' (deaktiviert)' END
            FROM public.abteilung WHERE unternehmen_id = $1::bigint ORDER BY NOT aktiv, lower(bezeichnung)
            """,
            new string?[] { Text(zugriff.UnternehmenId) }, cancellationToken);

        var abteilungen = new List<AbteilungAuswahl>();
        foreach (var row in rows)
        {
            abteilungen.Add(new AbteilungAuswahl(long.Parse(Required(row, 0), CultureInfo.InvariantCulture), Required(row, 1)));
        }

        return abteilungen;
    }

    public async Task<Fahrzeug> AnlegenAsync(FahrzeugEntwurf entwurf, CancellationToken cancellationToken = default)
    {
        // Erst die Eingaben (ohne Datenbank), dann die Rechte (mit Datenbank) prüfen.
        var eingaben = Pruefen(entwurf);
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);

        // Die Abteilung muss zur selben Firma gehören. Sonst liefert „abteilung“ keine Zeile und es wird nichts
        // gespeichert. Fahrzeug und Protokolleintrag entstehen im selben Befehl: entweder beides oder nichts.
        const string query = $"""
            WITH abteilung AS (
                SELECT $16::bigint AS abteilung_id
                WHERE $16::bigint IS NULL
                   OR EXISTS (SELECT 1 FROM public.abteilung a
                              WHERE a.abteilung_id = $16::bigint AND a.unternehmen_id = $17::bigint)
            ), gespeichert AS (
                INSERT INTO public.fahrzeug
                    (kennzeichen, fahrzeugtyp, hersteller, modell, baujahr, kilometerstand, status, tuev_datum,
                     aufbau, nutzlast_kg, nutzbare_laenge_m, innenbreite_m, innenhoehe_m, nutzbare_lademeter,
                     ep_stellplaetze, solltemperatur_c, unternehmen_id, abteilung_id)
                SELECT $1, $2, $3, $4, $5::integer, $6::integer, $7, $8::date,
                       $9, $10::numeric, $11::numeric, $12::numeric, $13::numeric, $14::numeric,
                       $15::integer, $18::numeric, $17::bigint, a.abteilung_id
                FROM abteilung a
                RETURNING *
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
                SELECT CASE WHEN $19::bigint IS NULL THEN 'Entwicklungsansicht' ELSE 'Benutzer' END,
                       'Fahrzeug angelegt', 'Gespeichert', 'Kennzeichen ' || g.kennzeichen,
                       g.unternehmen_id, g.abteilung_id, $19::bigint
                FROM gespeichert g
            )
            SELECT {Spalten} FROM gespeichert f
            """;
        var parameters = new string?[]
        {
            eingaben.Kennzeichen, eingaben.Fahrzeugtyp, eingaben.Hersteller, eingaben.Modell,
            Text(eingaben.Baujahr), Text(eingaben.Kilometerstand), eingaben.Status,
            eingaben.TuevDatum.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), eingaben.Aufbau,
            Text(eingaben.NutzlastKg), Text(eingaben.NutzbareLaengeM), Text(eingaben.InnenbreiteM),
            Text(eingaben.InnenhoeheM), Text(eingaben.NutzbareLademeter), Text(eingaben.EpStellplaetze),
            eingaben.AbteilungId is long abteilungId ? Text(abteilungId) : null,
            Text(zugriff.UnternehmenId),
            eingaben.SolltemperaturC is decimal temperatur ? Text(temperatur) : null,
            // Wer speichert: das angemeldete Konto (beim Admin-Zugang der MegakrasserAdmin selbst).
            // Nur die Dashboard-Entwicklungsansicht (Debug-Build) hat kein Konto.
            BenutzerSitzung.AktuellerBenutzer is { } benutzer ? Text(benutzer.Id) : null
        };

        try
        {
            var rows = await _client.Value.QueryAsync(query, parameters, cancellationToken);
            if (rows.Count == 1)
            {
                return ReadFahrzeug(rows[0]);
            }

            // Keine Zeile: Die gewählte Abteilung gehört nicht (mehr) zu dieser Firma.
            throw new ArgumentException("Die gewählte Abteilung gibt es nicht mehr. Bitte laden Sie das Formular neu.");
        }
        catch (NeonSqlException exception) when (exception.Code == "23505" &&
            exception.ConstraintName == "fahrzeug_unternehmen_kennzeichen_uq")
        {
            // Die Datenbank prüft eindeutige Kennzeichen je Firma, auch bei zwei gleichzeitigen Speichervorgängen.
            throw new FahrzeugKennzeichenVergebenException(exception);
        }
    }

    /// <summary>
    /// Prüft die Eingaben noch einmal unabhängig von der Oberfläche. Die Meldungen sind für die Anzeige gedacht.
    /// </summary>
    private static FahrzeugEntwurf Pruefen(FahrzeugEntwurf entwurf)
    {
        ArgumentNullException.ThrowIfNull(entwurf);
        string kennzeichen = FahrzeugWerte.KennzeichenVereinheitlichen(entwurf.Kennzeichen);
        if (kennzeichen.Length is < 2 or > 15)
            throw new ArgumentException("Bitte ein gültiges Kennzeichen eingeben (2 bis 15 Zeichen).");

        string hersteller = Pflichttext(entwurf.Hersteller, "Hersteller", 100);
        string modell = Pflichttext(entwurf.Modell, "Modell", 100);
        if (!Enthaelt(FahrzeugWerte.Fahrzeugtypen, entwurf.Fahrzeugtyp))
            throw new ArgumentException("Bitte eine Fahrzeugklasse auswählen.");
        if (!Enthaelt(FahrzeugWerte.Aufbauten, entwurf.Aufbau))
            throw new ArgumentException("Bitte einen Aufbau auswählen.");
        if (!Enthaelt(FahrzeugWerte.StatusBeimAnlegen, entwurf.Status))
            throw new ArgumentException("Bitte einen Status auswählen.");

        int naechstesJahr = DateTime.Today.Year + 1;
        if (entwurf.Baujahr < 1950 || entwurf.Baujahr > naechstesJahr)
            throw new ArgumentException($"Das Baujahr muss zwischen 1950 und {naechstesJahr} liegen.");
        if (entwurf.Kilometerstand is < 0 or > 5_000_000)
            throw new ArgumentException("Der Kilometerstand muss zwischen 0 und 5.000.000 km liegen.");
        if (entwurf.EpStellplaetze is < 0 or > 100)
            throw new ArgumentException("Die Euro-Palettenstellplätze müssen zwischen 0 und 100 liegen.");

        Bereich(entwurf.NutzlastKg, 0, 60_000, "Nutzlast (kg)");
        Bereich(entwurf.NutzbareLaengeM, 0, 20, "Nutzbare Ladelänge (m)");
        Bereich(entwurf.InnenbreiteM, 0, 3, "Innenbreite (m)");
        Bereich(entwurf.InnenhoeheM, 0, 4, "Innenhöhe (m)");
        Bereich(entwurf.NutzbareLademeter, 0, 20, "Nutzbare Lademeter");

        bool gekuehlt = entwurf.Aufbau == FahrzeugWerte.Kuehlaufbau;
        if (gekuehlt && entwurf.SolltemperaturC is null)
            throw new ArgumentException("Bei einem gekühlten Aufbau ist die Solltemperatur Pflicht.");
        if (entwurf.SolltemperaturC is decimal temperatur && (temperatur < -30 || temperatur > 30))
            throw new ArgumentException("Die Solltemperatur muss zwischen -30 und 30 °C liegen.");

        return entwurf with
        {
            Kennzeichen = kennzeichen,
            Hersteller = hersteller,
            Modell = modell,
            // Ohne Kühlung gibt es keine Solltemperatur.
            SolltemperaturC = gekuehlt ? entwurf.SolltemperaturC : null
        };
    }

    private static string Pflichttext(string? text, string feld, int maximaleLaenge)
    {
        string wert = (text ?? string.Empty).Trim();
        if (wert.Length == 0 || wert.Length > maximaleLaenge)
            throw new ArgumentException($"{feld} muss ausgefüllt sein und darf höchstens {maximaleLaenge} Zeichen enthalten.");
        return wert;
    }

    private static bool Enthaelt(IReadOnlyList<string> liste, string? wert)
    {
        foreach (string eintrag in liste)
        {
            if (string.Equals(eintrag, wert, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void Bereich(decimal wert, decimal minimum, decimal maximum, string feld)
    {
        if (wert < minimum || wert > maximum)
            throw new ArgumentException($"{feld} muss zwischen {minimum.ToString(CultureInfo.CurrentCulture)} und {maximum.ToString(CultureInfo.CurrentCulture)} liegen.");
    }

    private static Fahrzeug ReadFahrzeug(string?[] row)
    {
        // Neon liefert alle Werte als Text, Zahlen mit Punkt als Dezimaltrenner. Deshalb InvariantCulture.
        return new Fahrzeug(
            long.Parse(Required(row, 0), CultureInfo.InvariantCulture),
            Required(row, 1), Required(row, 2), Required(row, 3), Required(row, 4),
            int.Parse(Required(row, 5), CultureInfo.InvariantCulture),
            int.Parse(Required(row, 6), CultureInfo.InvariantCulture),
            Required(row, 7),
            DateOnly.Parse(Required(row, 8), CultureInfo.InvariantCulture),
            Required(row, 9),
            decimal.Parse(Required(row, 10), CultureInfo.InvariantCulture),
            decimal.Parse(Required(row, 11), CultureInfo.InvariantCulture),
            int.Parse(Required(row, 12), CultureInfo.InvariantCulture),
            row[13] is string temperatur ? decimal.Parse(temperatur, CultureInfo.InvariantCulture) : null,
            row[14]);
    }

    private static string Text(long wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Text(decimal wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}

/// <summary>Eine verständliche Meldung statt des technischen PostgreSQL-Fehlers.</summary>
public sealed class FahrzeugKennzeichenVergebenException : Exception
{
    public FahrzeugKennzeichenVergebenException(Exception innerException)
        : base("Dieses Kennzeichen gibt es in Ihrem Unternehmen schon.", innerException) { }
}
