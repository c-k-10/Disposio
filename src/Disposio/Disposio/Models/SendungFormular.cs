using System;
using System.Collections.Generic;

namespace Disposio.Models;

/// <summary>Ein Kunde der Firma (public.kunde) für die Auswahlliste in D03.</summary>
public sealed record KundeAuswahl(long Id, string Kundennummer, string Firmenname)
{
    public string Anzeige => $"{Kundennummer} · {Firmenname}";
}

/// <summary>Ein neuer Kunde, der zusammen mit der Sendung angelegt wird. Alle Felder sind in public.kunde Pflicht.</summary>
public sealed record NeuerKunde(
    string Kundennummer,
    string Firmenname,
    string Ansprechpartner,
    string Adresse,
    string Plz,
    string Ort,
    string Telefon,
    string Email);

/// <summary>
/// Die Eingaben aus D03 (Sendung erfassen / bearbeiten). Entweder KundeId (bestehender Kunde)
/// oder NeuerKunde ist gesetzt. Maße gelten je Einheit, das Gewicht für die ganze Sendung.
/// </summary>
public sealed record SendungEntwurf(
    string Auftragsnummer,
    string? Kundenreferenz,
    long? KundeId,
    NeuerKunde? NeuerKunde,
    string Abholadresse,
    DateOnly Abholdatum,
    TimeOnly AbholfensterVon,
    TimeOnly AbholfensterBis,
    string Lieferadresse,
    DateOnly Lieferdatum,
    TimeOnly LieferfensterVon,
    TimeOnly LieferfensterBis,
    string Ansprechpartner,
    bool AvisierungErforderlich,
    string Gutart,
    string Verpackungstyp,
    int Anzahl,
    decimal GewichtKg,
    decimal LaengeM,
    decimal BreiteM,
    decimal HoeheM,
    decimal? TemperaturMinC,
    decimal? TemperaturMaxC,
    decimal Warenwert,
    bool Stapelbar,
    bool Drehbar,
    bool Gefahrgut,
    bool Versicherung,
    bool Ladehilfsmitteltausch,
    bool Zollrelevanz,
    string LadeEntladeequipment,
    string Prioritaet,
    string Status);

/// <summary>Alles, was D03 zum Start braucht. Beim Bearbeiten ist Vorhanden gesetzt.</summary>
public sealed record SendungFormularDaten(
    IReadOnlyList<KundeAuswahl> Kunden,
    string AuftragsnummerVorschlag,
    string KundennummerVorschlag,
    SendungEntwurf? Vorhanden,
    string? Tournummer);

/// <summary>Startwert für D02 nach dem Speichern in D03: Tag der Abholung und eine Erfolgsmeldung.</summary>
public sealed record SendungGespeichert(DateOnly Tag, string Meldung);

/// <summary>Auswahllisten des Sendungsformulars. Sie stehen nur an dieser einen Stelle.</summary>
public static class SendungWerte
{
    public const string StatusOffen = "Offen";

    // Gleiche Werte wie beim Fahrzeugaufbau (FahrzeugWerte.Aufbauten): „Frische“ ist gekühlt.
    public const string GutartFrische = "Frische";
    public static readonly IReadOnlyList<string> Gutarten = new[] { "Trockengut", GutartFrische };

    public const string Europalette = "Europalette";
    public static readonly IReadOnlyList<string> Verpackungstypen =
        new[] { Europalette, "Einwegpalette", "Gitterbox", "Karton", "Sonstiges" };

    public static readonly IReadOnlyList<string> Prioritaeten = new[] { "Niedrig", "Normal", "Hoch", "Express" };
    public const string PrioritaetStandard = "Normal";

    public static readonly IReadOnlyList<string> Equipment =
        new[] { "Keines", "Hebebühne", "Stapler vor Ort", "Mitnahmestapler", "Hubwagen" };
}
