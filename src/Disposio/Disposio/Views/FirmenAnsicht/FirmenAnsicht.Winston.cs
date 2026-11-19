using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Disposio.Views
{
    // Winston (KI-Co-Disponent, FA-15) im Rahmen der Firmenansicht:
    //   - Knopf „Winston“ oben rechts blendet die Seitenleiste (WinstonLeiste) ein und aus.
    //   - Im Hintergrund wird alle 2 Minuten nach Verspätungen gesucht, egal welche Ansicht offen ist.
    //     Neue Vorschläge arbeitet die KI aus (KundenkommunikationRepository); der Disponent bekommt einen
    //     Hinweis und entscheidet in der Kundenkommunikation (D10) über Freigeben oder Verwerfen.
    // Nur für Konten mit der Rolle Disponent (und den Admin-Zugang).
    public sealed partial class FirmenAnsicht
    {
        private const string KundenkommunikationId = "D10";
        private static readonly TimeSpan WinstonAbstand = TimeSpan.FromMinutes(2);

        private readonly DispatcherTimer _winstonTimer = new() { Interval = WinstonAbstand };
        private readonly KundenkommunikationRepository _winstonVorschlaege = new();
        private WinstonLeiste? _winstonLeiste;
        // Vorschläge, die in dieser Sitzung schon gemeldet wurden. null = noch kein Durchlauf.
        private HashSet<long>? _gemeldeteVorschlaege;
        private bool _winstonEingerichtet;
        private bool _winstonPrueft;
        private long? _tippTourId;
        // Letzter Fehler der Hintergrundprüfung, damit er nur einmal in der Seitenleiste erscheint.
        private string? _winstonFehler;

        // Wird nach der Anmeldung aufgerufen, sobald die Rollen feststehen.
        private void WinstonEinrichten()
        {
            bool disponent = !_nurStatistik && _rollen.Contains(FirmenRolle.Disponent);
            WinstonButton.Visibility = disponent ? Visibility.Visible : Visibility.Collapsed;
            // In der Vorschau ohne Anmeldung gibt es keine Firmendaten, also auch nichts zu prüfen.
            if (!disponent || BenutzerSitzung.IstFirmenVorschau || _winstonEingerichtet)
                return;

            _winstonEingerichtet = true;
            _winstonTimer.Tick += async (_, _) => await WinstonPruefenAsync();
            Unloaded += (_, _) => _winstonTimer.Stop();
            _winstonTimer.Start();
            _ = WinstonPruefenAsync();
        }

        private void WinstonBeenden()
        {
            _winstonTimer.Stop();
            WinstonTipp.IsOpen = false;
            WinstonHost.Visibility = Visibility.Collapsed;
        }

        private WinstonLeiste Leiste()
        {
            if (_winstonLeiste is null)
            {
                _winstonLeiste = new WinstonLeiste(Open,
                    () => ReturnToLogin("Ihr Zugriff ist nicht mehr verfügbar. Bitte melden Sie sich erneut an."),
                    () => WinstonHost.Visibility = Visibility.Collapsed);
                WinstonHost.Child = _winstonLeiste;
            }
            return _winstonLeiste;
        }

        private void Winston_Click(object sender, RoutedEventArgs e)
        {
            bool oeffnen = WinstonHost.Visibility != Visibility.Visible;
            WinstonLeiste leiste = Leiste();
            WinstonHost.Visibility = oeffnen ? Visibility.Visible : Visibility.Collapsed;
            if (oeffnen)
            {
                WinstonTipp.IsOpen = false;
                leiste.Anzeigen();
            }
        }

        private void WinstonTipp_Ansehen(TeachingTip sender, object args)
        {
            sender.IsOpen = false;
            Open(KundenkommunikationId, _tippTourId);
        }

        // Sucht nach Verspätungen, lässt neue Vorschläge von der KI ausarbeiten und meldet sie.
        // Fehler (kein Netz, Datenbank nicht erreichbar): In 2 Minuten kommt der nächste Versuch. Der Grund erscheint
        // im Ausgabefenster von Visual Studio und einmal in der Seitenleiste, damit ein Ausfall nicht unbemerkt bleibt.
        private async Task WinstonPruefenAsync()
        {
            if (_winstonPrueft || _returningToLogin)
                return;

            _winstonPrueft = true;
            try
            {
                IReadOnlyList<Verspaetungsvorschlag> offen = await _winstonVorschlaege.AktualisierenUndLadenAsync();
                if (_returningToLogin)
                    return;
                if (_winstonFehler is not null)
                {
                    _winstonFehler = null;
                    ToolTipService.SetToolTip(WinstonButton, "Winston fragen (KI-Co-Disponent)");
                }

                Leiste().PruefungMelden(offen.Count);
                WinstonZahl.Visibility = offen.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                WinstonZahlText.Text = offen.Count.ToString();

                _gemeldeteVorschlaege ??= new HashSet<long>();
                Verspaetungsvorschlag[] neue = offen.Where(v => _gemeldeteVorschlaege.Add(v.Id)).ToArray();
                if (neue.Length == 0)
                    return;

                Leiste().VerspaetungenMelden(neue);
                // Wer gerade die Kundenkommunikation oder die Seitenleiste offen hat, sieht die Vorschläge schon.
                if (_menueAktiv == KundenkommunikationId || WinstonHost.Visibility == Visibility.Visible)
                    return;

                _tippTourId = neue[0].TourId;
                WinstonTipp.Subtitle = neue.Length == 1
                    ? $"Tour {neue[0].Tournummer} kommt zu spät nach {neue[0].Stopp}: Ankunft etwa {neue[0].EtaSnapshot}, " +
                      $"das Zeitfenster endet um {neue[0].Fensterende}. Ich habe einen Vorschlag für Kunde und Fahrer vorbereitet."
                    : $"{neue.Length} Verspätungen brauchen deine Entscheidung, zuerst Tour {neue[0].Tournummer} nach {neue[0].Stopp}. " +
                      "Die Vorschläge für Kunden und Fahrer liegen bereit.";
                WinstonTipp.IsOpen = true;
            }
            catch (UnauthorizedAccessException)
            {
                // Rolle entzogen oder abgemeldet: Die nächste Aktion in einer Ansicht führt zur Anmeldung.
                _winstonTimer.Stop();
            }
            catch (Exception fehler)
            {
                Exception innen = fehler.InnerException ?? fehler;
                string grund = $"{innen.GetType().Name}: {innen.Message}";
                System.Diagnostics.Debug.WriteLine($"[Winston] Prüfung auf Verspätungen fehlgeschlagen: {grund}");
                if (grund != _winstonFehler && !_returningToLogin)
                {
                    _winstonFehler = grund;
                    string text = "Ich konnte gerade nicht nach Verspätungen suchen. Neuer Versuch in 2 Minuten. (" + grund + ")";
                    ToolTipService.SetToolTip(WinstonButton, text);
                    Leiste().FehlerMelden(text);
                }
            }
            finally
            {
                _winstonPrueft = false;
            }
        }
    }
}
