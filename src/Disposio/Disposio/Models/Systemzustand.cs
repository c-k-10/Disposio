using System.Collections.Generic;

namespace Disposio.Models;

/// <summary>Ampel einer Prüfung im Admin-Dashboard. Reihenfolge = Schwere (für die Sortierung).</summary>
public enum Pruefstufe
{
    Ok = 0,
    NichtGeprueft = 1,
    Warnung = 2,
    Kritisch = 3
}

/// <summary>
/// Ergebnis einer Prüfung im Systemzustand des Admin-Dashboards.
/// Kurztext steht auf der Kachel, Details und Massnahme erscheinen nach dem Klick darauf.
/// </summary>
/// <param name="Name">z. B. „Datenbank“</param>
/// <param name="Kurztext">z. B. „Antwort in 140 ms“</param>
/// <param name="Details">Einzelne Befunde, z. B. betroffene Firmen oder Funktionen.</param>
/// <param name="Massnahme">Was der Admin tun kann. Leer, wenn alles in Ordnung ist.</param>
public sealed record Pruefergebnis(
    string Name,
    Pruefstufe Stufe,
    string Kurztext,
    IReadOnlyList<string> Details,
    string Massnahme);
