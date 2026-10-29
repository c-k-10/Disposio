using System;
using System.Linq;

namespace Disposio.Models;

/// <summary>
/// Gemeinsame Prüfregeln für „Sendung zuweisen“ (D16) und „Tour zuweisen“ (D06):
/// Passt ein freies Fahrzeug zur Ladung, und darf der Fahrer dieses Fahrzeug fahren?
/// </summary>
public static class ZuweisungsRegeln
{
    /// <summary>Warum ein Fahrzeug für die Ladung nicht taugt. null = passt. Werte 0 = unbekannt, wird nicht geprüft.</summary>
    public static string? FahrzeugPasstNicht(FreiesFahrzeug f, bool gekuehlt, decimal gewichtKg, decimal lademeter, int europaletten)
    {
        if (gekuehlt && f.Aufbau != FahrzeugWerte.Kuehlaufbau)
            return "kein Kühlaufbau";
        if (f.NutzlastKg > 0 && gewichtKg > f.NutzlastKg)
            return "Nutzlast zu gering";
        if (f.NutzbareLademeter > 0 && lademeter > f.NutzbareLademeter)
            return "zu wenig Lademeter";
        if (f.EpStellplaetze > 0 && europaletten > f.EpStellplaetze)
            return "zu wenig Palettenplätze";
        return null;
    }

    /// <summary>
    /// Grobe Prüfung der Führerscheinklasse: 40 t braucht CE, 12–26 t mindestens C, 7,5 t mindestens C1.
    /// null = passt, sonst eine Meldung für die Oberfläche.
    /// </summary>
    public static string? KlassePasstNicht(FreierFahrer fahrer, FreiesFahrzeug fahrzeug)
    {
        string[] klassen = fahrer.Fuehrerscheinklasse.ToUpperInvariant()
            .Split(new[] { ',', ' ', ';', '/' }, StringSplitOptions.RemoveEmptyEntries);
        bool Hat(params string[] erlaubt) => klassen.Any(klasse => erlaubt.Contains(klasse));
        bool ok = fahrzeug.Fahrzeugtyp switch
        {
            "40 t" => Hat("CE"),
            "26 t" or "18 t" or "12 t" => Hat("C", "CE"),
            "7,5 t" => Hat("C1", "C1E", "C", "CE"),
            _ => true
        };
        return ok ? null
            : $"{fahrer.Name} hat Führerscheinklasse {fahrer.Fuehrerscheinklasse} – für ein Fahrzeug mit {fahrzeug.Fahrzeugtyp} reicht das nicht.";
    }
}
