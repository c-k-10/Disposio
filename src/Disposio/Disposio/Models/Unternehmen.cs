using System;

namespace Disposio.Models;

/// <summary>
/// Ein bereits gespeichertes Unternehmen. Dieser Datensatz enthält genau eine Zeile aus der Datenbank.
/// </summary>
/// <remarks>
/// Ein „record“ hält Daten zusammen. Die Eigenschaften sind nach dem Erstellen unveränderlich.
/// So kann ein Dialog die angezeigten Daten nicht versehentlich verändern, bevor Speichern erfolgreich war.
/// </remarks>
public sealed record Unternehmen(
    long Id,
    string Firmenname,
    string Kennung,
    string AdminEmail,
    int Benutzer,
    string Status,
    DateTime Version,
    string Adresse,
    string Plz,
    string Ort,
    string Telefon,
    string KontaktEmail,
    string Kontaktperson);

/// <summary>
/// Die erlaubten Werte von unternehmen.status. „Inaktiv“ sperrt den Mandanten: Seine Konten können sich
/// nicht mehr anmelden, seine Daten bleiben aber erhalten. Ein neues Unternehmen kann nicht inaktiv starten.
/// </summary>
public static class UnternehmenStatus
{
    public const string Testphase = "Testphase";
    public const string Aktiv = "Aktiv";
    public const string Inaktiv = "Inaktiv";
}

/// <summary>
/// Die Eingaben aus dem Anlegen-/Bearbeiten-Dialog. Id, Benutzerzahl und Version kommen aus der Datenbank.
/// Die Kontaktdaten gehören bereits zur vorhandenen Unternehmen-Tabelle und sind dort Pflichtfelder.
/// </summary>
public sealed record UnternehmenEntwurf(
    string Firmenname,
    string Kennung,
    string AdminEmail,
    string Status,
    string Adresse,
    string Plz,
    string Ort,
    string Telefon,
    string KontaktEmail,
    string Kontaktperson);
