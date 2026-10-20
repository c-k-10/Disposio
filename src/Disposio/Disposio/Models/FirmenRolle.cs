using System;
using System.Collections.Generic;
using System.Linq;

namespace Disposio.Models;

/// <summary>
/// Die Rollen innerhalb eines Kundenunternehmens. Die Plattformrolle SuperKrasserAdmin
/// gehört bewusst nicht dazu: Sie gilt nur für die Projektbesitzer ohne Firma.
/// </summary>
public enum FirmenRolle
{
    Disponent,
    Fuhrparkmanager,
    Abteilungsleiter,
    Inhaber,
    Kundenadmin,
    Fahrer
}

/// <summary>
/// Namen und Aufgaben der Firmenrollen. Die Rollen selbst stehen bereits in public.rolle;
/// hier wird nur festgelegt, wie die App sie erkennt und anzeigt.
/// </summary>
public static class FirmenRollenKatalog
{
    // Reihenfolge im Menü: Wer mehrere Rollen hat, startet bei der täglichen Arbeit.
    // Fahrer stehen am Ende, weil sie am PC keinen eigenen Arbeitsbereich haben.
    public static readonly IReadOnlyList<FirmenRolle> Alle = Enum.GetValues<FirmenRolle>();

    /// <summary>Spalte bezeichnung in public.rolle. Sie wird exakt verglichen.</summary>
    public static string DatenbankName(FirmenRolle rolle) => rolle switch
    {
        FirmenRolle.Disponent => "Disponent",
        FirmenRolle.Fuhrparkmanager => "Fuhrparkmanager",
        FirmenRolle.Abteilungsleiter => "Abteilungsleiter",
        FirmenRolle.Inhaber => "Inhaber/Geschäftsführer",
        FirmenRolle.Kundenadmin => "Kundenadmin",
        FirmenRolle.Fahrer => "Fahrer",
        _ => throw new ArgumentOutOfRangeException(nameof(rolle))
    };

    public static string Bezeichnung(FirmenRolle rolle) =>
        rolle == FirmenRolle.Inhaber ? "Inhaber / Geschäftsführer" : DatenbankName(rolle);

    public static string Bereich(FirmenRolle rolle) => rolle switch
    {
        FirmenRolle.Abteilungsleiter => "Eigene Abteilung",
        FirmenRolle.Fahrer => "Eigene zugewiesene Touren",
        _ => "Eigenes Unternehmen"
    };

    public static string Aufgaben(FirmenRolle rolle) => rolle switch
    {
        FirmenRolle.Disponent => "Sendungen, Touren, Karte, Kommunikation, Dokumente",
        FirmenRolle.Fuhrparkmanager => "Fahrzeuge, Fahrerzuordnung, Wartung, Dokumente",
        FirmenRolle.Abteilungsleiter => "Übersicht, Kennzahlen, Fahrzeuge bearbeiten (nicht anlegen), Meldungen, Aktionsprotokoll",
        FirmenRolle.Inhaber => "Kennzahlen und Berichte lesen",
        FirmenRolle.Kundenadmin => "Benutzer und Passwort-Richtlinien",
        FirmenRolle.Fahrer => "Status, GPS, Chat, Belege, Tank- und Schadensmeldungen in der Fahrer-App",
        _ => throw new ArgumentOutOfRangeException(nameof(rolle))
    };

    /// <summary>
    /// Wandelt die Rollennamen aus der Datenbank in Firmenrollen um. Unbekannte Namen
    /// (zum Beispiel die Plattformrolle) werden ignoriert und erteilen keine Rechte.
    /// </summary>
    public static IReadOnlyList<FirmenRolle> AusDatenbank(IEnumerable<string> rollenNamen)
    {
        var namen = rollenNamen.ToHashSet(StringComparer.Ordinal);
        return Alle.Where(rolle => namen.Contains(DatenbankName(rolle))).ToArray();
    }
}
