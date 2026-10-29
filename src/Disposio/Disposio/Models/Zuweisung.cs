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
