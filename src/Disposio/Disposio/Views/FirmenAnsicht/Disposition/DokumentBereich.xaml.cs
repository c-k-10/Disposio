using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace Disposio.Views
{
    /// <summary>
    /// Dokumente einer Sendung: Liste (Name öffnet die Datei im Browser), „Datei hochladen …“ mit Dokumentart und
    /// ✕ zum Entfernen. Entfernen geht nur bei im Büro hochgeladenen Dokumenten (nicht Fahrer-App / Abliefernachweis).
    /// Ohne <see cref="AuftragId"/> (neue Sendung in D03) werden Dateien nur vorgemerkt.
    /// </summary>
    public sealed partial class DokumentBereich : UserControl
    {
        private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

        private readonly FrachtdokumentRepository _repository = new();
        private readonly List<VorgemerktesDokument> _vorgemerkt = new();
        private IReadOnlyList<Frachtdokument> _dokumente = Array.Empty<Frachtdokument>();
        private long? _auftragId;
        private bool _arbeitet;
        private bool _geladen = true;   // ohne Sendung gibt es nichts zu laden

        public DokumentBereich()
        {
            InitializeComponent();
            ArtAuswahl.ItemsSource = DokumentStatus.Arten;
            ArtAuswahl.SelectedIndex = 0;
            SpeicherPruefen();
            Anzeigen();
        }

        /// <summary>Wird ausgelöst, wenn sich die Liste ändert (z. B. für die Zusammenfassung in D03).</summary>
        public event EventHandler? Geaendert;

        /// <summary>Null = neue Sendung: Dateien nur vormerken.</summary>
        public long? AuftragId
        {
            get => _auftragId;
            set
            {
                _auftragId = value;
                _geladen = value is null;
                SpeicherPruefen();
                Anzeigen();
            }
        }

        /// <summary>Vorschau ohne Anmeldung: nichts laden, nichts hochladen.</summary>
        public bool NurAnsicht { get; set; }

        public IReadOnlyList<VorgemerktesDokument> Vorgemerkt => _vorgemerkt;

        /// <summary>Für die Zusammenfassung in D03: „Lieferschein: ls.pdf“ usw.</summary>
        public IEnumerable<string> Zusammenfassung() =>
            _dokumente.Select(d => $"{d.Art}: {Anzeigename(d)}")
                .Concat(_vorgemerkt.Select(d => $"{d.Art}: {d.Dateiname} (wird beim Speichern hochgeladen)"));

        // ---- Laden ----

        public async Task LadenAsync()
        {
            if (_auftragId is not long id)
                return;
            if (NurAnsicht)
            {
                LadeText.Text = "In der Vorschau werden keine Dokumente geladen.";
                return;
            }
            LadeText.Text = "Dokumente werden geladen …";
            _geladen = false;
            LadeText.Visibility = Visibility.Visible;
            try
            {
                _dokumente = await _repository.AuftragDokumenteLadenAsync(id);
                _geladen = true;
            }
            catch (UnauthorizedAccessException)
            {
                LadeText.Text = "Bitte melden Sie sich erneut an.";
            }
            catch (Exception)
            {
                LadeText.Text = "Die Dokumente konnten nicht geladen werden.";
            }
            Anzeigen();
        }

        /// <summary>
        /// D03 nach dem Anlegen der Sendung: vorgemerkte Dateien hochladen.
        /// </summary>
        /// <returns>Namen der Dateien, die nicht hochgeladen werden konnten.</returns>
        public async Task<IReadOnlyList<string>> VorgemerkteHochladenAsync(long auftragId)
        {
            var fehlgeschlagen = new List<string>();
            foreach (VorgemerktesDokument d in _vorgemerkt.ToList())
            {
                try
                {
                    await _repository.HochladenAsync(auftragId, d.Art, d.Dateiname, d.Inhalt);
                    _vorgemerkt.Remove(d);
                }
                catch (Exception)
                {
                    fehlgeschlagen.Add(d.Dateiname);
                }
            }
            return fehlgeschlagen;
        }

        // ---- Hochladen ----

        private async void Hochladen_Click(object sender, RoutedEventArgs e)
        {
            if (_arbeitet || NurAnsicht)
                return;
            string art = ArtAuswahl.SelectedItem as string ?? "";

            IReadOnlyList<Windows.Storage.StorageFile> dateien;
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                foreach (string endung in new[] { ".pdf", ".jpg", ".jpeg", ".png" })
                    picker.FileTypeFilter.Add(endung);
                // WinUI 3: Der Dialog braucht das Fenster, zu dem er gehört.
                IntPtr fenster = Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, fenster);
                dateien = await picker.PickMultipleFilesAsync();
            }
            catch (Exception)
            {
                StatusText.Text = "Die Dateiauswahl konnte nicht geöffnet werden.";
                return;
            }
            if (dateien is null || dateien.Count == 0)
                return;

            _arbeitet = true;
            HochladenKnopf.IsEnabled = false;
            var meldungen = new List<string>();
            int erfolgreich = 0;
            try
            {
                foreach (var datei in dateien)
                {
                    StatusText.Text = _auftragId is null ? $"{datei.Name} wird gelesen …" : $"{datei.Name} wird hochgeladen …";
                    try
                    {
                        var info = await datei.GetBasicPropertiesAsync();
                        if (info.Size > (ulong)FrachtdokumentRepository.MaxBytes)
                            throw new ArgumentException("Die Datei ist größer als 10 MB.");
                        byte[] inhalt = await File.ReadAllBytesAsync(datei.Path);
                        FrachtdokumentRepository.DateiPruefen(art, inhalt);

                        if (_auftragId is long id)
                            await _repository.HochladenAsync(id, art, datei.Name, inhalt);
                        else
                            _vorgemerkt.Add(new VorgemerktesDokument(art, datei.Name, inhalt));
                        erfolgreich++;
                    }
                    catch (ArgumentException fehler)
                    {
                        meldungen.Add($"{datei.Name}: {fehler.Message}");
                    }
                    catch (InvalidOperationException fehler) when (fehler.Message.StartsWith("Der Dokumentenspeicher", StringComparison.Ordinal))
                    {
                        meldungen.Add(fehler.Message);
                        break;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        meldungen.Add("Bitte melden Sie sich erneut an.");
                        break;
                    }
                    catch (Exception)
                    {
                        meldungen.Add($"{datei.Name}: konnte nicht hochgeladen werden. Bitte prüfen Sie die Verbindung.");
                    }
                }
            }
            finally
            {
                _arbeitet = false;
                HochladenKnopf.IsEnabled = true;
            }

            if (_auftragId is not null && erfolgreich > 0)
                await LadenAsync();
            else
                Anzeigen();

            string erfolg = erfolgreich == 0 ? ""
                : _auftragId is null
                    ? (erfolgreich == 1 ? "1 Datei vorgemerkt." : $"{erfolgreich} Dateien vorgemerkt.")
                    : (erfolgreich == 1 ? "1 Dokument hochgeladen." : $"{erfolgreich} Dokumente hochgeladen.");
            StatusText.Text = string.Join(" ", new[] { erfolg }.Concat(meldungen).Where(t => t.Length > 0));
            StatusText.Foreground = meldungen.Count > 0
                ? Ressource("ErrorBrush")
                : Ressource("MutedTextBrush");
            Geaendert?.Invoke(this, EventArgs.Empty);
        }

        // ---- Entfernen ----

        private async Task EntfernenAsync(Frachtdokument d)
        {
            if (_arbeitet)
                return;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Dokument entfernen?",
                Content = $"„{Anzeigename(d)}“ ({d.Art}) wird von der Sendung entfernt und gelöscht.",
                PrimaryButtonText = "Entfernen",
                CloseButtonText = "Abbrechen",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            _arbeitet = true;
            StatusText.Foreground = Ressource("MutedTextBrush");
            StatusText.Text = "Wird entfernt …";
            try
            {
                await _repository.EntfernenAsync(d.Id);
                StatusText.Text = $"„{Anzeigename(d)}“ wurde entfernt.";
            }
            catch (ArgumentException fehler)
            {
                StatusText.Foreground = Ressource("ErrorBrush");
                StatusText.Text = fehler.Message;
            }
            catch (UnauthorizedAccessException)
            {
                StatusText.Foreground = Ressource("ErrorBrush");
                StatusText.Text = "Bitte melden Sie sich erneut an.";
            }
            catch (Exception)
            {
                StatusText.Foreground = Ressource("ErrorBrush");
                StatusText.Text = "Das Dokument konnte nicht entfernt werden. Bitte prüfen Sie die Verbindung.";
            }
            finally
            {
                _arbeitet = false;
            }
            await LadenAsync();
            Geaendert?.Invoke(this, EventArgs.Empty);
        }

        // ---- Anzeige ----

        private void SpeicherPruefen()
        {
            bool speicher = CloudinarySpeicher.Verfuegbar;
            HochladenKnopf.IsEnabled = speicher && !_arbeitet && !NurAnsicht;
            ArtAuswahl.IsEnabled = speicher && !NurAnsicht;
            SpeicherText.Text = NurAnsicht ? "In der Vorschau können keine Dokumente hochgeladen werden."
                : !speicher
                ? $"Hochladen ist nicht möglich: Der Dokumentenspeicher ist nicht eingerichtet (Windows-Benutzervariable {CloudinarySpeicher.UmgebungsVariable})."
                : _auftragId is null
                    ? "PDF, JPG oder PNG, höchstens 10 MB. Die Dateien werden hochgeladen, sobald die Sendung gespeichert ist."
                    : "PDF, JPG oder PNG, höchstens 10 MB. Wird sofort gespeichert.";
        }

        private void Anzeigen()
        {
            Liste.Children.Clear();
            LadeText.Visibility = _geladen ? Visibility.Collapsed : Visibility.Visible;

            foreach (Frachtdokument d in _dokumente)
                ZeileHinzufuegen(GlyphFuer(d.MimeTyp), Anzeigename(d), d.Adresse,
                    $"{d.Art} · {Groesse(d.Bytes)} · {d.HochgeladenAm.ToString("dd.MM.yyyy HH:mm", Deutsch)}" +
                    (d.AusFahrerApp ? $" · Fahrer-App ({d.HochgeladenVon})" : string.IsNullOrWhiteSpace(d.HochgeladenVon) ? "" : $" · {d.HochgeladenVon}"),
                    d.Entfernbar ? () => EntfernenAsync(d) : (Func<Task>?)null);

            foreach (VorgemerktesDokument d in _vorgemerkt)
            {
                VorgemerktesDokument eintrag = d;
                ZeileHinzufuegen(GlyphFuer(Path.GetExtension(d.Dateiname).ToLowerInvariant() == ".pdf" ? "application/pdf" : "image"),
                    d.Dateiname, null, $"{d.Art} · {Groesse(d.Inhalt.LongLength)} · wird beim Speichern hochgeladen",
                    () =>
                    {
                        _vorgemerkt.Remove(eintrag);
                        Anzeigen();
                        Geaendert?.Invoke(this, EventArgs.Empty);
                        return Task.CompletedTask;
                    });
            }

            LeerText.Visibility = _geladen && Liste.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Eine Zeile: Symbol | Name (Link) + Angaben | ✕
        private void ZeileHinzufuegen(string glyph, string name, string? adresse, string angaben, Func<Task>? entfernen)
        {
            if (Liste.Children.Count > 0)
                Liste.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = Ressource("BorderBrush") });

            var zeile = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            zeile.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            zeile.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            zeile.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            zeile.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Ressource("MutedTextBrush")
            });

            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            if (adresse is not null && Uri.TryCreate(adresse, UriKind.Absolute, out Uri? uri))
            {
                var link = new HyperlinkButton
                {
                    Content = name,
                    NavigateUri = uri,
                    Padding = new Thickness(0),
                    FontSize = 14
                };
                ToolTipService.SetToolTip(link, "Im Browser öffnen");
                text.Children.Add(link);
            }
            else
            {
                text.Children.Add(new TextBlock
                {
                    Text = name,
                    Style = (Style)Application.Current.Resources["NormalText"],
                    FontSize = 14,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }
            text.Children.Add(new TextBlock
            {
                Text = angaben,
                Style = (Style)Application.Current.Resources["HinweisText"],
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(text, 1);
            zeile.Children.Add(text);

            if (entfernen is not null && !NurAnsicht)
            {
                var knopf = new Button
                {
                    Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
                    Padding = new Thickness(8, 6, 8, 6),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(knopf, "Dokument entfernen");
                AutomationProperties.SetName(knopf, $"{name} entfernen");
                knopf.Click += async (_, _) => await entfernen();
                Grid.SetColumn(knopf, 2);
                zeile.Children.Add(knopf);
            }

            Liste.Children.Add(zeile);
        }

        // Die Fahrer-App speichert Zufallsnamen (Guid) – dann lieber die Art zeigen (wie in D11).
        private static string Anzeigename(Frachtdokument d)
        {
            string endung = Path.GetExtension(d.Dateiname);
            bool zufall = Guid.TryParse(Path.GetFileNameWithoutExtension(d.Dateiname), out _);
            return zufall ? (d.Art == "Unterschrift" ? "Empfangsbestätigung" : d.Art) + endung : d.Dateiname;
        }

        private static string GlyphFuer(string mimeTyp) => mimeTyp == "application/pdf" ? "\uE8A5" : "\uEB9F";

        private static string Groesse(long bytes) => bytes >= 1024 * 1024
            ? $"{(bytes / 1024d / 1024d).ToString("0.0", Deutsch)} MB"
            : $"{Math.Max(1, bytes / 1024).ToString(Deutsch)} KB";

        private static Brush Ressource(string schluessel) => (Brush)Application.Current.Resources[schluessel];
    }
}
