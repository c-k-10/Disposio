using System;
using System.Collections.Generic;

namespace Disposio.Models;

/// <summary>
/// Ein Tankvorgang aus der Fahrer-App (public.tankvorgang) für F06 „Tankvorgänge“ im Fuhrpark.
/// Kosten ist der Gesamtbetrag; den Preis pro Liter rechnet <see cref="PreisProEinheit"/> daraus aus.
/// Bei Kraftstoff „Strom“ ist die Menge in kWh. Beleg = neuestes Dokument zum Tankvorgang, Belege = Anzahl aller.
/// </summary>
public sealed record Tankvorgang(
    long Id,
    long FahrzeugId,
    string Kennzeichen,
    DateOnly Datum,
    decimal Menge,
    decimal Kosten,
    int? Kilometerstand,
    string Tankstelle,
    string Kraftstoff,
    string Status,
    string? Fahrer,
    string? Notiz,
    string? BelegAdresse,
    int Belege)
{
    public bool IstStrom => string.Equals(Kraftstoff, "Strom", StringComparison.OrdinalIgnoreCase);

    /// <summary>„l“ oder bei Strom „kWh“.</summary>
    public string Einheit => IstStrom ? "kWh" : "l";

    /// <summary>Kosten ÷ Menge, null wenn keine Menge erfasst ist.</summary>
    public decimal? PreisProEinheit => Menge > 0 ? Kosten / Menge : null;
}

/// <summary>Ein Fahrzeug für die Auswahl in F06 (auch inaktive, damit ihre Tankvorgänge sichtbar bleiben).</summary>
public sealed record TankFahrzeug(long Id, string Kennzeichen, bool Aktiv)
{
    public string Anzeige => Aktiv ? Kennzeichen : $"{Kennzeichen} (inaktiv)";
}
