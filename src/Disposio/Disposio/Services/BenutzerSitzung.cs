using System;
using System.Linq;

namespace Disposio.Services;

/// <summary>
/// Ein MegakrasserAdmin schaut über „Einloggen“ im Dashboard in die Firmenansicht einer Kundenfirma.
/// Er bleibt dabei mit seinem eigenen Konto angemeldet; es wird kein Kundenkonto benutzt.
/// </summary>
public sealed record AdminFirmenzugang(long UnternehmenId, string Firmenname);

/// <summary>
/// Merkt sich das angemeldete Konto, solange die Anwendung läuft.
/// Nach einem Neustart wird erneut angemeldet. Passwort und Hash werden hier nicht gespeichert.
/// </summary>
public static class BenutzerSitzung
{
    public static AngemeldeterBenutzer? AktuellerBenutzer { get; private set; }
    public static bool IstEntwicklungsansicht { get; private set; }
    public static bool IstFirmenVorschau { get; private set; }

    // Gesetzt, solange ein MegakrasserAdmin die Firmenansicht einer Kundenfirma geöffnet hat.
    public static AdminFirmenzugang? Firmenzugang { get; private set; }

    // Eine Firmenrolle öffnet nicht die unternehmensübergreifende Verwaltung.
    public static bool DashboardZugriffErlaubt => IstEntwicklungsansicht ||
        AktuellerBenutzer?.IstMegakrasserAdmin == true;

    // Die Firmenansicht zeigt genau ein Unternehmen: das eigene eines Firmenkontos oder das, in
    // das ein MegakrasserAdmin über das Dashboard hineinschaut.
    public static bool FirmenansichtErlaubt => IstFirmenVorschau ||
        (Firmenzugang is not null && DashboardZugriffErlaubt) ||
        AktuellerBenutzer?.HatFirmenArbeitsbereich == true;

    public static void Starten(AngemeldeterBenutzer benutzer)
    {
        ArgumentNullException.ThrowIfNull(benutzer);
        // Eine eigene, schreibgeschützte Rollenliste verhindert versehentliches nachträgliches Ändern.
        AktuellerBenutzer = benutzer with { Rollen = Array.AsReadOnly(benutzer.Rollen.ToArray()) };
        IstEntwicklungsansicht = false;
        IstFirmenVorschau = false;
        Firmenzugang = null;
    }

    public static void Abmelden()
    {
        // Die Neon-Auth-Sitzung (Cookie) endet mit der Abmeldung. Sonst bliebe eine Admin-Sitzung hängen.
        NeonAuthClient.Instanz.SitzungVerwerfen();
        AktuellerBenutzer = null;
        IstEntwicklungsansicht = false;
        IstFirmenVorschau = false;
        Firmenzugang = null;
    }

    /// <summary>
    /// Nur ein MegakrasserAdmin (oder die Dashboard-Entwicklungsansicht) darf in eine Firma
    /// hineinschauen. Die Rolle wurde vorher in der Datenbank geprüft und der Zugang protokolliert.
    /// </summary>
    public static void FirmenzugangStarten(AdminFirmenzugang zugang)
    {
        ArgumentNullException.ThrowIfNull(zugang);
        if (!DashboardZugriffErlaubt)
            throw new UnauthorizedAccessException("Nur ein MegakrasserAdmin darf die Firmenansicht einer Kundenfirma öffnen.");

        Firmenzugang = zugang;
    }

    public static void FirmenzugangBeenden() => Firmenzugang = null;

    public static void EntwicklungsansichtStarten()
    {
#if DEBUG
        // Das Visual-Studio-Profil „Disposio (Dashboard)“ ist nur eine Entwicklungshilfe.
        // Der normale Start und veröffentlichte Release-Versionen verlangen die Anmeldung.
        AktuellerBenutzer = null;
        IstEntwicklungsansicht = true;
        IstFirmenVorschau = false;
        Firmenzugang = null;
#else
        throw new UnauthorizedAccessException("Der direkte Dashboard-Start ist nur im Debug-Build erlaubt.");
#endif
    }

    public static void FirmenVorschauStarten()
    {
#if DEBUG
        // Das Profil „Disposio (Firmenansicht)“ zeigt alle Büro-Rollen ohne Anmeldung, damit das
        // Grundgerüst ohne Testkonten durchgeklickt werden kann. Es lädt keine Firmendaten.
        AktuellerBenutzer = null;
        IstEntwicklungsansicht = false;
        IstFirmenVorschau = true;
        Firmenzugang = null;
#else
        throw new UnauthorizedAccessException("Der direkte Start der Firmenansicht ist nur im Debug-Build erlaubt.");
#endif
    }
}
