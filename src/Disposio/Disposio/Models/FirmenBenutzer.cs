using System;
using System.Collections.Generic;
using System.Linq;

namespace Disposio.Models;

/// <summary>
/// Ein Konto des eigenen Unternehmens, so wie es die Benutzerliste (K01) anzeigt.
/// Passwort und Hash gehören bewusst nicht dazu: Admins sehen keine Passwörter.
/// Rollen und RollenIds stehen in derselben Reihenfolge.
/// </summary>
public sealed record FirmenBenutzer(
    long Id,
    string Vorname,
    string Nachname,
    string Email,
    bool Aktiv,
    string Sprache,
    long? AbteilungId,
    string? Abteilung,
    IReadOnlyList<string> Rollen,
    IReadOnlyList<long> RollenIds,
    DateTime Erstellt)
{
    public string Anzeigename => $"{Vorname} {Nachname}".Trim();

    // Im Wireframe hat jeder Benutzer genau eine Rolle. Hat ein altes Konto mehrere,
    // zeigt die Liste alle an und das Formular startet mit der ersten.
    public string RollenText => Rollen.Count == 0 ? "Keine Rolle" : string.Join(", ", Rollen.Select(RollenAnzeige));

    public static string RollenAnzeige(string datenbankName) =>
        FirmenRollenKatalog.AusDatenbank(new[] { datenbankName }) is { Count: 1 } rolle
            ? FirmenRollenKatalog.Bezeichnung(rolle[0])
            : datenbankName;
}

/// <summary>
/// Die Eingaben aus K02. Das Startpasswort gibt es nur beim Anlegen; beim Bearbeiten ist es null.
/// RollenId ist die rollen_id aus public.rolle: eine Standardrolle oder eine eigene Rolle der Firma.
/// </summary>
public sealed record FirmenBenutzerEntwurf(
    string Vorname,
    string Nachname,
    string Email,
    long RollenId,
    long? AbteilungId,
    string Sprache,
    bool Aktiv,
    string? Passwort);

/// <summary>Eine Rolle für die Auswahlliste in K02. IstEigene: vom Kundenadmin in K03 angelegt.</summary>
public sealed record RollenAuswahl(long Id, string Bezeichnung, bool IstEigene)
{
    public string Anzeige => IstEigene ? $"{Bezeichnung} (eigene Rolle)" : FirmenBenutzer.RollenAnzeige(Bezeichnung);
}

/// <summary>
/// Die Sprachen aus FA-14. Wert = Inhalt der Spalte benutzer.sprache (Standard in Neon: 'de'),
/// Anzeige = Text im Formular.
/// </summary>
public static class SprachKatalog
{
    public static readonly IReadOnlyList<(string Wert, string Anzeige)> Alle = new[]
    {
        ("de", "Deutsch"),
        ("en", "Englisch"),
        ("pl", "Polnisch")
    };

    public static string Standard => Alle[0].Wert;

    public static bool IstGueltig(string wert) => Alle.Any(sprache => sprache.Wert == wert);

    // Unbekannte alte Werte werden unverändert angezeigt statt eine falsche Sprache zu behaupten.
    public static string Anzeige(string wert) =>
        Alle.FirstOrDefault(sprache => string.Equals(sprache.Wert, wert, StringComparison.OrdinalIgnoreCase)).Anzeige ?? wert;
}
