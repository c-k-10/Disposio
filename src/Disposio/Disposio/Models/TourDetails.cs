using System.Collections.Generic;

namespace Disposio.Models;

/// <summary>
/// Kopf der Tourdetails (D07). Zeiten sind schon als Text in deutscher Ortszeit formatiert.
/// Die Ladung ist über alle Sendungen der Tour zusammengerechnet.
/// </summary>
public sealed record TourDetailKopf(
    long Id,
    string Tournummer,
    string? Bereich,
    string Status,
    string? Fahrer,
    string? Kennzeichen,
    string? Fahrzeugtyp,
    string? Aufbau,
    string Zeitraum,
    string Startort,
    string Zielort,
    int Sendungen,
    int Plaetze,
    decimal Lademeter,
    decimal GewichtKg);

/// <summary>Ein Stopp der Tour mit Ist-Zeiten, ETA und Verspätung in Minuten (0 = pünktlich).</summary>
public sealed record TourDetailStopp(
    long Id,
    int Reihenfolge,
    string Art,
    string Bezeichnung,
    string Adresse,
    string? Kunden,
    string Datum,
    string FensterVon,
    string FensterBis,
    string? IstAnkunft,
    string? IstAbfahrt,
    string? Eta,
    int VerspaetungMinuten,
    string Status,
    decimal? Breitengrad,
    decimal? Laengengrad);

public sealed record TourDetails(TourDetailKopf Tour, IReadOnlyList<TourDetailStopp> Stopps);

/// <summary>Startwert für die Live-Karte (D08), wenn D07 „Auf Karte zeigen“ öffnet: Stopps mit Koordinaten.</summary>
public sealed record TourKarte(string Tournummer, IReadOnlyList<TourKartenStopp> Stopps);

public sealed record TourKartenStopp(int Nummer, string Bezeichnung, string Zeitfenster, bool Erledigt, double Breite, double Laenge);
