using System;
using System.Collections.Generic;

namespace Disposio.Models;

/// <summary>
/// Die Eingaben aus dem Fahrzeugformular (F02). Zahlen sind schon umgewandelt; geprüft werden sie
/// im Formular und noch einmal im Repository, bevor sie an die Datenbank gehen.
/// SolltemperaturC ist nur bei gekühltem Aufbau („Frische“) gesetzt, AbteilungId ist optional.
/// </summary>
public sealed record FahrzeugEntwurf(
    string Kennzeichen,
    string Fahrzeugtyp,
    string Hersteller,
    string Modell,
    int Baujahr,
    int Kilometerstand,
    string Status,
    DateOnly TuevDatum,
    string Aufbau,
    decimal NutzlastKg,
    decimal NutzbareLaengeM,
    decimal InnenbreiteM,
    decimal InnenhoeheM,
    decimal NutzbareLademeter,
    int EpStellplaetze,
    decimal? SolltemperaturC,
    long? AbteilungId);

/// <summary>Ein gespeichertes Fahrzeug aus public.fahrzeug, wie es die Fahrzeugliste (F01) anzeigt.</summary>
public sealed record Fahrzeug(
    long Id,
    string Kennzeichen,
    string Fahrzeugtyp,
    string Hersteller,
    string Modell,
    int Baujahr,
    int Kilometerstand,
    string Status,
    DateOnly TuevDatum,
    string Aufbau,
    decimal NutzlastKg,
    decimal NutzbareLademeter,
    int EpStellplaetze,
    decimal? SolltemperaturC,
    string? Abteilung);

/// <summary>
/// Die festen Auswahllisten des Fahrzeugformulars. Sie folgen dem Wireframe F02
/// (Fahrzeugklasse, Aufbau, Status) und stehen nur an dieser einen Stelle.
/// </summary>
public static class FahrzeugWerte
{
    public static readonly IReadOnlyList<string> Fahrzeugtypen = new[] { "40 t", "26 t", "18 t", "12 t", "7,5 t", "3,5 t" };

    // „Frische“ ist ein gekühlter Aufbau und braucht deshalb eine Solltemperatur.
    public const string Kuehlaufbau = "Frische";
    public static readonly IReadOnlyList<string> Aufbauten = new[] { "Trockengut", Kuehlaufbau };

    // „Im Einsatz“ setzt später die Tourenplanung. Ein neues Fahrzeug startet in einem dieser Zustände.
    public static readonly IReadOnlyList<string> StatusBeimAnlegen = new[] { "Verfügbar", "Wartung", "Gesperrt" };

    /// <summary>
    /// Einheitliche Schreibweise für Kennzeichen: Großbuchstaben, keine äußeren Leerzeichen,
    /// keine doppelten Leerzeichen. So gelten „h-fc 402“ und „H-FC  402“ als dasselbe Kennzeichen.
    /// </summary>
    public static string KennzeichenVereinheitlichen(string? kennzeichen) =>
        string.Join(' ', (kennzeichen ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpperInvariant();
}
