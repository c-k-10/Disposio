using System;

namespace Disposio.Services
{
    /// <summary>
    /// Liefert den TomTom-API-Schlüssel für die Verkehrslage auf der Live-Karte (D08).
    /// Reihenfolge: zuerst die Windows-Benutzervariable DISPOSIO_TOMTOM_KEY (falls jemand einen
    /// eigenen Schlüssel nutzen will), sonst der hier eingetragene Schlüssel.
    /// </summary>
    /// <remarks>
    /// Diese Datei liegt im öffentlichen Git-Repository. Der Schlüssel gehört zu einem kostenlosen
    /// TomTom-Konto ohne Zahlungsdaten; im schlimmsten Fall ist das Monatskontingent aufgebraucht.
    /// Keine anderen Zugangsdaten (Datenbank, Neon Auth) auf diese Weise speichern!
    /// </remarks>
    internal static class TomTomKonfiguration
    {
        internal const string UmgebungsVariable = "DISPOSIO_TOMTOM_KEY";

        // Hier den TomTom-Schlüssel eintragen (developer.tomtom.com → Dashboard → Keys).
        private const string Schluessel = "w1jSE4a4N8whCLkP7jNFEgxinZMrfxiL";

        public static string? SchluesselLesen()
        {
            string? ausVariable = Environment.GetEnvironmentVariable(UmgebungsVariable);
            if (!string.IsNullOrWhiteSpace(ausVariable))
                return ausVariable.Trim();

            if (string.IsNullOrWhiteSpace(Schluessel) || Schluessel == "HIER_SCHLUESSEL_EINTRAGEN")
                return null;

            return Schluessel.Trim();
        }
    }
}
