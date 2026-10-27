using System;
using System.Collections.Generic;
using System.Globalization;

namespace Disposio.Models;

/// <summary>
/// Die Kennzahlen einer Firma für einen Kalendermonat (G02). Monat ist immer der Monatserste.
/// Tankvorgänge, Wartungen und Schäden zählen über die Fahrzeuge der Firma.
/// </summary>
public sealed record MonatsStatistik(
    DateOnly Monat,
    string Firmenname,
    int Touren,
    int Auftraege,
    int Tankvorgaenge,
    decimal Liter,
    decimal Tankkosten,
    int Wartungen,
    decimal Wartungskosten,
    int Schaeden,
    decimal Schadenskosten,
    int Aktionen)
{
    public decimal Gesamtkosten => Tankkosten + Wartungskosten + Schadenskosten;
}

/// <summary>Eine Zeile der Statistik, so wie sie die Ansicht und das PDF zeigen.</summary>
public sealed record StatistikZeile(string Bezeichnung, string Wert, bool Hervorgehoben = false);

/// <summary>
/// Texte und Zahlenformate der Statistik an einer Stelle, damit Ansicht und PDF gleich aussehen.
/// </summary>
public static class StatistikTexte
{
    // Die App ist deutschsprachig: „1.234,50 €“ unabhängig von der Windows-Sprache.
    private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

    // Eigene Kurznamen, weil .NET „Jan.“ und „Sept.“ mit Punkt schreibt. Gewünscht ist „Jan 22“.
    private static readonly string[] KurzeMonate =
        { "Jan", "Feb", "Mär", "Apr", "Mai", "Jun", "Jul", "Aug", "Sep", "Okt", "Nov", "Dez" };

    /// <summary>Zum Beispiel „Jan 22“ oder „Okt 26“ für die Download-Liste.</summary>
    public static string Kurz(DateOnly monat) =>
        $"{KurzeMonate[monat.Month - 1]} {(monat.Year % 100).ToString("00", CultureInfo.InvariantCulture)}";

    /// <summary>Zum Beispiel „Oktober 2026“ für Überschriften.</summary>
    public static string Lang(DateOnly monat) => monat.ToString("MMMM yyyy", Deutsch);

    public static IReadOnlyList<StatistikZeile> Zeilen(MonatsStatistik statistik) => new[]
    {
        new StatistikZeile("Touren", Anzahl(statistik.Touren)),
        new StatistikZeile("Aufträge (Lieferdatum im Monat)", Anzahl(statistik.Auftraege)),
        new StatistikZeile("Tankvorgänge", Anzahl(statistik.Tankvorgaenge)),
        new StatistikZeile("Getankte Liter", $"{statistik.Liter.ToString("N2", Deutsch)} l"),
        new StatistikZeile("Tankkosten", Euro(statistik.Tankkosten)),
        new StatistikZeile("Wartungen", Anzahl(statistik.Wartungen)),
        new StatistikZeile("Wartungskosten", Euro(statistik.Wartungskosten)),
        new StatistikZeile("Schadensmeldungen", Anzahl(statistik.Schaeden)),
        new StatistikZeile("Schadenskosten", Euro(statistik.Schadenskosten)),
        new StatistikZeile("Kosten gesamt", Euro(statistik.Gesamtkosten), Hervorgehoben: true),
        new StatistikZeile("Protokollierte Aktionen", Anzahl(statistik.Aktionen))
    };

    private static string Anzahl(int wert) => wert.ToString("N0", Deutsch);

    private static string Euro(decimal wert) => $"{wert.ToString("N2", Deutsch)} €";
}
