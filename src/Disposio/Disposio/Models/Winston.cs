using System;
using System.Collections.Generic;

namespace Disposio.Models;

/// <summary>
/// Fehler von Winston (KI-Co-Disponent) mit einem Text, der so in der Oberfläche angezeigt werden kann:
/// Schlüssel fehlt, keine Verbindung, Guthaben leer … Enthält nie den Schlüssel oder technische Details.
/// </summary>
public sealed class WinstonException : Exception
{
    public WinstonException(string message) : base(message) { }
}

/// <summary>Die Verkehrslage konnte nicht geladen werden (TomTom nicht erreichbar, Kontingent, keine Koordinaten).</summary>
public sealed class VerkehrException : Exception
{
    public VerkehrException(string message) : base(message) { }
}

/// <summary>Ein Punkt der Reststrecke einer Tour: aktuelle Position oder ein offener Stopp.</summary>
public sealed record RoutenPunkt(double Breite, double Laenge, string Bezeichnung);

/// <summary>
/// Eine Störung von TomTom, die die Reststrecke einer Tour betrifft.
/// </summary>
/// <param name="Art">Stau, Unfall, Baustelle, Sperrung, Fahrstreifen gesperrt oder Störung.</param>
/// <param name="AufAktuellerRoute">
/// true = liegt auf der gerade schnellsten Route (kostet Zeit). false = Sperrung auf der sonst üblichen Route,
/// die deshalb umfahren wird.
/// </param>
public sealed record Verkehrsstoerung(
    string Art,
    string Beschreibung,
    string? Strasse,
    string? Von,
    string? Nach,
    int VerzoegerungMinuten,
    int LaengeMeter,
    bool AufAktuellerRoute);

/// <summary>
/// Verkehrslage auf der Reststrecke einer Tour (aktuelle Position → alle offenen Stopps), berechnet mit TomTom.
/// </summary>
/// <param name="Kilometer">Länge der gerade schnellsten Route.</param>
/// <param name="FahrzeitMinuten">Fahrzeit mit aktuellem Verkehr.</param>
/// <param name="VerzoegerungMinuten">Davon Zeitverlust durch Staus und Störungen.</param>
/// <param name="UeblicheKilometer">Länge der Route ohne Verkehr (zum Erkennen einer Umleitung).</param>
/// <param name="UeblicheFahrzeitMinuten">Fahrzeit der üblichen Route ohne Verkehr.</param>
/// <param name="Vollstaendig">false = Die Strecke war zu lang, Sperrungen wurden nur für einen Teil geprüft.</param>
public sealed record RoutenVerkehr(
    double Kilometer,
    int FahrzeitMinuten,
    int VerzoegerungMinuten,
    double UeblicheKilometer,
    int UeblicheFahrzeitMinuten,
    IReadOnlyList<Verkehrsstoerung> Stoerungen,
    bool Vollstaendig);

/// <summary>
/// Was Winston (Claude) zu einer Verspätung entwirft. Der Disponent prüft alles in D10 und gibt frei oder verwirft.
/// </summary>
/// <param name="Begruendung">Lage, Ursache und Empfehlung für den Disponenten (Du-Form).</param>
/// <param name="Kundentext">E-Mail an den Kunden (Sie-Form, ohne interne Nummern).</param>
/// <param name="Fahrertext">Kurze Nachricht an den Fahrer auf Deutsch (Sie-Form). Übersetzt wird danach wie bisher.</param>
public sealed record VerspaetungsEntwurf(string Begruendung, string Kundentext, string Fahrertext);
