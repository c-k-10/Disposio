using System;
using System.Collections.Generic;

namespace Disposio.Models;

/// <summary>
/// Eine Zeile aus public.fahrer_fahrzeug_zuordnung (US-03). Bis = null heißt: Die Zuordnung läuft noch.
/// Beendete Zuordnungen werden nie gelöscht, sie bilden die Historie (#88). Zeiten sind deutsche Ortszeit.
/// </summary>
public sealed record Fahrerzuordnung(
    long Id,
    long FahrzeugId,
    string Kennzeichen,
    long FahrerId,
    string Fahrername,
    DateTime Von,
    DateTime? Bis,
    string? Bemerkung)
{
    public bool IstAktuell => Bis is null;
}

/// <summary>
/// Ein Fahrer für die Auswahlliste in F03 (#83). AktuellesFahrzeug ist das Kennzeichen, das er gerade fährt
/// (oder null). Er darf trotzdem zugeordnet werden, die Liste zeigt es nur zur Orientierung an.
/// </summary>
public sealed record ZuordnungFahrer(
    long FahrerId,
    string Name,
    string Fuehrerscheinklasse,
    string Verfuegbarkeit,
    string? AktuellesFahrzeug)
{
    public string Anzeige
    {
        get
        {
            string text = $"{Name} · Klasse {Fuehrerscheinklasse}";
            if (!string.Equals(Verfuegbarkeit, "Verfügbar", StringComparison.OrdinalIgnoreCase))
                text += $" · {Verfuegbarkeit}";
            if (AktuellesFahrzeug is not null)
                text += $" · fährt derzeit {AktuellesFahrzeug}";
            return text;
        }
    }
}

/// <summary>
/// Ein Fahrzeug, das jetzt einem Fahrer zugeordnet werden darf (#84): aktiv, nicht in Wartung oder gesperrt
/// und ohne laufende Zuordnung.
/// </summary>
public sealed record ZuordnungFahrzeug(long Id, string Kennzeichen, string Fahrzeugtyp, string Status)
{
    public string Anzeige => $"{Kennzeichen} · {Fahrzeugtyp} · {Status}";
}

/// <summary>Alles, was F03 auf einmal lädt.</summary>
public sealed record FahrerzuordnungDaten(
    IReadOnlyList<Fahrerzuordnung> Aktuelle,
    IReadOnlyList<Fahrerzuordnung> Historie,
    IReadOnlyList<ZuordnungFahrer> Fahrer,
    IReadOnlyList<ZuordnungFahrzeug> FreieFahrzeuge);
