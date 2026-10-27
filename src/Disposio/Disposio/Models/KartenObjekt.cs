using System;
using System.Collections.Generic;
using System.Linq;

namespace Disposio.Models;

public enum KartenObjektArt
{
    Fahrer,
    Fahrzeug
}

/// <summary>
/// Ein Fahrer oder Fahrzeug, das auf der Live-Karte (D08) gesucht und später angezeigt werden kann.
/// Positionen gibt es noch nicht: Die liefert erst die Fahrer-App.
/// </summary>
/// <param name="Name">Fahrername oder Kennzeichen.</param>
/// <param name="Details">Zweite Zeile in der Liste, zum Beispiel Abteilung oder Fahrzeugtyp.</param>
/// <param name="Suchbegriffe">Texte, in denen die Suche sucht.</param>
public sealed record KartenObjekt(
    KartenObjektArt Art,
    long Id,
    string Name,
    string Details,
    IReadOnlyList<string> Suchbegriffe)
{
    /// <summary>Eindeutiger Schlüssel, zum Beispiel „Fahrzeug:12“. Damit findet die Karte später ihren Marker.</summary>
    public string Schluessel => $"{Art}:{Id}";

    /// <summary>
    /// Passt das Objekt zur Suche? Groß-/Kleinschreibung, Leerzeichen, Bindestriche und Punkte zählen nicht.
    /// So findet „nab123“ auch das Kennzeichen „N-AB 123“.
    /// </summary>
    public bool Passt(string? suche)
    {
        string gesucht = Vereinfachen(suche);
        return gesucht.Length == 0 || Suchbegriffe.Any(begriff => Vereinfachen(begriff).Contains(gesucht, StringComparison.Ordinal));
    }

    private static string Vereinfachen(string? text) =>
        new string((text ?? string.Empty).Where(zeichen => zeichen is not (' ' or '-' or '.')).ToArray())
            .ToLowerInvariant();
}
