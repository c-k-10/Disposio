using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Disposio.Views
{
    // D01 lädt alles beim Öffnen in einem Rutsch (TagesuebersichtRepository) und zeigt es nur an.
    // „Aktualisieren“ lädt neu. Die Knöpfe öffnen D02, D06, D07 oder D15 mit dem passenden Startwert.
    public sealed partial class D01Tagesuebersicht : UserControl
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        private readonly FirmenAnsichtKontext _kontext;
        private readonly TagesuebersichtRepository _repository = new();
        private CancellationTokenSource? _laden;

        public D01Tagesuebersicht(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;

            Loaded += async (_, _) => await LadenAsync();
            Unloaded += (_, _) => _laden?.Cancel();
        }

        private async void Aktualisieren_Click(object sender, RoutedEventArgs e) => await LadenAsync();

        private void Ereignis_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: EreignisZeile zeile })
                _kontext.Oeffnen(zeile.ZielAnsicht, zeile.ZielParameter);
        }

        private void Hinweis_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: HinweisZeile zeile })
                _kontext.Oeffnen(zeile.ZielAnsicht, zeile.ZielParameter);
        }

        private async Task LadenAsync()
        {
            if (_kontext.IstVorschau)
            {
                StatusText.Text = "In der Vorschau ohne Anmeldung werden keine Daten geladen.";
                ZeitraumText.Text = "Vorschau ohne Anmeldung";
                EreignisLeer.Visibility = HinweisLeer.Visibility = Visibility.Visible;
                return;
            }

            _laden?.Cancel();
            var abbruch = new CancellationTokenSource();
            _laden = abbruch;
            LadeAnzeige.IsActive = true;
            LadeAnzeige.Visibility = Visibility.Visible;
            StatusText.Text = "Tagesübersicht wird geladen …";
            try
            {
                Tagesuebersicht daten = await _repository.LadenAsync(abbruch.Token);
                if (abbruch.IsCancellationRequested)
                    return;
                Anzeigen(daten);
                StatusText.Text = $"Stand {DateTime.Now.ToString("HH:mm", Deutsch)} Uhr";
            }
            catch (OperationCanceledException) when (abbruch.IsCancellationRequested)
            {
            }
            catch (UnauthorizedAccessException)
            {
                _kontext.ZugriffBeenden();
            }
            catch (Exception)
            {
                // Keine technischen Details anzeigen. Die zuletzt geladenen Werte bleiben sichtbar.
                StatusText.Text = "Die Tagesübersicht konnte nicht geladen werden. Bitte prüfen Sie die Verbindung und versuchen Sie es erneut.";
            }
            finally
            {
                if (ReferenceEquals(_laden, abbruch))
                {
                    _laden = null;
                    LadeAnzeige.IsActive = false;
                    LadeAnzeige.Visibility = Visibility.Collapsed;
                }
                abbruch.Dispose();
            }
        }

        private void Anzeigen(Tagesuebersicht d)
        {
            TourenWert.Text = d.TourenHeute.ToString(Deutsch);
            UnverplantWert.Text = d.SendungenUnverplant.ToString(Deutsch);
            FreigabenWert.Text = d.FreigabenOffen.ToString(Deutsch);
            SchaedenWert.Text = d.SchaedenOffen.ToString(Deutsch);
            SchaedenText.Text = d.SchaedenOffen == 1 ? "Schadensmeldung offen" : "Schadensmeldungen offen";

            ZeitraumText.Text = ZeitraumBeschreiben(d.Seit, d.SeitAbmeldung);

            EreignisListe.ItemsSource = d.Ereignisse.Select(e => new EreignisZeile(e)).ToList();
            EreignisLeer.Visibility = d.Ereignisse.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            HinweisListe.ItemsSource = d.Hinweise.Select(h => new HinweisZeile(h)).ToList();
            HinweisLeer.Visibility = d.Hinweise.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string ZeitraumBeschreiben(DateTime seit, bool seitAbmeldung)
        {
            string wann = EreignisZeile.Tag(seit) + ", " + seit.ToString("HH:mm", Deutsch) + " Uhr";
            if (seitAbmeldung)
                return $"Seit Ihrer letzten Abmeldung · {wann}";
            if (seit.Date == DateTime.Today.AddDays(-1) && seit.Hour == 18 && seit.Minute == 0)
                return "Seit gestern, 18:00 Uhr · Ihre letzte Abmeldung ist nicht bekannt.";
            return $"Seit {wann} · höchstens drei Tage zurück";
        }
    }

    // Eine Zeile in „Seit Ihrer letzten Schicht“, z. B. „22:15 · Tour T-12 abgeschlossen“.
    public sealed class EreignisZeile
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        public EreignisZeile(SchichtEreignis e)
        {
            // Heute nur die Uhrzeit wie im Wireframe, sonst mit Tag davor. Ohne Uhrzeit nur der Tag.
            string zeit = e.ZeitGenau
                ? (e.Zeit.Date == DateTime.Today ? e.Zeit.ToString("HH:mm", Deutsch) : $"{Tag(e.Zeit)} {e.Zeit.ToString("HH:mm", Deutsch)}")
                : Tag(e.Zeit);
            Kopf = $"{zeit} · {e.Titel}";
            Detail = e.Detail;
            KnopfText = e.KnopfText;
            KnopfBeschreibung = $"{e.KnopfText}: {e.Titel}";
            ZielAnsicht = e.ZielAnsicht;
            ZielParameter = e.ZielParameter;
        }

        // Für Tag="{x:Bind Selbst}": Der Knopf kennt so seine Zeile.
        public EreignisZeile Selbst => this;
        public string Kopf { get; }
        public string Detail { get; }
        public string KnopfText { get; }
        public string KnopfBeschreibung { get; }
        public string ZielAnsicht { get; }
        public object? ZielParameter { get; }

        // „Heute“, „Gestern“ oder z. B. „Mo 03.11.“
        public static string Tag(DateTime zeit)
        {
            if (zeit.Date == DateTime.Today)
                return "Heute";
            if (zeit.Date == DateTime.Today.AddDays(-1))
                return "Gestern";
            return zeit.ToString("ddd dd.MM.", Deutsch);
        }
    }

    // Eine Zeile in „Heute priorisieren“.
    public sealed class HinweisZeile
    {
        public HinweisZeile(TagesHinweis h)
        {
            Hinweis = h.Hinweis;
            Auswirkung = h.Auswirkung;
            KnopfText = h.KnopfText;
            KnopfBeschreibung = $"{h.KnopfText}: {h.Hinweis}";
            ZielAnsicht = h.ZielAnsicht;
            ZielParameter = h.ZielParameter;
        }

        public HinweisZeile Selbst => this;
        public string Hinweis { get; }
        public string Auswirkung { get; }
        public string KnopfText { get; }
        public string KnopfBeschreibung { get; }
        public string ZielAnsicht { get; }
        public object? ZielParameter { get; }
    }
}
