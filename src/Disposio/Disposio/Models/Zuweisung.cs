using System;
using System.Collections.Generic;

namespace Disposio.Models;

/// <summary>Die Sendung, die in D16 einem Fahrer und Fahrzeug zugewiesen wird. Lademeter wie in D02 berechnet.</summary>
public sealed record ZuweisungSendung(
    long Id,
    string Auftragsnummer,
    string Referenz,
    string Kunde,
    string Abholadresse,
    DateOnly Abholdatum,
    TimeOnly AbholfensterVon,
    TimeOnly AbholfensterBis,
    string Lieferadresse,
    DateOnly Lieferdatum,
    TimeOnly LieferfensterVon,
    TimeOnly LieferfensterBis,
    string Gutart,
    string Verpackungstyp,
    int Anzahl,
    decimal GewichtKg,
    decimal Lademeter,
    decimal? TemperaturMinC,
    decimal? TemperaturMaxC,
    bool Gefahrgut,
    string Prioritaet,
    string? Tournummer)
{
    /// <summary>Zeitraum der Sendung: Beginn des Abholfensters bis Ende des Lieferfensters (deutsche Ortszeit).</summary>
    public DateTime Von => Abholdatum.ToDateTime(AbholfensterVon);
    public DateTime Bis => Lieferdatum.ToDateTime(LieferfensterBis);
}

/// <summary>Ein Fahrer, der im Zeitraum der Sendung keine andere Tour hat und verfügbar ist.</summary>
public sealed record FreierFahrer(long FahrerId, string Name, string Fuehrerscheinklasse, string Verfuegbarkeit, string? Abteilung);

/// <summary>Ein Fahrzeug, das im Zeitraum der Sendung keine andere Tour hat und nicht in Wartung oder gesperrt ist.</summary>
public sealed record FreiesFahrzeug(
    long Id,
    string Kennzeichen,
    string Fahrzeugtyp,
    string Aufbau,
    decimal NutzlastKg,
    decimal NutzbareLademeter,
    int EpStellplaetze,
    string Status,
    string? Abteilung);

/// <summary>Alles, was D16 „Sendung zuweisen“ braucht.</summary>
public sealed record ZuweisungDaten(
    ZuweisungSendung Sendung,
    IReadOnlyList<FreierFahrer> Fahrer,
    IReadOnlyList<FreiesFahrzeug> Fahrzeuge,
    IReadOnlyList<AbteilungAuswahl> Abteilungen,
    string TournummerVorschlag);

/// <summary>
/// Die Tour in D06 „Tour zuweisen“ mit der zusammengerechneten Ladung ihrer Sendungen.
/// Start und Ende sind deutsche Ortszeit. FahrerId/FahrzeugId sind die bisherige Zuordnung (oder null).
/// </summary>
public sealed record TourZuweisungKopf(
    long Id,
    string Tournummer,
    string? Bereich,
    string Status,
    bool IstOffen,
    string Startort,
    string Zielort,
    DateTime Start,
    DateTime Ende,
    long? FahrerId,
    long? FahrzeugId,
    int Sendungen,
    decimal GewichtKg,
    decimal Lademeter,
    int Europaletten,
    bool Gekuehlt);

/// <summary>Ein Stopp der Tour, fertig formatiert für die Anzeige.</summary>
public sealed record TourStoppKurz(int Reihenfolge, string Art, string Bezeichnung, string Zeitfenster);

/// <summary>Alles, was D06 braucht: Tour, Stopps und die im Tourzeitraum freien Fahrer und Fahrzeuge.</summary>
public sealed record TourZuweisungDaten(
    TourZuweisungKopf Tour,
    IReadOnlyList<TourStoppKurz> Stopps,
    IReadOnlyList<FreierFahrer> Fahrer,
    IReadOnlyList<FreiesFahrzeug> Fahrzeuge);

/// <summary>
/// Startwert für D16 aus D02, wenn ein Fahrer oder Fahrzeug per Drag &amp; Drop auf eine Sendung gezogen wurde.
/// D16 wählt die beiden dann schon aus, sofern sie im Zeitraum der Sendung frei sind und passen.
/// </summary>
public sealed record SendungZuweisenStart(long AuftragId, long? FahrerId, long? FahrzeugId);

/// <summary>Ein Fahrer in der Spalte „Verfügbar am …“ (D02) mit seinen Touren an diesem Tag. Touren null = ganz frei.</summary>
public sealed record TagesFahrer(FreierFahrer Fahrer, string? Touren);

/// <summary>Ein Fahrzeug in der Spalte „Verfügbar am …“ (D02) mit seinen Touren an diesem Tag. Touren null = ganz frei.</summary>
public sealed record TagesFahrzeug(FreiesFahrzeug Fahrzeug, string? Touren);

/// <summary>Fahrer und Fahrzeuge, die an einem Tag einsetzbar sind (nicht krank/Urlaub, nicht Wartung/gesperrt).</summary>
public sealed record TagesVerfuegbarkeit(IReadOnlyList<TagesFahrer> Fahrer, IReadOnlyList<TagesFahrzeug> Fahrzeuge);
