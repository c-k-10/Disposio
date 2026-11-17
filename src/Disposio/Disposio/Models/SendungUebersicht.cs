using System;

namespace Disposio.Models;

/// <summary>
/// Eine Zeile der Sendungsliste (D02): ein Auftrag aus public.auftrag mit Kundenname.
/// Lademeter = Anzahl × Länge × Breite / 2,4 m (Ladefläche eines Sattelaufliegers), auf 0,1 gerundet.
/// </summary>
public sealed record SendungUebersicht(
    long Id,
    string Auftragsnummer,
    string? Kundenreferenz,
    string Kunde,
    string Abholadresse,
    string Lieferadresse,
    DateOnly Abholdatum,
    DateOnly Lieferdatum,
    string LieferfensterVon,
    string LieferfensterBis,
    string Gutart,
    int Anzahl,
    decimal Lademeter,
    string Status,
    string Prioritaet,
    string? Tournummer)
{
    public bool IstVerplant => Tournummer is not null;

    /// <summary>Fahrer und Kennzeichen der Tour, in der die Sendung verplant ist (sonst null).</summary>
    public string? Fahrer { get; init; }
    public string? Kennzeichen { get; init; }

    /// <summary>Tour der Sendung mit Fahrer-/Fahrzeug-Id. Bearbeitbar = „Geplant“/„Zugewiesen“ und noch nicht losgefahren.</summary>
    public long? TourId { get; init; }
    public long? FahrerId { get; init; }
    public long? FahrzeugId { get; init; }
    public bool TourBearbeitbar { get; init; }
    public string? TourStatus { get; init; }

    /// <summary>Die Referenz, die der Kunde kennt. Ohne Kundenreferenz die eigene Auftragsnummer.</summary>
    public string Referenz => string.IsNullOrWhiteSpace(Kundenreferenz) ? Auftragsnummer : Kundenreferenz!;
}

/// <summary>Kleine Helfer für Adressen in der Form „Straße Nr., PLZ Ort“.</summary>
public static class Adressen
{
    /// <summary>„Hafenstr. 1, 29221 Celle“ → „Celle“. Ohne Komma bleibt die ganze Adresse.</summary>
    public static string Ort(string adresse)
    {
        string teil = adresse.Split(',')[^1].Trim();
        int start = 0;
        while (start < teil.Length && (char.IsDigit(teil[start]) || teil[start] == ' '))
            start++;
        string ort = teil[start..].Trim();
        return ort.Length > 0 ? ort : adresse.Trim();
    }
}
