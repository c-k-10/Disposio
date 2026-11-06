using System.Collections.Generic;

namespace Disposio.Models;

/// <summary>
/// Neueste Position einer aktiven Tour (Status Unterwegs oder Pausiert) für die Live-Karte (D08).
/// Die Positionen sendet die Fahrer-App über die Disposio.Api in die Tabelle gps_position.
/// </summary>
/// <param name="FahrerSchluessel">Schlüssel wie „Fahrer:4“ (siehe KartenObjekt), damit die Suche links den Marker findet.</param>
/// <param name="FahrzeugSchluessel">Schlüssel wie „Fahrzeug:12“, oder null ohne Fahrzeug.</param>
/// <param name="Stand">Uhrzeit des Punkts (deutsche Zeit), z. B. „14:35“.</param>
/// <param name="AlterSekunden">Wie alt der Punkt ist. Über 5 Minuten gilt er als veraltet.</param>
public sealed record LivePosition(
    long TourId,
    string Tournummer,
    string Status,
    string Fahrer,
    string? FahrerSchluessel,
    string? Kennzeichen,
    string? FahrzeugSchluessel,
    double Breite,
    double Laenge,
    string Stand,
    long AlterSekunden,
    IReadOnlyList<LiveStopp> Stopps)
{
    public const string StatusUnterwegs = "Unterwegs";
    public const string StatusPausiert = "Pausiert";

    public bool Pausiert => Status == StatusPausiert;

    public bool Veraltet => AlterSekunden > 5 * 60;
}

/// <summary>Ein Stopp der Tour mit der ETA aus TomTom (tourstopp.aktuelle_eta), Zeiten als „HH:mm“.</summary>
public sealed record LiveStopp(
    int Reihenfolge,
    string Bezeichnung,
    string Status,
    string? Eta,
    string ZeitfensterBis,
    bool Verspaetet);
