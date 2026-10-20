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
    // K03 Rollen & Rechte: Der Kundenadmin legt eigene Rollen an und hakt einzelne Berechtigungen an.
    // Außerdem steht hier, wie eigene Rollen im Menü erscheinen. Gleiche Klasse wie FirmenAnsicht.xaml.cs.
    public sealed partial class FirmenAnsicht
    {
        // Eigene Rollen des angemeldeten Kontos. Vorschau und Admin-Zugang zeigen nur die Standardrollen.
        private IReadOnlyList<EigeneRolle> _eigeneRollen = Array.Empty<EigeneRolle>();

        private static IReadOnlyList<EigeneRolle> EigeneRollenDerSitzung() =>
            BenutzerSitzung.IstFirmenVorschau || BenutzerSitzung.Firmenzugang is not null
                ? Array.Empty<EigeneRolle>()
                : BenutzerSitzung.AktuellerBenutzer?.EigeneRollen ?? Array.Empty<EigeneRolle>();

        // Menüpunkte einer eigenen Rolle in der Reihenfolge des Katalogs. K01–K04 nie.
        private static IReadOnlyList<AnsichtInfo> EigeneMenuepunkte(EigeneRolle rolle) =>
            AnsichtenKatalog.Alle.Where(ansicht => ansicht.Menuename is not null &&
                ansicht.Rolle != FirmenRolle.Kundenadmin && rolle.Ansichten.Contains(ansicht.Id)).ToArray();

        private int EigeneMenuepunkteGesamt() => _eigeneRollen.Sum(rolle => EigeneMenuepunkte(rolle).Count);

        private string ErsteEigeneAnsicht() => _eigeneRollen.SelectMany(EigeneMenuepunkte).First().Id;

        // Wird am Ende von BuildMenu aufgerufen: je eigene Rolle ein Abschnitt wie bei den Standardrollen.
        private void EigeneRollenMenue(string activeId)
        {
            foreach (EigeneRolle rolle in _eigeneRollen)
            {
                IReadOnlyList<AnsichtInfo> punkte = EigeneMenuepunkte(rolle);
                if (punkte.Count == 0)
                    continue;

                MenuPanel.Children.Add(new TextBlock
                {
                    Text = rolle.Rolle.ToUpper(CultureInfo.CurrentCulture),
                    FontSize = 12,
                    CharacterSpacing = 100,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("MutedTextBrush"),
                    Margin = new Thickness(4, MenuPanel.Children.Count == 0 ? 0 : 18, 0, 6)
                });

                foreach (AnsichtInfo item in punkte)
                {
                    var button = new Button { Content = item.Menuename, Style = (Style)Resources["MenuButton"] };
                    if (item.Id == activeId)
                    {
                        button.Background = Brush("AccentBrush");
                        button.Foreground = Brush("AccentTextBrush");
                        button.FontWeight = FontWeights.SemiBold;
                        button.Loaded += (_, _) => button.StartBringIntoView();
                    }
                    button.Click += (_, _) => Open(item.Id);
                    MenuPanel.Children.Add(button);
                }
            }
        }

        // ── K03 Rollen & Rechte ──────────────────────────────────────────────────────────

        private UIElement CreateRollenUndRechteView()
        {
            var root = new StackPanel { Spacing = 22 };

            // Oben die Standardrollen mit ihren Aufgaben (Tabelle aus dem Grundgerüst).
            root.Children.Add(CreateRolesView());

            // Darunter die eigenen Rollen des Unternehmens.
            var newButton = new Button { Content = "+ Neue Rolle", Style = (Style)Resources["PrimaryButton"], IsEnabled = false };
            var header = new Grid { ColumnSpacing = 16 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Platzieren(header, new TextBlock
            {
                Text = "Eigene Rollen",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("TextBrush")
            }, 0, 0);
            Platzieren(header, newButton, 0, 1);

            var statusText = Muted("Eigene Rollen werden geladen …");
            var listHost = new Border();
            var editorHost = new Border { Visibility = Visibility.Collapsed };

            var content = new StackPanel { Spacing = 14 };
            content.Children.Add(header);
            content.Children.Add(Muted("Eine eigene Rolle schaltet genau die angehakten Bereiche frei. " +
                "Benutzer bekommen sie unter „Benutzer“ wie jede andere Rolle. Änderungen gelten ab der nächsten Anmeldung."));
            content.Children.Add(statusText);
            content.Children.Add(listHost);
            content.Children.Add(editorHost);
            root.Children.Add(Card(content));

            IReadOnlyList<Berechtigung> berechtigungen = Array.Empty<Berechtigung>();

            async Task LoadAsync()
            {
                statusText.Text = "Eigene Rollen werden geladen …";
                statusText.Visibility = Visibility.Visible;
                try
                {
                    if (berechtigungen.Count == 0)
                        berechtigungen = await _kundenadminRepository.BerechtigungenLadenAsync();
                    IReadOnlyList<EigeneRolleDaten> rollen = await _kundenadminRepository.EigeneRollenLadenAsync();

                    listHost.Child = EigeneRollenTabelle(rollen, berechtigungen, rolle => ShowEditor(rolle));
                    statusText.Visibility = rollen.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    statusText.Text = "Noch keine eigenen Rollen angelegt.";
                    newButton.IsEnabled = true;
                }
                catch (UnauthorizedAccessException exception)
                {
                    ReturnToLogin(exception.Message);
                }
                catch (Exception exception)
                {
                    statusText.Text = exception is InvalidOperationException
                        ? exception.Message
                        : "Die eigenen Rollen konnten nicht geladen werden. Wurde 003_eigene_rollen.sql in Neon ausgeführt?";
                }
            }

            void ShowEditor(EigeneRolleDaten? rolle)
            {
                newButton.IsEnabled = false;
                editorHost.Child = EigeneRolleEditor(rolle, berechtigungen,
                    gespeichert: async () =>
                    {
                        editorHost.Child = null;
                        editorHost.Visibility = Visibility.Collapsed;
                        await LoadAsync();
                    },
                    abgebrochen: () =>
                    {
                        editorHost.Child = null;
                        editorHost.Visibility = Visibility.Collapsed;
                        newButton.IsEnabled = true;
                    });
                editorHost.Visibility = Visibility.Visible;
                editorHost.StartBringIntoView();
            }

            newButton.Click += (_, _) => ShowEditor(null);
            _ = LoadAsync();
            return root;
        }

        private UIElement EigeneRollenTabelle(IReadOnlyList<EigeneRolleDaten> rollen, IReadOnlyList<Berechtigung> berechtigungen,
            Action<EigeneRolleDaten> bearbeiten)
        {
            if (rollen.Count == 0)
                return new Border();

            var table = new Grid { ColumnSpacing = 16 };
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.6, GridUnitType.Star) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string[] titel = { "Rolle", "Berechtigungen", "Benutzer", "" };
            for (int spalte = 0; spalte < titel.Length; spalte++)
                Platzieren(table, new Border { Padding = new Thickness(0, 6, 0, 6), Child = Cell(titel[spalte], true, false) }, 0, spalte);

            foreach (EigeneRolleDaten rolle in rollen)
            {
                int zeile = table.RowDefinitions.Count;
                string rechte = string.Join(", ", berechtigungen.Where(b => rolle.BerechtigungIds.Contains(b.Id)).Select(b => b.Bezeichnung));
                var button = new Button
                {
                    Content = "Bearbeiten",
                    Style = (Style)Resources["DashboardButton"],
                    MinHeight = 36,
                    Padding = new Thickness(14, 4, 14, 4)
                };
                button.Click += (_, _) => bearbeiten(rolle);

                UIElement[] zellen =
                {
                    Cell(rolle.Bezeichnung, false, bold: true),
                    Cell(rechte.Length == 0 ? "Keine" : rechte, false, false),
                    Cell(rolle.Benutzer.ToString(CultureInfo.CurrentCulture), false, false),
                    button
                };
                for (int spalte = 0; spalte < zellen.Length; spalte++)
                {
                    Platzieren(table, new Border
                    {
                        BorderBrush = Brush("BorderBrush"),
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        Padding = new Thickness(0, 12, 0, 12),
                        Child = zellen[spalte]
                    }, zeile, spalte);
                }
            }
            return table;
        }

        private UIElement EigeneRolleEditor(EigeneRolleDaten? rolle, IReadOnlyList<Berechtigung> berechtigungen,
            Func<Task> gespeichert, Action abgebrochen)
        {
            var nameBox = Eingabe(rolle?.Bezeichnung ?? "", "z. B. Lagerist oder Disponent Nachtschicht");
            nameBox.MaxLength = EigeneRollenRegeln.MaximaleLaenge;

            var editor = new StackPanel { Spacing = 14 };
            editor.Children.Add(new TextBlock
            {
                Text = rolle is null ? "Neue Rolle anlegen" : $"Rolle „{rolle.Bezeichnung}“ bearbeiten",
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextBrush")
            });
            editor.Children.Add(Feld("Rollenname *", nameBox));
            editor.Children.Add(new TextBlock
            {
                Text = "Berechtigungen *",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextBrush")
            });

            // Häkchen nach Bereich gruppiert (Disposition, Fuhrpark …). Zwei Spalten, damit die Liste kurz bleibt.
            var checkBoxes = new List<(CheckBox Box, Berechtigung Recht)>();
            foreach (IGrouping<string, Berechtigung> bereich in berechtigungen.GroupBy(b => b.Bereich))
            {
                editor.Children.Add(Muted(bereich.Key));
                var grid = new Grid { ColumnSpacing = 16 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int index = 0;
                foreach (Berechtigung recht in bereich)
                {
                    var box = new CheckBox
                    {
                        Content = recht.Bezeichnung,
                        IsChecked = rolle?.BerechtigungIds.Contains(recht.Id) == true,
                        Foreground = Brush("TextBrush")
                    };
                    checkBoxes.Add((box, recht));
                    Platzieren(grid, box, index / 2, index % 2);
                    index++;
                }
                editor.Children.Add(grid);
            }

            var errorText = new TextBlock
            {
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(ParseColor(_isLight ? "#B91C1C" : "#FF939B")),
                Visibility = Visibility.Collapsed
            };
            editor.Children.Add(errorText);

            var cancelButton = new Button { Content = "Abbrechen", Style = (Style)Resources["DashboardButton"] };
            var saveButton = new Button { Content = "Rolle speichern", Style = (Style)Resources["PrimaryButton"] };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
            Button? deleteButton = null;
            if (rolle is not null)
            {
                deleteButton = new Button { Content = "Rolle löschen", Style = (Style)Resources["DashboardButton"] };
                buttons.Children.Add(deleteButton);
            }
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(saveButton);
            editor.Children.Add(buttons);

            bool isSaving = false;
            void SetEnabled(bool enabled)
            {
                nameBox.IsEnabled = saveButton.IsEnabled = cancelButton.IsEnabled = enabled;
                foreach (var (box, _) in checkBoxes)
                    box.IsEnabled = enabled;
                if (deleteButton is not null)
                    deleteButton.IsEnabled = enabled;
            }

            void ShowError(string message)
            {
                errorText.Text = message;
                errorText.Visibility = Visibility.Visible;
            }

            async Task RunAsync(Func<Task> aktion, string fehlertext)
            {
                if (isSaving)
                    return;

                isSaving = true;
                SetEnabled(false);
                errorText.Visibility = Visibility.Collapsed;
                try
                {
                    await aktion();
                    isSaving = false;
                    await gespeichert();
                    return;
                }
                catch (UnauthorizedAccessException exception)
                {
                    ReturnToLogin(exception.Message);
                    return;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    ShowError(exception.Message);
                }
                catch (Exception)
                {
                    ShowError(fehlertext);
                }

                isSaving = false;
                SetEnabled(true);
            }

            saveButton.Click += async (_, _) =>
            {
                long[] ids = checkBoxes.Where(c => c.Box.IsChecked == true).Select(c => c.Recht.Id).ToArray();
                await RunAsync(() => _kundenadminRepository.EigeneRolleSpeichernAsync(rolle?.Id, nameBox.Text, ids),
                    "Die Rolle konnte nicht gespeichert werden. Prüfe die Verbindung.");
            };
            cancelButton.Click += (_, _) => { if (!isSaving) abgebrochen(); };
            if (deleteButton is not null && rolle is not null)
            {
                deleteButton.Click += async (_, _) =>
                {
                    if (isSaving || XamlRoot is null)
                        return;

                    var confirm = new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        RequestedTheme = RequestedTheme,
                        Title = "Rolle löschen?",
                        Content = $"Die Rolle „{rolle.Bezeichnung}“ und ihre Berechtigungen werden gelöscht.",
                        PrimaryButtonText = "Löschen",
                        CloseButtonText = "Abbrechen",
                        DefaultButton = ContentDialogButton.Close
                    };
                    if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                        return;

                    await RunAsync(() => _kundenadminRepository.EigeneRolleLoeschenAsync(rolle.Id),
                        "Die Rolle konnte nicht gelöscht werden. Prüfe die Verbindung.");
                };
            }

            return new Border
            {
                Background = Brush("BackgroundBrush"),
                BorderBrush = Brush("AccentBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(20, 18, 20, 18),
                Child = editor
            };
        }
    }
}
