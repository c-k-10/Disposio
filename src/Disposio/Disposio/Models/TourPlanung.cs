using System;
using System.Collections.Generic;

namespace Disposio.Models;

/// <summary>Ein Fahrer aus public.fahrer (mit Namen aus public.benutzer) für Auswahllisten.</summary>
public sealed record FahrerAuswahl(long FahrerId, string Name, string Verfuegbarkeit)
{
    public string Anzeige => string.IsNullOrWhiteSpace(Verfuegbarkeit) ? Name : $"{Name} · {Verfuegbarkeit}";
}

/// <summary>Ein Fahrzeug der Firma für Auswahllisten.</summary>
public sealed record FahrzeugAuswahl(long Id, string Kennzeichen, string Fahrzeugtyp, string Status)
{
    public string Anzeige => $"{Kennzeichen} · {Fahrzeugtyp} · {Status}";
}

/// <summary>Eine Sendung (public.auftrag), die noch keiner Tour zugeordnet ist.</summary>
public sealed record OffeneSendung(
    long Id,
    string Auftragsnummer,
    string Kunde,
    string Abholadresse,
    string Lieferadresse,
    DateOnly Abholdatum,
    TimeOnly AbholfensterVon,
    TimeOnly AbholfensterBis,
    DateOnly Lieferdatum,
    TimeOnly LieferfensterVon,
    TimeOnly LieferfensterBis,
    string Prioritaet);

/// <summary>Alles, was das Formular „Tour planen“ (D05) zum Start braucht.</summary>
public sealed record TourPlanungsdaten(
    IReadOnlyList<AbteilungAuswahl> Abteilungen,
    IReadOnlyList<FahrerAuswahl> Fahrer,
    IReadOnlyList<FahrzeugAuswahl> Fahrzeuge,
    IReadOnlyList<OffeneSendung> Sendungen,
    string TournummerVorschlag);

/// <summary>
/// Ein Stopp im Formular. Von und Bis sind deutsche Ortszeit. Stopps aus einer Sendung haben
/// AuftragId und Vorgang („Abholung“ oder „Zustellung“), eigene Stopps nicht.
/// </summary>
public sealed record TourStoppEntwurf(
    string Art,
    string Bezeichnung,
    string Adresse,
    DateTime Von,
    DateTime Bis,
    long? AuftragId,
    string? Vorgang);

/// <summary>Die Eingaben aus D05. Start und Ende sind deutsche Ortszeit. Die Reihenfolge der Stopps zählt.</summary>
public sealed record TourEntwurf(
    string Tournummer,
    long AbteilungId,
    DateTime Start,
    DateTime Ende,
    string Startort,
    string Zielort,
    long? FahrerId,
    long? FahrzeugId,
    IReadOnlyList<TourStoppEntwurf> Stopps);

/// <summary>Feste Werte für Touren. Die Datenbank erlaubt für tourstopp.art nur diese drei.</summary>
public static class TourWerte
{
    public const string StatusGeplant = "Geplant";
    public const string StatusZugewiesen = "Zugewiesen";
    public const string StoppStatusOffen = "Offen";

    public const string ArtAbholung = "Abholung";
    public const string ArtZustellung = "Zustellung";
    public const string ArtKombiniert = "Kombiniert";
    public static readonly IReadOnlyList<string> StoppArten = new[] { ArtAbholung, ArtZustellung, ArtKombiniert };
}

/// <summary>
/// Startwert für D05 aus der Sendungsliste (D02, „Manuell verplanen“): Tag der Tour und die Sendungen,
/// die im Formular schon angehakt sein sollen.
/// </summary>
public sealed record TourPlanenStart(DateOnly Tag, IReadOnlyList<long> SendungIds);

/// <summary>Startwert für D04 nach dem Speichern in D05: Tag der neuen Tour und eine Erfolgsmeldung.</summary>
public sealed record TourGespeichert(DateOnly Tag, string Meldung);
