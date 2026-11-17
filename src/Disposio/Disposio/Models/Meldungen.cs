using System;
using System.Collections.Generic;

namespace Disposio.Models;

/// <summary>
/// Eine Tank- oder Schadensmeldung aus der Fahrer-App für D15 „Meldungen bearbeiten“.
/// Art ist „Schaden“ (public.schadensmeldung) oder „Tankvorgang“ (public.tankvorgang). Liter nur beim Tankvorgang.
/// Der Anhang ist das neueste Dokument (public.dokument) zur Meldung, Anhaenge die Anzahl aller.
/// </summary>
public sealed record Meldung(
    string Art,
    long Id,
    string Titel,
    string Hinweis,
    string Status,
    string? Notiz,
    long? BearbeiterId,
    decimal Kosten,
    decimal? Liter,
    DateOnly Datum,
    long FahrzeugId,
    string Kennzeichen,
    string FahrzeugStatus,
    string? Fahrer,
    string? AnhangAdresse,
    string? AnhangMimeTyp,
    string? AnhangName,
    int Anhaenge)
{
    public bool IstTank => Art == MeldungWerte.ArtTank;
    public bool IstOffen => MeldungWerte.IstOffen(Status);
    public string Schluessel => $"{Art}:{Id}";
}

/// <summary>Wer eine Meldung bearbeiten kann (Spalte bearbeiter_benutzer_id): Büro-Konten der Firma.</summary>
public sealed record MeldungBearbeiter(long Id, string Name, string Rollen)
{
    public string Anzeige => string.IsNullOrWhiteSpace(Rollen) ? Name : $"{Name} · {Rollen}";
}

public sealed record MeldungenDaten(IReadOnlyList<Meldung> Meldungen, IReadOnlyList<MeldungBearbeiter> Bearbeiter);

/// <summary>Die Eingaben aus „Meldung prüfen“. Liter nur beim Tankvorgang (sonst null).</summary>
public sealed record MeldungBearbeitung(
    string Art,
    long Id,
    string Status,
    string Notiz,
    long BearbeiterId,
    decimal Kosten,
    decimal? Liter,
    string FahrzeugStatus);

/// <summary>Auswahllisten von D15. Sie stehen nur an dieser einen Stelle.</summary>
public static class MeldungWerte
{
    public const string ArtSchaden = "Schaden";
    public const string ArtTank = "Tankvorgang";

    public const string StatusOffen = "Offen";
    public const string StatusErledigt = "Erledigt";

    /// <summary>Bearbeitungsstatus im Formular. Unbekannte Werte aus der Fahrer-App bleiben zusätzlich wählbar.</summary>
    public static readonly IReadOnlyList<string> Status =
        new[] { StatusOffen, "In Prüfung", "Rückfrage an Fahrer", "Beleg fehlt", StatusErledigt };

    /// <summary>Fahrzeugstatus nach der Prüfung: Wert in fahrzeug.status und Text im Formular.</summary>
    public static readonly IReadOnlyList<(string Wert, string Anzeige)> Verfuegbarkeit = new[]
    {
        ("Verfügbar", "Verfügbar – kann weiterfahren"),
        ("Wartung", "Wartung – muss in die Werkstatt"),
        ("Gesperrt", "Gesperrt bis Prüfung")
    };

    // Gleiche Regel wie in D01 (TagesuebersichtRepository): erledigt, abgeschlossen, geschlossen oder repariert.
    public static bool IstOffen(string status)
    {
        string s = status.ToLowerInvariant();
        return !(s.Contains("erledigt") || s.Contains("abgeschlossen") || s.Contains("geschlossen") || s.Contains("repariert"));
    }
}
