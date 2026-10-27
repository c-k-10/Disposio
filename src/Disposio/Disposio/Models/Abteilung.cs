namespace Disposio.Models;

/// <summary>
/// Eine Abteilung der eigenen Firma, so wie sie die Abteilungsverwaltung (K05) anzeigt:
/// mit der Zahl der Benutzer, Fahrzeuge und offenen Touren, die ihr zugeordnet sind.
/// Offen = Status nicht abgeschlossen, beendet oder storniert.
/// </summary>
public sealed record Abteilung(long Id, string Bezeichnung, bool Aktiv, int Benutzer, int Fahrzeuge, int OffeneTouren);
