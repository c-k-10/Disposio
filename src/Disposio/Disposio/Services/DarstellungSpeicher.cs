using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Disposio.Services
{
    /// <summary>
    /// Merkt sich, ob die App hell oder dunkel angezeigt wird und in welcher Akzentfarbe.
    /// Lokal auf diesem PC (für die Anmeldeseite) und zusätzlich je Konto in der Datenbank
    /// (benutzer.darstellung_hell / darstellung_akzent, Migration 015): Ist jemand angemeldet, wird jede
    /// Änderung sofort für sein Konto gespeichert und beim nächsten Anmelden wieder angewendet – auch an einem
    /// anderen PC. Fehlen die lokalen Einstellungen (z. B. ohne MSIX-Paket), gilt lokal einfach Hell.
    /// </summary>
    public static class DarstellungSpeicher
    {
        private const string Schluessel = "Darstellung.Hell";
        private const string MenueSchluessel = "Darstellung.MenueAngeheftet";
        private const string AkzentSchluessel = "Darstellung.Akzent";

        private const string HellSpeichernQuery =
            "UPDATE public.benutzer SET darstellung_hell = $2::boolean WHERE benutzer_id = $1::bigint";
        private const string AkzentSpeichernQuery =
            "UPDATE public.benutzer SET darstellung_akzent = $2 WHERE benutzer_id = $1::bigint";
        private const string LadenQuery =
            "SELECT darstellung_hell, darstellung_akzent FROM public.benutzer WHERE benutzer_id = $1::bigint";

        // Änderungen nacheinander schreiben, damit bei schnellem Umschalten die letzte Auswahl gewinnt.
        private static readonly SemaphoreSlim Reihe = new(1, 1);
        private static readonly Regex FarbMuster = new("^#[0-9A-Fa-f]{6}$");

        /// <summary>Hauptfarbe der App als #RRGGBB. Standard ist Disposio-Blau wie in der Fahrer-App.</summary>
        public static string Akzent
        {
            get
            {
                try
                {
                    return ApplicationData.Current.LocalSettings.Values[AkzentSchluessel] is string hex && hex.Length == 7
                        ? hex
                        : "#263749";
                }
                catch (Exception)
                {
                    return "#263749";
                }
            }
            set
            {
                AkzentLokal(value);
                if (IstFarbe(value))
                    KontoSpeichern(AkzentSpeichernQuery, value.ToUpperInvariant());
            }
        }

        /// <summary>Linke Navigationsleiste angeheftet (breit mit Namen) oder eingeklappt (nur Symbole).</summary>
        public static bool MenueAngeheftet
        {
            get
            {
                try
                {
                    return ApplicationData.Current.LocalSettings.Values[MenueSchluessel] is not bool wert || wert;
                }
                catch (Exception)
                {
                    return true;
                }
            }
            set
            {
                try
                {
                    ApplicationData.Current.LocalSettings.Values[MenueSchluessel] = value;
                }
                catch (Exception)
                {
                }
            }
        }

        public static bool IstHell
        {
            get
            {
                try
                {
                    return ApplicationData.Current.LocalSettings.Values[Schluessel] is not bool hell || hell;
                }
                catch (Exception)
                {
                    return true;
                }
            }
            set
            {
                HellLokal(value);
                KontoSpeichern(HellSpeichernQuery, value ? "true" : "false");
            }
        }

        // ---- Je Konto in der Datenbank ----

        /// <summary>
        /// Liest die gespeicherte Darstellung eines Kontos. Ein Wert ist null, wenn das Konto ihn noch nie
        /// gespeichert hat. Bei Verbindungsfehlern oder fehlender Migration 015 kommt (null, null):
        /// Die Anmeldung darf daran nie scheitern.
        /// </summary>
        public static async Task<(bool? Hell, string? Akzent)> KontoLadenAsync(long benutzerId)
        {
            try
            {
                using var abbruch = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var rows = await new NeonSqlClient().QueryAsync(LadenQuery,
                    new string?[] { benutzerId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    abbruch.Token);
                if (rows.Count != 1)
                    return (null, null);
                bool? hell = rows[0][0] switch { "t" => true, "f" => false, _ => null };
                string? akzent = rows[0][1] is string hex && IstFarbe(hex) ? hex.ToUpperInvariant() : null;
                return (hell, akzent);
            }
            catch (Exception)
            {
                return (null, null);
            }
        }

        /// <summary>
        /// Nach dem Anmelden: Die Darstellung des Kontos gilt ab jetzt auch lokal (für die Anmeldeseite beim
        /// nächsten Start). Ein Wert, den das Konto noch nicht hatte, wird für das Konto gespeichert.
        /// Erst nach BenutzerSitzung.Starten aufrufen.
        /// </summary>
        public static void KontoUebernehmen(bool hell, string akzent, bool hellNeu, bool akzentNeu)
        {
            HellLokal(hell);
            AkzentLokal(akzent);
            if (hellNeu)
                KontoSpeichern(HellSpeichernQuery, hell ? "true" : "false");
            if (akzentNeu && IstFarbe(akzent))
                KontoSpeichern(AkzentSpeichernQuery, akzent.ToUpperInvariant());
        }

        public static bool IstFarbe(string? hex) => hex is not null && FarbMuster.IsMatch(hex);

        // Ohne angemeldetes Konto (Anmeldeseite, Entwicklungsansicht) wird nur lokal gespeichert.
        // Fehler werden verschluckt: Eine nicht gespeicherte Farbe darf die App nie stören.
        private static async void KontoSpeichern(string query, string wert)
        {
            if (BenutzerSitzung.AktuellerBenutzer is not { } benutzer)
                return;
            string id = benutzer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                await Reihe.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var abbruch = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await new NeonSqlClient().QueryAsync(query, new string?[] { id, wert }, abbruch.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    Reihe.Release();
                }
            }
            catch (Exception)
            {
            }
        }

        // ---- Nur lokal auf diesem PC ----

        private static void HellLokal(bool hell)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[Schluessel] = hell;
            }
            catch (Exception)
            {
                // Ohne Paket gibt es keine lokalen Einstellungen. Die Auswahl gilt dann nur bis zum Schließen.
            }
        }

        private static void AkzentLokal(string hex)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[AkzentSchluessel] = hex;
            }
            catch (Exception)
            {
            }
        }
    }
}
