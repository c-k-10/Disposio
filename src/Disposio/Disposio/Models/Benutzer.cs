namespace Disposio.Models;

/// <summary>
/// Die Eingaben aus dem Dialog „Kundenadmin anlegen“. Das Klartext-Passwort wird nur
/// im Repository in einen BCrypt-Hash umgewandelt und niemals an die Datenbank geschickt.
/// </summary>
public sealed record BenutzerEntwurf(
    long UnternehmenId,
    string Vorname,
    string Nachname,
    string Email,
    string Passwort);
