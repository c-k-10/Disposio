using System;
using System.Linq;

namespace Disposio.Services;

/// <summary>
/// Merkt sich das angemeldete Konto, solange die Anwendung läuft.
/// Nach einem Neustart wird erneut angemeldet. Passwort und Hash werden hier nicht gespeichert.
/// </summary>
public static class BenutzerSitzung
{
    public static AngemeldeterBenutzer? AktuellerBenutzer { get; private set; }
    public static bool IstEntwicklungsansicht { get; private set; }

    // Eine Firmenrolle öffnet nicht die unternehmensübergreifende Verwaltung.
    public static bool DashboardZugriffErlaubt => IstEntwicklungsansicht ||
        AktuellerBenutzer?.IstMegakrasserAdmin == true;

    public static void Starten(AngemeldeterBenutzer benutzer)
    {
        ArgumentNullException.ThrowIfNull(benutzer);
        // Eine eigene, schreibgeschützte Rollenliste verhindert versehentliches nachträgliches Ändern.
        AktuellerBenutzer = benutzer with { Rollen = Array.AsReadOnly(benutzer.Rollen.ToArray()) };
        IstEntwicklungsansicht = false;
    }

    public static void Abmelden()
    {
        AktuellerBenutzer = null;
        IstEntwicklungsansicht = false;
    }

    public static void EntwicklungsansichtStarten()
    {
#if DEBUG
        // Das Visual-Studio-Profil „Disposio (Dashboard)“ ist nur eine Entwicklungshilfe.
        // Der normale Start und veröffentlichte Release-Versionen verlangen die Anmeldung.
        AktuellerBenutzer = null;
        IstEntwicklungsansicht = true;
#else
        throw new UnauthorizedAccessException("Der direkte Dashboard-Start ist nur im Debug-Build erlaubt.");
#endif
    }
}
