using System;

namespace Disposio.Models;

/// <summary>
/// Ein offener Verspätungsvorschlag von „Winston“ (public.winston_vorschlag) für D10 „Kundenkommunikation“.
/// Uhrzeiten sind schon als Text in deutscher Zeit formatiert.
/// </summary>
public sealed record Verspaetungsvorschlag(
    long Id,
    long TourId,
    string Tournummer,
    string Stopp,
    string Kunde,
    string Auftragsnummer,
    string EtaSnapshot,
    string Fensterende,
    string Begruendung,
    string Kundentext,
    string Kundenempfaenger,
    string FahrertextOriginal,
    string? FahrertextUebersetzt,
    string Fahrersprache,
    string FahrerName,
    int Textversion,
    string? EtaBerechnetAm,
    string? AktuelleEta,
    int AbweichungMinuten);

/// <summary>Was der Disponent vor „Freigeben &amp; senden“ eingetragen hat.</summary>
public sealed record VorschlagFreigabe(
    long Id,
    int Textversion,
    string Kundenempfaenger,
    string Betreff,
    string Kundentext,
    string FahrertextDeutsch,
    string? FahrertextZiel,
    string Fahrersprache);

/// <summary>
/// Werte von winston_vorschlag.status. „veraltet“: Die Verspätung besteht nicht mehr (Stopp erledigt oder
/// ETA wieder im Fenster), bevor jemand entschieden hat.
/// </summary>
public static class VorschlagStatus
{
    public const string Offen = "offen";
    public const string Freigegeben = "freigegeben";
    public const string Verworfen = "verworfen";
    public const string Veraltet = "veraltet";
}
