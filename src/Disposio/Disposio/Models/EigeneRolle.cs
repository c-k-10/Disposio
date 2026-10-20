using System;
using System.Collections.Generic;
using System.Linq;

namespace Disposio.Models;

/// <summary>
/// Eine eigene Rolle des angemeldeten Kontos mit den Ansichten, die sie freischaltet
/// (Wireframe-IDs wie „D02“). Kommt beim Login aus Neon und steckt danach in der Sitzung.
/// </summary>
public sealed record EigeneRolle(string Rolle, IReadOnlyList<string> Ansichten);

/// <summary>Eine einzelne Berechtigung aus public.berechtigung. Schlüssel = Wireframe-ID der Ansicht.</summary>
public sealed record Berechtigung(long Id, string Schluessel, string Bezeichnung, string Bereich);

/// <summary>Eine eigene Rolle der Firma, wie K03 sie zum Bearbeiten lädt.</summary>
public sealed record EigeneRolleDaten(long Id, string Bezeichnung, IReadOnlyList<long> BerechtigungIds, int Benutzer);

/// <summary>
/// Regeln für eigene Rollen. Sie dürfen nicht wie eine Standardrolle heißen, sonst könnte eine
/// eigene Rolle „Kundenadmin“ unbemerkt Admin-Rechte bekommen.
/// </summary>
public static class EigeneRollenRegeln
{
    public const int MaximaleLaenge = 60;

    public static bool IstReservierterName(string name)
    {
        string vergleich = name.Trim();
        return FirmenRollenKatalog.Alle
            .SelectMany(rolle => new[] { FirmenRollenKatalog.DatenbankName(rolle), FirmenRollenKatalog.Bezeichnung(rolle) })
            .Append("SuperKrasserAdmin")
            .Append("MegakrasserAdmin")
            .Any(reserviert => string.Equals(reserviert, vergleich, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Darf eine eigene Rolle diese Ansicht öffnen? Unteransichten (z. B. D03) folgen ihrer Oberansicht (D02).
    /// Die Kundenadmin-Ansichten K01–K04 werden nie über eigene Rollen vergeben.
    /// </summary>
    public static bool Erlaubt(IEnumerable<EigeneRolle> rollen, AnsichtInfo ansicht)
    {
        if (ansicht.Rolle == FirmenRolle.Kundenadmin)
            return false;

        string schluessel = ansicht.Oberansicht ?? ansicht.Id;
        return rollen.Any(rolle => rolle.Ansichten.Contains(schluessel));
    }
}
