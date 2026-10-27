using System.Collections.Generic;
using System.Linq;

namespace Disposio.Models;

/// <summary>
/// Eine Ansicht aus dem Wireframe-Katalog. Menüname ist leer, wenn die Ansicht nur aus
/// einer anderen heraus geöffnet wird (zum Beispiel K02 aus K01). Oberansicht nennt dann diese.
/// </summary>
public sealed record AnsichtInfo(string Id, string? Menuename, string Titel, string Zweck,
    FirmenRolle Rolle, string? Oberansicht = null);

/// <summary>
/// Alle Ansichten der Firmenkonten mit ihren Wireframe-IDs. Die Anmeldeseiten S01–S03
/// gehören allen Rollen und stehen deshalb nicht in dieser Liste.
/// </summary>
public static class AnsichtenKatalog
{
    public static readonly IReadOnlyList<AnsichtInfo> Alle = new AnsichtInfo[]
    {
        // Kundenadmin
        new("K01", "Benutzer", "Benutzer verwalten", "Konten des eigenen Unternehmens suchen und verwalten.", FirmenRolle.Kundenadmin),
        new("K02", null, "Benutzer anlegen / bearbeiten", "Alle Kontodaten, Rolle und Abteilungszugriff erfassen.", FirmenRolle.Kundenadmin, "K01"),
        new("K06", "Abteilungen", "Abteilungen verwalten", "Bereiche des Unternehmens anlegen, umbenennen und deaktivieren.", FirmenRolle.Kundenadmin),
        new("K03", "Rollen & Rechte", "Rollen & Rechte", "Zugriffsbereiche verständlich erklären.", FirmenRolle.Kundenadmin),
        new("K04", "Passwort-Richtlinien", "Passwort-Richtlinien", "Unternehmensweite Anmeldevorgaben konfigurieren.", FirmenRolle.Kundenadmin),
        new("K05", "Aktionsprotokoll", "Aktionsprotokoll", "Nachvollziehen, wer im Unternehmen wann was geändert hat.", FirmenRolle.Kundenadmin),

        // Inhaber / Geschäftsführer
        new("G01", "Unternehmensübersicht", "Unternehmensübersicht", "Unternehmensweite Leistung und auffällige Bereiche erkennen.", FirmenRolle.Inhaber),
        new("G02", "Kennzahlen & Berichte", "Kennzahlen & Statistik", "Berichte nach Zeitraum und Abteilung vergleichen.", FirmenRolle.Inhaber),

        // Abteilungsleiter
        new("L01", "Abteilungsübersicht", "Meine Abteilung", "Fahrzeuge und Meldungen der eigenen Abteilung überblicken.", FirmenRolle.Abteilungsleiter),
        new("L05", "Kennzahlen", "Kennzahlen der Abteilung", "Leistung der eigenen Abteilung nach Zeitraum auswerten.", FirmenRolle.Abteilungsleiter),
        new("L02", "Fahrzeuge", "Fahrzeuge der Abteilung", "Fahrzeugdaten im eigenen Bereich bearbeiten.", FirmenRolle.Abteilungsleiter),
        new("L04", null, "Fahrzeug bearbeiten · Abteilung", "Kapazitäten und Verfügbarkeit im eigenen Bereich pflegen.", FirmenRolle.Abteilungsleiter, "L02"),
        new("L03", "Meldungen bearbeiten", "Tankvorgang / Schaden prüfen", "Meldungen fachlich prüfen, Rückfragen stellen und Bearbeitung dokumentieren.", FirmenRolle.Abteilungsleiter),
        new("L06", "Aktionsprotokoll", "Aktionsprotokoll", "Entscheidungen, Statusänderungen und Versandstatus der Abteilung nachvollziehen.", FirmenRolle.Abteilungsleiter),

        // Fuhrparkmanager
        new("F01", "Fuhrpark", "Fuhrparkübersicht", "Verfügbarkeit und Kapazitäten aller Unternehmensfahrzeuge sehen.", FirmenRolle.Fuhrparkmanager),
        new("F02", null, "Fahrzeug anlegen / bearbeiten", "Tatsächliche Maße, Nutzlast und Stellplätze getrennt erfassen.", FirmenRolle.Fuhrparkmanager, "F01"),
        new("F03", "Fahrerzuordnung", "Fahrer – Fahrzeug zuweisen", "Zuweisungen zeitlich planen und Überschneidungen erkennen.", FirmenRolle.Fuhrparkmanager),
        new("F04", "Wartung & Dokumente", "Wartung & Dokumente", "Fällige Wartungen und auslaufende Fahrzeugdokumente überwachen.", FirmenRolle.Fuhrparkmanager),
        new("F05", null, "Wartung / Dokument erfassen", "Frist, Kilometerstand, Nachweis und Verfügbarkeit festhalten.", FirmenRolle.Fuhrparkmanager, "F04"),

        // Disponent
        new("D01", "Tagesübersicht", "Tagesübersicht", "Schichtbeginn: Nachtgeschehen und Handlungsbedarf erfassen.", FirmenRolle.Disponent),
        new("D02", "Sendungen", "Sendungen", "Aufträge durchsuchen, erfassen und zur Planung auswählen.", FirmenRolle.Disponent),
        new("D03", null, "Sendung erfassen / bearbeiten", "Kundendaten, Ladegut und beide Transportorte vollständig erfassen.", FirmenRolle.Disponent, "D02"),
        new("D13", null, "Verplanungsvorschlag prüfen", "Sendungen anhand von Route, Zeitfenster und Ladekapazität zuordnen.", FirmenRolle.Disponent, "D02"),
        new("D04", "Touren", "Tourenübersicht", "Touren suchen, planen und deren Bearbeitungsstand erkennen.", FirmenRolle.Disponent),
        new("D05", null, "Tour planen", "Sendungen und Stoppreihenfolge zu einer Tour zusammenstellen.", FirmenRolle.Disponent, "D04"),
        new("D06", null, "Tour zuweisen & freigeben", "Passende Fahrer und Fahrzeuge auswählen und Konflikte prüfen.", FirmenRolle.Disponent, "D04"),
        new("D07", null, "Tourdetails", "Status, Stopps, Fahrzeug und Verlauf an einem Ort.", FirmenRolle.Disponent, "D04"),
        new("D08", "Live-Karte", "Live-Karte & Verkehr", "Fahrzeuge und voraussichtliche Ankunft mit Datenalter verfolgen.", FirmenRolle.Disponent),
        new("D09", "Nachrichten", "Nachrichten mit Fahrer", "Tourbezogen schreiben und Übersetzung mit Original prüfen.", FirmenRolle.Disponent),
        new("D10", "Kundenkommunikation", "Kundenkommunikation", "Verspätungsentwürfe bearbeiten, freigeben oder mit Grund verwerfen.", FirmenRolle.Disponent),
        new("D11", "Frachtdokumente", "Frachtdokumente & Abliefernachweise", "Lieferschein, Foto, Signatur und Problemmeldungen je Sendung verwalten.", FirmenRolle.Disponent),
        new("D15", "Meldungen", "Meldungen bearbeiten", "Tank- und Schadensmeldungen sichten und weiterbearbeiten.", FirmenRolle.Disponent),
    };

    public static AnsichtInfo? Finden(string id) => Alle.FirstOrDefault(ansicht => ansicht.Id == id);

    /// <summary>Menüpunkte einer Rolle in der Reihenfolge der Liste oben.</summary>
    public static IReadOnlyList<AnsichtInfo> Menue(FirmenRolle rolle) =>
        Alle.Where(ansicht => ansicht.Rolle == rolle && ansicht.Menuename is not null).ToArray();

    /// <summary>Ansichten, die aus der angegebenen Ansicht heraus geöffnet werden.</summary>
    public static IReadOnlyList<AnsichtInfo> Unteransichten(string id) =>
        Alle.Where(ansicht => ansicht.Oberansicht == id).ToArray();
}
