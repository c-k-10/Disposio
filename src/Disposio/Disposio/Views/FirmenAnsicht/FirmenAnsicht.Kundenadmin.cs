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
    // Die Ansichten des Kundenadmins: K01 Benutzer verwalten, K02 Benutzer anlegen / bearbeiten
    // und K04 Passwort-Richtlinien. K03 (eigene Rollen) steht in FirmenAnsicht.EigeneRollen.cs.
    // „partial“ bedeutet: Das ist dieselbe Klasse wie in FirmenAnsicht.xaml.cs, nur in einer eigenen Datei.
    // So bleibt das Grundgerüst übersichtlich und die Bausteine (Card, Muted, Brush …) sind hier nutzbar.
    public sealed partial class FirmenAnsicht
    {
        private readonly KundenadminRepository _kundenadminRepository = new();

        // null: K02 legt ein neues Konto an. Sonst bearbeitet K02 genau dieses Konto.
        private FirmenBenutzer? _bearbeiteterBenutzer;

        private void OpenUserEditor(FirmenBenutzer? benutzer)
        {
            _bearbeiteterBenutzer = benutzer;
            Open("K02");
        }

        // ── K01 Benutzer verwalten ───────────────────────────────────────────────────────

        private UIElement CreateUsersView()
        {
            _bearbeiteterBenutzer = null;
            IReadOnlyList<FirmenBenutzer> alle = Array.Empty<FirmenBenutzer>();
            var root = new StackPanel { Spacing = 18 };

            // Hinweisleiste wie im Wireframe: Firma, Zahl der aktiven Konten und „Benutzer anlegen“.
            var summaryText = new TextBlock
            {
                Text = $"Unternehmen: {AktuellerFirmenname()} · wird geladen …",
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("TextBrush")
            };
            var createButton = new Button { Content = "+ Benutzer anlegen", Style = (Style)Resources["PrimaryButton"] };
            createButton.Click += (_, _) => OpenUserEditor(null);
            root.Children.Add(InfoBar(summaryText, createButton));

            // Filter: Name oder E-Mail, Status und „angelegt ab“. Gefiltert wird in der geladenen Liste.
            var searchBox = Eingabe("", "Begriff eingeben");
            var statusBox = Auswahl(new (string, object?)[] { ("Alle", null), ("Aktiv", true), ("Deaktiviert", false) }, null);
            var dateBox = new CalendarDatePicker
            {
                PlaceholderText = "Datum wählen",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 44
            };
            var filterButton = new Button
            {
                Content = "Filter anwenden",
                Style = (Style)Resources["DashboardButton"],
                VerticalAlignment = VerticalAlignment.Bottom
            };

            var filterRow = new Grid { ColumnSpacing = 14 };
            filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Platzieren(filterRow, Feld("Name oder E-Mail", searchBox), 0, 0);
            Platzieren(filterRow, Feld("Status", statusBox), 0, 1);
            Platzieren(filterRow, Feld("Angelegt ab", dateBox), 0, 2);
            Platzieren(filterRow, filterButton, 0, 3);
            root.Children.Add(filterRow);

            // Liste
            var loadText = Muted("Benutzer werden geladen …");
            var tableHost = new Border();
            var listContent = new StackPanel { Spacing = 14 };
            listContent.Children.Add(new TextBlock
            {
                Text = "Benutzer",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextBrush")
            });
            listContent.Children.Add(loadText);
            listContent.Children.Add(tableHost);
            root.Children.Add(Card(listContent));

            void ApplyFilter()
            {
                string begriff = searchBox.Text.Trim();
                object? status = Gewaehlt(statusBox);
                DateTime? ab = dateBox.Date?.Date;

                FirmenBenutzer[] treffer = alle.Where(benutzer =>
                    (begriff.Length == 0 ||
                     benutzer.Anzeigename.Contains(begriff, StringComparison.CurrentCultureIgnoreCase) ||
                     benutzer.Email.Contains(begriff, StringComparison.OrdinalIgnoreCase)) &&
                    (status is not bool aktiv || benutzer.Aktiv == aktiv) &&
                    (ab is null || benutzer.Erstellt.Date >= ab.Value)).ToArray();

                tableHost.Child = UsersTable(treffer);
                loadText.Text = treffer.Length == alle.Count
                    ? $"{alle.Count} Konten"
                    : $"{treffer.Length} von {alle.Count} Konten";
                loadText.Visibility = Visibility.Visible;
            }

            async Task LoadAsync()
            {
                loadText.Text = "Benutzer werden geladen …";
                tableHost.Child = null;
                try
                {
                    alle = await _kundenadminRepository.BenutzerLadenAsync();
                    summaryText.Text = $"Unternehmen: {AktuellerFirmenname()} · {alle.Count(b => b.Aktiv)} aktive Benutzer";
                    ApplyFilter();
                }
                catch (UnauthorizedAccessException exception)
                {
                    ReturnToLogin(exception.Message);
                }
                catch (Exception exception)
                {
                    loadText.Text = exception is InvalidOperationException
                        ? exception.Message
                        : "Die Benutzer konnten nicht geladen werden. Prüfe die Verbindung.";
                    var retry = new Button { Content = "Erneut laden", Style = (Style)Resources["DashboardButton"] };
                    retry.Click += async (_, _) => await LoadAsync();
                    tableHost.Child = retry;
                }
            }

            filterButton.Click += (_, _) => ApplyFilter();
            searchBox.KeyDown += (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    args.Handled = true;
                    ApplyFilter();
                }
            };

            _ = LoadAsync();
            return root;
        }

        private UIElement UsersTable(IReadOnlyList<FirmenBenutzer> benutzerListe)
        {
            if (benutzerListe.Count == 0)
                return Text("Keine Benutzer gefunden.");

            var table = new Grid { ColumnSpacing = 16 };
            foreach (double breite in new[] { 2.2, 1.3, 1.1, 0.9, 1.0 })
                table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(breite, GridUnitType.Star) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Kopfzeile mit hinterlegter Fläche wie im Wireframe.
            var header = new Border { Background = Brush("InputBrush"), CornerRadius = new CornerRadius(8) };
            Grid.SetColumnSpan(header, 6);
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            table.Children.Add(header);
            string[] titel = { "Name / E-Mail", "Rolle", "Abteilung", "Sprache", "Status", "" };
            for (int spalte = 0; spalte < titel.Length; spalte++)
                Platzieren(table, new Border { Padding = new Thickness(spalte == 0 ? 12 : 0, 10, 0, 10), Child = Cell(titel[spalte], true, false) }, 0, spalte);

            foreach (FirmenBenutzer benutzer in benutzerListe)
            {
                int zeile = table.RowDefinitions.Count;
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var name = new StackPanel { Spacing = 2 };
                name.Children.Add(Cell(benutzer.Anzeigename, false, bold: true));
                name.Children.Add(Muted(benutzer.Email));

                var bearbeiten = new Button
                {
                    Content = "Bearbeiten",
                    Style = (Style)Resources["DashboardButton"],
                    MinHeight = 36,
                    Padding = new Thickness(14, 4, 14, 4)
                };
                bearbeiten.Click += (_, _) => OpenUserEditor(benutzer);
                AutomationPropertiesName(bearbeiten, $"{benutzer.Anzeigename} bearbeiten");

                UIElement[] zellen =
                {
                    name,
                    Cell(benutzer.RollenText, false, false),
                    Cell(benutzer.Abteilung ?? "–", false, false),
                    Cell(SprachKatalog.Anzeige(benutzer.Sprache), false, false),
                    StatusPill(benutzer.Aktiv),
                    bearbeiten
                };
                for (int spalte = 0; spalte < zellen.Length; spalte++)
                {
                    var zelle = new Border
                    {
                        BorderBrush = Brush("BorderBrush"),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding = new Thickness(spalte == 0 ? 12 : 0, 12, 0, 12),
                        Child = zellen[spalte]
                    };
                    zelle.VerticalAlignment = VerticalAlignment.Stretch;
                    Platzieren(table, zelle, zeile, spalte);
                }
            }

            // In schmalen Fenstern brechen die Texte in den Spalten um.
            return table;
        }

        private Border StatusPill(bool aktiv) => new()
        {
            BorderBrush = Brush(aktiv ? "AccentBrush" : "BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 2, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = aktiv ? "Aktiv" : "Deaktiviert",
                FontSize = 13,
                Foreground = Brush(aktiv ? "TextBrush" : "MutedTextBrush")
            }
        };

        // ── K02 Benutzer anlegen / bearbeiten ────────────────────────────────────────────

        private UIElement CreateUserEditView()
        {
            FirmenBenutzer? benutzer = _bearbeiteterBenutzer;
            bool neu = benutzer is null;
            // Das eigene Konto: Rolle und Status bleiben gesperrt, sonst sperrt man sich selbst aus.
            bool eigenesKonto = benutzer is not null && benutzer.Id == BenutzerSitzung.AktuellerBenutzer?.Id;
            PageTitle.Text = neu ? "Benutzer anlegen" : "Benutzer bearbeiten";

            var firstNameBox = Eingabe(benutzer?.Vorname ?? "", "");
            var lastNameBox = Eingabe(benutzer?.Nachname ?? "", "");
            var emailBox = Eingabe(benutzer?.Email ?? "", "name@firma.de");
            firstNameBox.MaxLength = lastNameBox.MaxLength = 100;
            emailBox.MaxLength = 254;

            // Rollen (Standard + eigene aus K03) und Abteilungen werden gleich aus Neon geladen.
            // „Keine Abteilung“ gilt für unternehmensweite Rollen.
            var roleBox = Auswahl(Array.Empty<(string, object?)>(), null);
            roleBox.PlaceholderText = "Rolle wählen";
            var departmentBox = Auswahl(new (string, object?)[] { ("Keine Abteilung", null) }, null);
            roleBox.IsEnabled = departmentBox.IsEnabled = false;

            var languageBox = Auswahl(SprachKatalog.Alle.Select(sprache => (sprache.Anzeige, (object?)sprache.Wert)),
                benutzer?.Sprache ?? SprachKatalog.Standard);
            languageBox.PlaceholderText = "Sprache wählen";
            var statusBox = Auswahl(new (string, object?)[] { ("Aktiv", true), ("Deaktiviert", false) }, benutzer?.Aktiv ?? true);

            if (eigenesKonto)
                statusBox.IsEnabled = false;

            var passwordBox = PasswortEingabe();
            var repeatBox = PasswortEingabe();

            var form = new Grid { ColumnSpacing = 20, RowSpacing = 16 };
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Platzieren(form, Feld("Vorname *", firstNameBox), 0, 0);
            Platzieren(form, Feld("Nachname *", lastNameBox), 0, 1);
            Platzieren(form, Feld("E-Mail-Adresse *", emailBox), 1, 0);
            Platzieren(form, Feld("Rolle *", roleBox), 1, 1);
            Platzieren(form, Feld("Abteilung", departmentBox), 2, 0);
            Platzieren(form, Feld("Sprache *", languageBox), 2, 1);
            Platzieren(form, Feld("Kontostatus *", statusBox), 3, 0);
            if (neu)
            {
                // Einladungen werden im Prototyp nicht versendet. Deshalb vergibt der Kundenadmin ein
                // Startpasswort, wie beim Anlegen des Kundenadmins im Dashboard. Gespeichert wird nur der Hash.
                Platzieren(form, Feld("Startpasswort *", passwordBox), 4, 0);
                Platzieren(form, Feld("Startpasswort wiederholen *", repeatBox), 4, 1);
            }

            var formContent = new StackPanel { Spacing = 18 };
            formContent.Children.Add(new TextBlock
            {
                Text = "Kontodaten",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextBrush")
            });
            formContent.Children.Add(form);
            // Zeigt nach dem Laden die Mindestlänge aus der Passwort-Richtlinie (K04).
            var passwordHint = Muted("");
            passwordHint.Visibility = Visibility.Collapsed;
            formContent.Children.Add(passwordHint);
            if (eigenesKonto)
                formContent.Children.Add(Muted("Das ist Ihr eigenes Konto. Rolle und Kontostatus können Sie hier nicht ändern."));

            var errorText = new TextBlock
            {
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(ParseColor(_isLight ? "#B91C1C" : "#FF939B")),
                Visibility = Visibility.Collapsed
            };

            var cancelButton = new Button { Content = "Abbrechen", Style = (Style)Resources["DashboardButton"] };
            var saveButton = new Button { Content = "Speichern", Style = (Style)Resources["PrimaryButton"], IsEnabled = false };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(saveButton);

            var root = new StackPanel { Spacing = 18 };
            root.Children.Add(Muted("* Pflichtfeld"));
            root.Children.Add(Card(formContent));
            root.Children.Add(errorText);
            root.Children.Add(buttons);

            Button? statusButton = null;
            if (benutzer is not null)
            {
                statusButton = new Button
                {
                    Content = benutzer.Aktiv ? "Konto deaktivieren" : "Konto aktivieren",
                    Style = (Style)Resources["DashboardButton"],
                    IsEnabled = !eigenesKonto
                };
                root.Children.Add(InfoBar(Text(benutzer.Aktiv
                    ? "Deaktivieren entzieht den Zugang; vorhandene Vorgänge bleiben zugeordnet."
                    : "Dieses Konto ist deaktiviert. Aktivieren erlaubt wieder die Anmeldung."), statusButton));
            }
            root.Children.Add(Muted("Administratoren sehen keine Passwörter. Einladungen werden im Prototyp nicht versendet."));

            bool isSaving = false;
            void SetEnabled(bool enabled)
            {
                firstNameBox.IsEnabled = lastNameBox.IsEnabled = emailBox.IsEnabled = enabled;
                departmentBox.IsEnabled = languageBox.IsEnabled = enabled;
                roleBox.IsEnabled = statusBox.IsEnabled = enabled && !eigenesKonto;
                passwordBox.IsEnabled = repeatBox.IsEnabled = enabled;
                saveButton.IsEnabled = cancelButton.IsEnabled = enabled;
                if (statusButton is not null)
                    statusButton.IsEnabled = enabled && !eigenesKonto;
            }

            void ShowError(string message, Control? field = null)
            {
                errorText.Text = message;
                errorText.Visibility = Visibility.Visible;
                field?.Focus(FocusState.Programmatic);
            }

            async Task SaveAsync()
            {
                // Doppelklick oder Enter während des Speicherns darf kein zweites Konto anlegen.
                if (isSaving || !saveButton.IsEnabled)
                    return;

                errorText.Visibility = Visibility.Collapsed;
                if (Gewaehlt(roleBox) is not long rollenId)
                {
                    ShowError("Bitte eine Rolle auswählen.", roleBox);
                    return;
                }
                if (Gewaehlt(languageBox) is not string sprache)
                {
                    ShowError("Bitte eine Sprache auswählen.", languageBox);
                    return;
                }
                if (neu && !string.Equals(passwordBox.Password, repeatBox.Password, StringComparison.Ordinal))
                {
                    ShowError("Die beiden Passwörter stimmen nicht überein.", repeatBox);
                    return;
                }

                var entwurf = new FirmenBenutzerEntwurf(firstNameBox.Text, lastNameBox.Text, emailBox.Text, rollenId,
                    Gewaehlt(departmentBox) as long?, sprache, Gewaehlt(statusBox) as bool? ?? true,
                    neu ? passwordBox.Password : null);

                isSaving = true;
                SetEnabled(false);
                saveButton.Content = "Speichert …";
                try
                {
                    if (benutzer is null)
                        await _kundenadminRepository.BenutzerAnlegenAsync(entwurf);
                    else
                        await _kundenadminRepository.BenutzerAendernAsync(benutzer.Id, entwurf);

                    // Erst nach der Bestätigung aus Neon zurück zur Liste. Sie lädt dann neu.
                    isSaving = false;
                    Open("K01");
                    return;
                }
                catch (UnauthorizedAccessException exception)
                {
                    ReturnToLogin(exception.Message);
                    return;
                }
                catch (Exception exception) when (exception is ArgumentException or BenutzerEmailVergebenException
                                                       or InvalidOperationException)
                {
                    // Diese Meldungen stammen aus unserer eigenen Prüfung und sind verständlich formuliert.
                    ShowError(exception.Message);
                }
                catch (Exception)
                {
                    ShowError("Speichern konnte nicht bestätigt werden. Prüfe die Verbindung und lade die Liste neu, bevor du es erneut versuchst.");
                }

                isSaving = false;
                SetEnabled(true);
                saveButton.Content = "Speichern";
            }

            async Task ToggleStatusAsync()
            {
                if (benutzer is null || isSaving || XamlRoot is null)
                    return;

                bool neuerStatus = !benutzer.Aktiv;
                if (!neuerStatus)
                {
                    var confirm = new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        RequestedTheme = RequestedTheme,
                        Title = "Konto deaktivieren?",
                        Content = $"{benutzer.Anzeigename} kann sich danach nicht mehr anmelden. Vorhandene Vorgänge bleiben zugeordnet.",
                        PrimaryButtonText = "Deaktivieren",
                        CloseButtonText = "Abbrechen",
                        DefaultButton = ContentDialogButton.Close
                    };
                    if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                        return;
                }

                isSaving = true;
                SetEnabled(false);
                try
                {
                    await _kundenadminRepository.KontoStatusSetzenAsync(benutzer.Id, neuerStatus);
                    isSaving = false;
                    Open("K01");
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
                    ShowError("Der Status konnte nicht gespeichert werden. Prüfe die Verbindung.");
                }

                isSaving = false;
                SetEnabled(true);
            }

            async Task LoadChoicesAsync()
            {
                try
                {
                    IReadOnlyList<RollenAuswahl> rollen = await _kundenadminRepository.RollenLadenAsync();
                    // Bei mehreren alten Rollen startet das Formular mit der ersten, die vergeben werden darf.
                    long? startRolle = benutzer?.RollenIds.Cast<long?>().FirstOrDefault(id => rollen.Any(r => r.Id == id));
                    foreach (RollenAuswahl rolle in rollen)
                    {
                        var item = new ComboBoxItem { Content = rolle.Anzeige, Tag = rolle.Id };
                        roleBox.Items.Add(item);
                        if (rolle.Id == startRolle)
                            roleBox.SelectedItem = item;
                    }

                    IReadOnlyList<AbteilungAuswahl> abteilungen = await _kundenadminRepository.AbteilungenLadenAsync();
                    foreach (AbteilungAuswahl abteilung in abteilungen)
                    {
                        var item = new ComboBoxItem { Content = abteilung.Bezeichnung, Tag = abteilung.Id };
                        departmentBox.Items.Add(item);
                        if (abteilung.Id == benutzer?.AbteilungId)
                            departmentBox.SelectedItem = item;
                    }

                    if (neu)
                    {
                        PasswortRichtlinie richtlinie = await _kundenadminRepository.PasswortRichtlinieLadenAsync();
                        passwordHint.Text = $"Das Startpasswort braucht mindestens {richtlinie.Mindestlaenge} Zeichen " +
                            "(Passwort-Richtlinie Ihres Unternehmens). Teilen Sie es dem Benutzer persönlich mit.";
                        passwordHint.Visibility = Visibility.Visible;
                    }

                    SetEnabled(true);
                    firstNameBox.Focus(FocusState.Programmatic);
                }
                catch (UnauthorizedAccessException exception)
                {
                    ReturnToLogin(exception.Message);
                }
                catch (Exception exception)
                {
                    ShowError(exception is InvalidOperationException
                        ? exception.Message
                        : "Rollen und Abteilungen konnten nicht geladen werden. Bitte die Ansicht neu öffnen.");
                }
            }

            saveButton.Click += async (_, _) => await SaveAsync();
            cancelButton.Click += (_, _) => { if (!isSaving) Open("K01"); };
            if (statusButton is not null)
                statusButton.Click += async (_, _) => await ToggleStatusAsync();
            foreach (TextBox field in new[] { firstNameBox, lastNameBox, emailBox })
            {
                field.TextChanged += (_, _) => errorText.Visibility = Visibility.Collapsed;
                field.KeyDown += async (_, args) =>
                {
                    if (args.Key == Windows.System.VirtualKey.Enter)
                    {
                        args.Handled = true;
                        await SaveAsync();
                    }
                };
            }

            _ = LoadChoicesAsync();
            return root;
        }

        // ── K04 Passwort-Richtlinien ─────────────────────────────────────────────────────

        private UIElement CreatePasswordPolicyView()
        {
            var root = new StackPanel { Spacing = 18 };
            root.Children.Add(InfoBar(Text($"Diese Richtlinie gilt für alle Benutzer von {AktuellerFirmenname()}."), null));
            root.Children.Add(Muted("* Pflichtfeld"));

            NumberBox Zahl(int minimum, int maximum) => new()
            {
                Minimum = minimum,
                Maximum = maximum,
                SmallChange = 1,
                LargeChange = 5,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = false
            };
            var lengthBox = Zahl(8, 72);
            var attemptsBox = Zahl(3, 20);
            var lockBox = Zahl(1, 1440);
            var idleBox = Zahl(5, 480);
            var commonBox = new CheckBox
            {
                Content = "Häufige und bekannte kompromittierte Passwörter sperren",
                Foreground = Brush("TextBrush"),
                IsEnabled = false
            };

            var form = new Grid { ColumnSpacing = 20, RowSpacing = 16 };
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Platzieren(form, Feld("Mindestlänge *", lengthBox), 0, 0);
            Platzieren(form, Feld("Fehlversuche bis temporäre Sperre *", attemptsBox), 0, 1);
            Platzieren(form, Feld("Sperrdauer (Minuten) *", lockBox), 1, 0);
            Platzieren(form, Feld("Inaktivität bis Abmeldung (Minuten) *", idleBox), 1, 1);

            var content = new StackPanel { Spacing = 18 };
            content.Children.Add(new TextBlock
            {
                Text = "Richtlinie",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextBrush")
            });
            content.Children.Add(form);
            content.Children.Add(commonBox);
            root.Children.Add(Card(content));

            var messageText = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            root.Children.Add(messageText);

            var cancelButton = new Button { Content = "Abbrechen", Style = (Style)Resources["DashboardButton"], IsEnabled = false };
            var saveButton = new Button { Content = "Richtlinie speichern", Style = (Style)Resources["PrimaryButton"], IsEnabled = false };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(saveButton);
            root.Children.Add(buttons);
            root.Children.Add(Muted("Die Mindestlänge und die Sperrliste gelten sofort für neue Startpasswörter. " +
                "Sperre nach Fehlversuchen und automatische Abmeldung werden gespeichert; die Anmeldung setzt sie in einem späteren Schritt um."));

            void ShowMessage(string message, bool error)
            {
                messageText.Text = message;
                messageText.Foreground = error
                    ? new SolidColorBrush(ParseColor(_isLight ? "#B91C1C" : "#FF939B"))
                    : Brush("TextBrush");
                messageText.Visibility = Visibility.Visible;
            }

            void SetEnabled(bool enabled)
            {
                lengthBox.IsEnabled = attemptsBox.IsEnabled = lockBox.IsEnabled = idleBox.IsEnabled = enabled;
                commonBox.IsEnabled = saveButton.IsEnabled = cancelButton.IsEnabled = enabled;
            }

            void Show(PasswortRichtlinie richtlinie)
            {
                lengthBox.Value = richtlinie.Mindestlaenge;
                attemptsBox.Value = richtlinie.Fehlversuche;
                lockBox.Value = richtlinie.SperrdauerMinuten;
                idleBox.Value = richtlinie.InaktivitaetMinuten;
                commonBox.IsChecked = richtlinie.BekanntePasswoerterSperren;
            }

            async Task LoadAsync()
            {
                SetEnabled(false);
                try
                {
                    Show(await _kundenadminRepository.PasswortRichtlinieLadenAsync());
                    SetEnabled(true);
                }
                catch (UnauthorizedAccessException exception)
                {
                    ReturnToLogin(exception.Message);
                }
                catch (Exception exception)
                {
                    ShowMessage(exception is InvalidOperationException
                        ? exception.Message
                        : "Die Richtlinie konnte nicht geladen werden. Prüfe die Verbindung.", error: true);
                }
            }

            bool isSaving = false;
            saveButton.Click += async (_, _) =>
            {
                if (isSaving)
                    return;

                // Ein leeres Zahlenfeld liefert NaN. Dann bleibt das Speichern mit einer Meldung stehen.
                double[] werte = { lengthBox.Value, attemptsBox.Value, lockBox.Value, idleBox.Value };
                if (werte.Any(double.IsNaN))
                {
                    ShowMessage("Bitte alle Pflichtfelder ausfüllen.", error: true);
                    return;
                }

                var richtlinie = new PasswortRichtlinie((int)lengthBox.Value, (int)attemptsBox.Value,
                    (int)lockBox.Value, (int)idleBox.Value, commonBox.IsChecked == true);
                isSaving = true;
                SetEnabled(false);
                saveButton.Content = "Speichert …";
                try
                {
                    await _kundenadminRepository.PasswortRichtlinieSpeichernAsync(richtlinie);
                    ShowMessage("Die Passwort-Richtlinie wurde gespeichert.", error: false);
                }
                catch (UnauthorizedAccessException exception)
                {
                    ReturnToLogin(exception.Message);
                    return;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    ShowMessage(exception.Message, error: true);
                }
                catch (Exception)
                {
                    ShowMessage("Speichern konnte nicht bestätigt werden. Prüfe die Verbindung und lade die Ansicht neu.", error: true);
                }

                isSaving = false;
                SetEnabled(true);
                saveButton.Content = "Richtlinie speichern";
            };
            // Abbrechen verwirft die Eingaben und zeigt wieder die gespeicherte Richtlinie.
            cancelButton.Click += async (_, _) =>
            {
                if (isSaving)
                    return;
                messageText.Visibility = Visibility.Collapsed;
                await LoadAsync();
            };

            _ = LoadAsync();
            return root;
        }

        // ── Bausteine für Formulare ──────────────────────────────────────────────────────

        private string AktuellerFirmenname() =>
            BenutzerSitzung.Firmenzugang?.Firmenname ?? BenutzerSitzung.AktuellerBenutzer?.Firmenname ?? "";

        // Leiste mit Akzentstrich links, Text und optional einem Button rechts (K01, K02, K04).
        private Border InfoBar(FrameworkElement text, Button? button)
        {
            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var strich = new Border { Background = Brush("AccentBrush"), CornerRadius = new CornerRadius(2) };
            Platzieren(grid, strich, 0, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            Platzieren(grid, text, 0, 1);
            if (button is not null)
                Platzieren(grid, button, 0, 2);
            return new Border
            {
                Background = Brush("SurfaceBrush"),
                BorderBrush = Brush("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 12, 10),
                Child = grid
            };
        }

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
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, label.TrimEnd(' ', '*'));
            return feld;
        }

        private TextBox Eingabe(string text, string placeholder) => new()
        {
            Text = text,
            PlaceholderText = placeholder,
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

        // PasswordBox zeigt Punkte statt Klartext. 72 Bytes ist die Grenze von BCrypt.
        private PasswordBox PasswortEingabe() => new()
        {
            Background = Brush("InputBrush"),
            Foreground = Brush("TextBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            MinHeight = 44,
            Padding = new Thickness(12, 9, 12, 9),
            FontSize = 16,
            MaxLength = 72,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // Auswahlliste: Jeder Eintrag zeigt einen Text und trägt im Tag den Wert für die Datenbank.
        private ComboBox Auswahl(IEnumerable<(string Text, object? Wert)> eintraege, object? gewaehlt)
        {
            var box = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 44,
                FontSize = 16,
                CornerRadius = new CornerRadius(10)
            };
            foreach (var (text, wert) in eintraege)
            {
                var item = new ComboBoxItem { Content = text, Tag = wert };
                box.Items.Add(item);
                if (Equals(wert, gewaehlt))
                    box.SelectedItem = item;
            }
            return box;
        }

        private static object? Gewaehlt(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag;

        private static void Platzieren(Grid grid, FrameworkElement element, int zeile, int spalte)
        {
            while (grid.RowDefinitions.Count <= zeile)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(element, zeile);
            Grid.SetColumn(element, spalte);
            grid.Children.Add(element);
        }

        private static void AutomationPropertiesName(UIElement element, string name) =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
    }
}
