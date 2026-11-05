using System;
using System.Collections.Generic;
using System.Linq;

namespace Disposio.Models;

/// <summary>
/// Ein offener Wartungstermin (status = 'Geplant') aus public.wartung. Termin ist der geplante Tag,
/// FaelligBeiKm ein optionaler Kilometerstand, ab dem die Wartung ebenfalls fällig ist.
/// AktuellerKm ist der Kilometerstand des Fahrzeugs aus public.fahrzeug.
/// </summary>
public sealed record Wartungstermin(
    long Id,
    long FahrzeugId,
    string Kennzeichen,
    string Wartungsart,
    DateOnly Termin,
    int? FaelligBeiKm,
    int AktuellerKm,
    string Bemerkung);

/// <summary>Eine durchgeführte Wartung (status = 'Erledigt') für die Historie (#91).</summary>
public sealed record DurchgefuehrteWartung(
    long Id,
    long FahrzeugId,
    string Kennzeichen,
    string Wartungsart,
    DateOnly Datum,
    int Kilometerstand,
    decimal Kosten,
    string Bemerkung,
    DateOnly? Folgetermin);

/// <summary>Ein Fahrzeug für die Auswahl in F04 (Filter) und F05 (Termin anlegen).</summary>
public sealed record WartungFahrzeug(long Id, string Kennzeichen, int Kilometerstand, bool Aktiv);

/// <summary>Alles, was F04 auf einmal lädt.</summary>
public sealed record WartungDaten(
    IReadOnlyList<Wartungstermin> OffeneTermine,
    IReadOnlyList<DurchgefuehrteWartung> Historie,
    IReadOnlyList<WartungFahrzeug> Fahrzeuge);

/// <summary>Neuer Termin aus F05 (#89).</summary>
public sealed record WartungsterminEntwurf(long FahrzeugId, string Wartungsart, DateOnly Termin, int? FaelligBeiKm, string? Bemerkung);

/// <summary>
/// Eine durchgeführte Wartung aus F05: entweder ein offener Termin wird erledigt (TerminId gesetzt) oder eine
/// Wartung ohne vorherigen Termin wird nachgetragen (TerminId null). Ist Folgetermin gesetzt, entsteht dazu
/// gleich ein neuer offener Termin derselben Wartungsart.
/// </summary>
public sealed record WartungDurchfuehrung(
    long? TerminId,
    long FahrzeugId,
    string Wartungsart,
    DateOnly Datum,
    int Kilometerstand,
    decimal Kosten,
    string? Bemerkung,
    DateOnly? Folgetermin,
    int? FolgeterminBeiKm);

/// <summary>Ampel für Wartungen (#90, #92). Die Reihenfolge ist zugleich die Dringlichkeit.</summary>
public enum WartungsAmpel
{
    Ueberfaellig = 0,
    BaldFaellig = 1,
    Geplant = 2,
    KeinTermin = 3
}

/// <summary>
/// Die festen Regeln für Wartungen an genau einer Stelle: Wartungsarten und wann ein Termin
/// „bald fällig“ oder „überfällig“ ist.
/// </summary>
public static class WartungsRegeln
{
    public const string StatusGeplant = "Geplant";
    public const string StatusErledigt = "Erledigt";

    /// <summary>Bis so viele Tage vor dem Termin gilt eine Wartung als „bald fällig“.</summary>
    public const int BaldFaelligTage = 14;

    /// <summary>Bis so viele Kilometer vor dem Fälligkeits-Kilometerstand gilt eine Wartung als „bald fällig“.</summary>
    public const int BaldFaelligKm = 1_000;

    public static readonly IReadOnlyList<string> Wartungsarten = new[]
    {
        "Inspektion", "Ölwechsel", "Bremsen", "Reifen", "Hauptuntersuchung (TÜV)", "Sicherheitsprüfung (SP)",
        "Tachografenprüfung", "Kühlaggregat", "Sonstiges"
    };

    /// <summary>
    /// Bewertet einen offenen Termin. Überfällig, wenn der Tag vorbei ist oder der Kilometerstand erreicht wurde.
    /// Bald fällig, wenn eines davon in den nächsten 14 Tagen bzw. 1.000 km erreicht wird.
    /// </summary>
    public static WartungsAmpel Bewerten(DateOnly termin, int? faelligBeiKm, int aktuellerKm, DateOnly heute)
    {
        if (termin < heute || (faelligBeiKm is int km && aktuellerKm >= km))
            return WartungsAmpel.Ueberfaellig;
        if (termin <= heute.AddDays(BaldFaelligTage) || (faelligBeiKm is int km2 && aktuellerKm >= km2 - BaldFaelligKm))
            return WartungsAmpel.BaldFaellig;
        return WartungsAmpel.Geplant;
    }

    public static WartungsAmpel Bewerten(Wartungstermin termin, DateOnly heute) =>
        Bewerten(termin.Termin, termin.FaelligBeiKm, termin.AktuellerKm, heute);

    /// <summary>
    /// Wartungsstatus eines Fahrzeugs (#92): der dringendste seiner offenen Termine, bei Gleichstand der früheste.
    /// Null, wenn das Fahrzeug keinen offenen Termin hat.
    /// </summary>
    public static Wartungstermin? DringendsterTermin(IEnumerable<Wartungstermin> termineDesFahrzeugs, DateOnly heute) =>
        termineDesFahrzeugs
            .OrderBy(termin => Bewerten(termin, heute))
            .ThenBy(termin => termin.Termin)
            .FirstOrDefault();

    public static string Bezeichnung(WartungsAmpel ampel) => ampel switch
    {
        WartungsAmpel.Ueberfaellig => "Überfällig",
        WartungsAmpel.BaldFaellig => "Bald fällig",
        WartungsAmpel.Geplant => "Geplant",
        _ => "Kein Termin"
    };

    /// <summary>Heute in deutscher Ortszeit, damit „überfällig“ nicht von der Windows-Zeitzone abhängt.</summary>
    public static DateOnly Heute()
    {
        DateTime jetzt;
        try
        {
            TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
            jetzt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        }
        catch (TimeZoneNotFoundException)
        {
            jetzt = DateTime.Now;
        }
        return DateOnly.FromDateTime(jetzt);
    }
}
