using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Disposio.Views
{
    /// <summary>
    /// Aktionsprotokoll als Tabelle: Datum/Uhrzeit, Wer, (Firma), Aktion, Details. Nur lesen.
    /// Wird an zwei Stellen benutzt: K05 in der Firmenansicht (eigene Firma) und im Dashboard (alle Firmen).
    /// Farben und Stile kommen von der Seite, die die Ansicht zeigt, damit Hell/Dunkel und Akzentfarbe passen.
    /// </summary>
    public sealed class AktionsprotokollAnsicht : UserControl
    {
        private readonly ProtokollRepository _repository = new();
        private readonly bool _alleFirmen;
        private readonly Func<string, object> _ressource;
        private readonly Action _zugriffBeenden;
        private readonly List<ProtokollEintrag> _eintraege = new();

        private readonly TextBox _suchBox;
        private readonly ComboBox _aktionBox;
        private readonly ComboBox? _firmaBox;
        private readonly TextBlock _ladeText;
        private readonly Border _tabelleHost = new();
        private readonly Button _mehrButton;
        private bool _laedt;

        /// <param name="alleFirmen">true im Dashboard: alle Firmen und die Spalte Firma.</param>
        /// <param name="ressource">Liest einen Brush oder Style der Seite, zum Beispiel „TextBrush“.</param>
        /// <param name="zugriffBeenden">Wird aufgerufen, wenn die Berechtigung fehlt. Die Seite meldet dann ab.</param>
        public AktionsprotokollAnsicht(bool alleFirmen, Func<string, object> ressource, Action zugriffBeenden)
        {
            _alleFirmen = alleFirmen;
            _ressource = ressource;
            _zugriffBeenden = zugriffBeenden;

            var root = new StackPanel { Spacing = 18 };

            // Filter wie in K01: Suchbegriff, Aktion (und im Dashboard die Firma). Gefiltert wird in der geladenen Liste.
            _suchBox = new TextBox
            {
                PlaceholderText = "Begriff eingeben",
                Background = Brush("InputBrush"),
                Foreground = Brush("TextBrush"),
                BorderBrush = Brush("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                MinHeight = 44,
                Padding = new Thickness(12, 9, 12, 9),
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _aktionBox = Auswahl();
            if (alleFirmen)
                _firmaBox = Auswahl();
            var filterButton = new Button
            {
                Content = "Filter anwenden",
                Style = (Style)_ressource("DashboardButton"),
                VerticalAlignment = VerticalAlignment.Bottom
            };

            var filterRow = new Grid { ColumnSpacing = 14 };
            filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            if (_firmaBox is not null)
                filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int spalte = 0;
            Platzieren(filterRow, Feld("Wer, Aktion oder Details", _suchBox), spalte++);
            Platzieren(filterRow, Feld("Aktion", _aktionBox), spalte++);
            if (_firmaBox is not null)
                Platzieren(filterRow, Feld("Firma", _firmaBox), spalte++);
            Platzieren(filterRow, filterButton, spalte);
            root.Children.Add(filterRow);

            // Liste in einer Karte wie die Benutzerliste.
            _ladeText = Muted("Einträge werden geladen …");
            _mehrButton = new Button
            {
                Content = "Mehr laden",
                Style = (Style)_ressource("DashboardButton"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed
            };
            var karte = new StackPanel { Spacing = 14 };
            karte.Children.Add(new TextBlock
            {
                Text = "Einträge",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextBrush")
            });
            karte.Children.Add(_ladeText);
            karte.Children.Add(_tabelleHost);
            karte.Children.Add(_mehrButton);
            root.Children.Add(new Border
            {
                Background = Brush("SurfaceBrush"),
                BorderBrush = Brush("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(26, 24, 26, 24),
                Child = karte
            });

            filterButton.Click += (_, _) => FilterAnwenden();
            _suchBox.KeyDown += (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    args.Handled = true;
                    FilterAnwenden();
                }
            };
            _mehrButton.Click += async (_, _) => await LadenAsync();

            Content = root;
            _ = LadenAsync();
        }

        // Lädt die nächsten Einträge (200 Stück) und hängt sie an die Liste an.
        private async Task LadenAsync()
        {
            if (_laedt)
                return;
            _laedt = true;
            _mehrButton.IsEnabled = false;
            _ladeText.Text = "Einträge werden geladen …";
            try
            {
                IReadOnlyList<ProtokollEintrag> neue = _alleFirmen
                    ? await _repository.AlleLadenAsync(_eintraege.Count)
                    : await _repository.FirmaLadenAsync(_eintraege.Count);
                _eintraege.AddRange(neue);
                _mehrButton.Visibility = neue.Count == ProtokollRepository.Seitengroesse ? Visibility.Visible : Visibility.Collapsed;
                AuswahlFuellen(_aktionBox, _eintraege.Select(eintrag => eintrag.Aktion));
                if (_firmaBox is not null)
                    AuswahlFuellen(_firmaBox, _eintraege.Select(eintrag => eintrag.Firma).OfType<string>());
                FilterAnwenden();
            }
            catch (UnauthorizedAccessException)
            {
                _zugriffBeenden();
            }
            catch (Exception exception)
            {
                _ladeText.Text = exception is InvalidOperationException
                    ? exception.Message
                    : "Das Protokoll konnte nicht geladen werden. Prüfe die Verbindung.";
                if (_eintraege.Count == 0)
                {
                    var retry = new Button { Content = "Erneut laden", Style = (Style)_ressource("DashboardButton") };
                    retry.Click += async (_, _) => await LadenAsync();
                    _tabelleHost.Child = retry;
                }
            }
            finally
            {
                _laedt = false;
                _mehrButton.IsEnabled = true;
            }
        }

        private void FilterAnwenden()
        {
            string begriff = _suchBox.Text.Trim();
            string? aktion = Gewaehlt(_aktionBox);
            string? firma = _firmaBox is null ? null : Gewaehlt(_firmaBox);

            ProtokollEintrag[] treffer = _eintraege.Where(eintrag =>
                (begriff.Length == 0 ||
                 eintrag.Wer.Contains(begriff, StringComparison.CurrentCultureIgnoreCase) ||
                 eintrag.Aktion.Contains(begriff, StringComparison.CurrentCultureIgnoreCase) ||
                 eintrag.Details.Contains(begriff, StringComparison.CurrentCultureIgnoreCase)) &&
                (aktion is null || eintrag.Aktion == aktion) &&
                (firma is null || eintrag.Firma == firma)).ToArray();

            _tabelleHost.Child = Tabelle(treffer);
            _ladeText.Text = treffer.Length == _eintraege.Count
                ? $"{_eintraege.Count} Einträge, neueste zuerst"
                : $"{treffer.Length} von {_eintraege.Count} Einträgen";
        }

        private UIElement Tabelle(IReadOnlyList<ProtokollEintrag> eintraege)
        {
            if (eintraege.Count == 0)
                return Muted("Keine Einträge gefunden.");

            var spalten = new List<(string Titel, double Breite)> { ("Datum / Uhrzeit", 1.1), ("Wer", 1.3) };
            if (_alleFirmen)
                spalten.Add(("Firma", 1.2));
            spalten.Add(("Aktion", 1.3));
            spalten.Add(("Details", 2.6));

            var table = new Grid { ColumnSpacing = 16 };
            foreach (var (_, breite) in spalten)
                table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(breite, GridUnitType.Star) });

            // Kopfzeile mit hinterlegter Fläche wie in K01.
            var header = new Border { Background = Brush("InputBrush"), CornerRadius = new CornerRadius(8) };
            Grid.SetColumnSpan(header, spalten.Count);
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            table.Children.Add(header);
            for (int spalte = 0; spalte < spalten.Count; spalte++)
                Platzieren(table, new Border { Padding = new Thickness(spalte == 0 ? 12 : 0, 10, 0, 10), Child = Zelle(spalten[spalte].Titel, kopf: true) }, spalte);

            foreach (ProtokollEintrag eintrag in eintraege)
            {
                var werte = new List<string>
                {
                    eintrag.Zeitpunkt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                    eintrag.Wer
                };
                if (_alleFirmen)
                    werte.Add(eintrag.Firma ?? "–");
                werte.Add(eintrag.Aktion);
                werte.Add(eintrag.Details.Length == 0 ? "–" : eintrag.Details);

                int zeile = table.RowDefinitions.Count;
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int spalte = 0; spalte < werte.Count; spalte++)
                {
                    var zelle = new Border
                    {
                        BorderBrush = Brush("BorderBrush"),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding = new Thickness(spalte == 0 ? 12 : 0, 12, 0, 12),
                        Child = Zelle(werte[spalte], kopf: false)
                    };
                    Grid.SetRow(zelle, zeile);
                    Grid.SetColumn(zelle, spalte);
                    table.Children.Add(zelle);
                }
            }

            return table;
        }

        // ── Kleine Bausteine (wie in FirmenAnsicht.Kundenadmin.cs) ─────────────────────────

        private SolidColorBrush Brush(string key) => (SolidColorBrush)_ressource(key);

        private TextBlock Zelle(string text, bool kopf) => new()
        {
            Text = text,
            FontSize = kopf ? 14 : 16,
            FontWeight = kopf ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(kopf ? "MutedTextBrush" : "TextBrush")
        };

        private TextBlock Muted(string text) => new()
        {
            Text = text,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("MutedTextBrush")
        };

        private StackPanel Feld(string label, Control input)
        {
            var feld = new StackPanel { Spacing = 6 };
            feld.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextBrush")
            });
            feld.Children.Add(input);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, label);
            return feld;
        }

        private static ComboBox Auswahl()
        {
            var box = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 44,
                FontSize = 16,
                CornerRadius = new CornerRadius(10)
            };
            box.Items.Add(new ComboBoxItem { Content = "Alle", Tag = null });
            box.SelectedIndex = 0;
            return box;
        }

        // Füllt eine Auswahl mit den vorkommenden Werten. Die bisherige Wahl bleibt erhalten.
        private static void AuswahlFuellen(ComboBox box, IEnumerable<string> werte)
        {
            string? bisher = Gewaehlt(box);
            box.Items.Clear();
            box.Items.Add(new ComboBoxItem { Content = "Alle", Tag = null });
            box.SelectedIndex = 0;
            foreach (string wert in werte.Distinct().OrderBy(wert => wert, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new ComboBoxItem { Content = wert, Tag = wert };
                box.Items.Add(item);
                if (wert == bisher)
                    box.SelectedItem = item;
            }
        }

        private static string? Gewaehlt(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

        private static void Platzieren(Grid grid, FrameworkElement element, int spalte)
        {
            Grid.SetColumn(element, spalte);
            grid.Children.Add(element);
        }
    }
}
