namespace Disposio.Models;

/// <summary>
/// Eine Abteilung des eigenen Unternehmens für Auswahllisten, zum Beispiel im
/// Fahrzeugformular (F02) und im Benutzerformular (K02).
/// </summary>
public sealed record AbteilungAuswahl(long Id, string Bezeichnung);
