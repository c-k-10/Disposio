namespace Disposio.Models;

/// <summary>
/// Eine Zeile der Tourenübersicht (D04): die Tour aus public.tour mit ihrem nächsten offenen Stopp
/// aus public.tourstopp. Uhrzeiten kommen schon als Text in deutscher Zeit (Europe/Berlin).
/// </summary>
/// <param name="Bereich">Abteilung der Tour, z. B. „Frische“.</param>
/// <param name="Fahrer">Vor- und Nachname, null wenn noch kein Fahrer zugeordnet ist.</param>
/// <param name="NaechsterStopp">Bezeichnung des ersten Stopps ohne Ist-Ankunft, null wenn alle erledigt sind.</param>
/// <param name="Eta">Aktuelle ETA, sonst geplante Ankunft des nächsten Stopps.</param>
/// <param name="Verspaetet">Die ETA liegt nach dem Ende des Zeitfensters.</param>
public sealed record TourUebersicht(
    long Id,
    string Tournummer,
    string? Bereich,
    string Status,
    string? Fahrer,
    string? Kennzeichen,
    string? NaechsterStopp,
    string? FensterVon,
    string? FensterBis,
    string? Eta,
    bool Verspaetet,
    bool HatFahrer,
    bool HatFahrzeug,
    string Startort,
    string Zielort);
