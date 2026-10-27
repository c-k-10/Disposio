using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Mail;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading;
using Disposio.Models;
using Disposio.Services;

namespace Disposio.Views
{
    // Die Seite kümmert sich um Anzeige und Eingaben. SQL gehört in UnternehmenRepository.
    // Datenfluss: Formular -> Entwurf -> Repository -> Neon -> gespeicherter Datensatz -> Anzeige.
    public sealed partial class DashboardAnsicht : Page
    {
        private static readonly string[] AccentColors =
        {
            "#FF004D", "#007BFF", "#00C853", "#FF9100", "#7B2CBF"
        };

        private readonly HashSet<Kunde> _observedCustomers = new();
        private readonly IUnternehmenRepository _unternehmenRepository = new UnternehmenRepository();
        private readonly IBenutzerRepository _benutzerRepository = new BenutzerRepository();
        private CancellationTokenSource? _loadCancellation;
        private bool _isLoading;
        private bool _hasDataLoaded;
        private bool _canEditCustomers;
        private bool _isInitialized;
        private bool _dialogOpen;
        private bool _returningToLogin;
        private bool _isLight;
        private string _accentHex = "#7B2CBF";

        // ObservableCollection meldet der XAML-Liste automatisch, wenn ein Eintrag hinzukommt.
        // Sie ist nur die Anzeige-Kopie: Dauerhaft gespeichert wird ausschließlich in Neon.
        public ObservableCollection<Kunde> Kunden { get; } = new();

        public DashboardAnsicht()
        {
            InitializeComponent();
            Kunden.CollectionChanged += Kunden_CollectionChanged;

            SetAppearanceControls();
            _isInitialized = true;
            ApplyAppearance();
            UpdateStatistics();
            UpdateDataActions();

            // Ein Konstruktor kann nicht auf einen Datenbankzugriff warten. Loaded startet das
            // Laden, sobald die Seite sichtbar ist. await hält dabei das Fenster bedienbar.
            Loaded += Dashboard_Loaded;
            Unloaded += (_, _) => _loadCancellation?.Cancel();
        }

        private async void Dashboard_Loaded(object sender, RoutedEventArgs e)
        {
            if (!EnsureDashboardAccess())
                return;

            if (!_hasDataLoaded)
                await LoadCustomersAsync();
        }

        private async void ReloadCustomers_Click(object sender, RoutedEventArgs e) => await LoadCustomersAsync();

        private async Task LoadCustomersAsync()
        {
            if (!EnsureDashboardAccess())
                return;

            if (_isLoading || _dialogOpen || !IsLoaded)
                return;

            _isLoading = true;
            _canEditCustomers = false;
            var cancellation = new CancellationTokenSource();
            _loadCancellation = cancellation;
            DatabaseLoadingIndicator.IsActive = true;
            DatabaseLoadingIndicator.Visibility = Visibility.Visible;
            DatabaseStatusText.Text = "Unternehmen werden geladen …";
            EmptyCustomersText.Visibility = Visibility.Collapsed;
            UpdateDataActions();
            try
            {
                // Erst alle Zeilen laden, dann die Anzeige austauschen. Bei einem Fehler bleibt
                // der zuletzt geladene Stand sichtbar; eine halbe Liste wird nicht angezeigt.
                IReadOnlyList<Unternehmen> companies = await _unternehmenRepository.LoadAsync(cancellation.Token);
                if (cancellation.IsCancellationRequested || !IsLoaded)
                    return;

                _hasDataLoaded = true;
                Kunden.Clear();
                foreach (Unternehmen company in companies)
                    Kunden.Add(CreateDisplayedCustomer(company));

                _canEditCustomers = true;
                UpdateStatistics();
                DatabaseStatusText.Text = "Unternehmen sind geladen. Änderungen werden dauerhaft gespeichert.";
                EmptyCustomersText.Visibility = Kunden.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Beim Verlassen der Seite brauchen wir das Ladeergebnis nicht mehr.
            }
            catch (UnauthorizedAccessException)
            {
                // Die Rolle kann während einer Sitzung entzogen werden. Das Repository
                // prüft sie vor jeder Abfrage erneut; die Ansicht beendet dann die Sitzung.
                ReturnToLogin("Dein Zugriff ist nicht mehr verfügbar. Bitte melde dich erneut an.");
            }
            catch (Exception)
            {
                // Keine technischen Fehlermeldungen oder Zugangsdaten im Fenster ausgeben.
                DatabaseStatusText.Text = "Unternehmen konnten nicht geladen werden. Prüfe die Verbindung und versuche es erneut.";
            }
            finally
            {
                _isLoading = false;
                if (ReferenceEquals(_loadCancellation, cancellation))
                    _loadCancellation = null;
                cancellation.Dispose();
                DatabaseLoadingIndicator.IsActive = false;
                DatabaseLoadingIndicator.Visibility = Visibility.Collapsed;
                UpdateDataActions();
            }
        }

        private Kunde CreateDisplayedCustomer(Unternehmen company) => new(
            company, Brush("AccentBrush"), Brush("BorderBrush"));

        private void UpdateDataActions()
        {
            // Bei fehlgeschlagenem Laden sind Anlegen/Bearbeiten gesperrt, damit keine
            // unvollständige oder veraltete Liste als aktuelle Datenbasis benutzt wird.
            bool accessAllowed = BenutzerSitzung.DashboardZugriffErlaubt && !_returningToLogin;
            CreateCustomerButton.IsEnabled = accessAllowed && _canEditCustomers && !_isLoading && !_dialogOpen;
            KundenItems.IsEnabled = accessAllowed && _canEditCustomers && !_isLoading && !_dialogOpen;
            ReloadCustomersButton.IsEnabled = accessAllowed && !_isLoading && !_dialogOpen;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is DashboardAppearance appearance)
            {
                if (AccentColors.Contains(appearance.AccentHex, StringComparer.OrdinalIgnoreCase))
                    _accentHex = appearance.AccentHex.ToUpperInvariant();

                _isLight = appearance.IsLight;
                _isInitialized = false;
                SetAppearanceControls();
                _isInitialized = true;
                ApplyAppearance();
            }
            UpdateAccountDisplay();
            EnsureDashboardAccess();
        }

        private void UpdateAccountDisplay()
        {
            if (BenutzerSitzung.IstEntwicklungsansicht)
            {
                // Eine Debug-Vorschau ist kein angemeldetes Benutzerkonto und wird klar benannt.
                AccountNameText.Text = "Entwicklungsansicht";
                AccountRoleText.Text = "Dashboard-Vorschau";
            }
            else
            {
                AngemeldeterBenutzer? user = BenutzerSitzung.AktuellerBenutzer;
                AccountNameText.Text = user is null ? "" : $"{user.Vorname} {user.Nachname}".Trim();
                AccountRoleText.Text = user?.IstMegakrasserAdmin == true ? "MegakrassenAdmin" : "";
            }
            CompactAccountText.Text = string.Join(" · ", new[] { AccountNameText.Text, AccountRoleText.Text }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private bool EnsureDashboardAccess()
        {
            // Eine direkte Navigation zur Seite reicht nicht als Berechtigung. Erst eine
            // erfolgreiche Admin-Anmeldung (oder die ausdrückliche Debug-Vorschau) erlaubt Zugriff.
            if (!_returningToLogin && BenutzerSitzung.DashboardZugriffErlaubt)
                return true;

            if (!_returningToLogin)
            {
                _returningToLogin = true;
                _canEditCustomers = false;
                UpdateDataActions();
                // OnNavigatedTo läuft innerhalb einer Navigation. Wir wechseln erst nach
                // diesem Ereignis zurück, damit WinUI keine verschachtelte Navigation ausführt.
                DispatcherQueue.TryEnqueue(() => ReturnToLogin("Bitte melde dich als MegakrassenAdmin an."));
            }
            return false;
        }

        private void ReturnToLogin(string? message = null)
        {
            _returningToLogin = true;
            _canEditCustomers = false;
            _loadCancellation?.Cancel();
            BenutzerSitzung.Abmelden();
            UpdateDataActions();
            // Beim Abmelden verschwinden auch die geladenen Kundendaten aus dieser Anzeige.
            Kunden.Clear();
            _hasDataLoaded = false;
            UpdateStatistics();
            UpdateAccountDisplay();
            var appearance = new DashboardAppearance { AccentHex = _accentHex, IsLight = _isLight };
            object parameter = message is null ? appearance : new LoginNavigationParameter(appearance, message);
            if (Frame is Frame frame && ReferenceEquals(frame.Content, this) && frame.Navigate(typeof(LoginPage), parameter))
                frame.BackStack.Clear();
        }

        private void SetAppearanceControls()
        {
            ModeSwitch.IsOn = _isLight;
            foreach (ComboBoxItem item in ColorCombo.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag as string, _accentHex, StringComparison.OrdinalIgnoreCase))
                {
                    ColorCombo.SelectedItem = item;
                    break;
                }
            }
        }

        private void ColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitialized && ColorCombo.SelectedItem is ComboBoxItem item &&
                item.Tag is string hex && AccentColors.Contains(hex, StringComparer.OrdinalIgnoreCase))
            {
                _accentHex = hex.ToUpperInvariant();
                ApplyAccent();
            }
        }

        private void ModeSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitialized)
            {
                _isLight = ModeSwitch.IsOn;
                ApplyAppearance();
            }
        }

        private void ApplyAppearance()
        {
            // XAML verwendet dieselben Brush-Objekte überall. Ändern wir deren Farbe,
            // passen sich Karten, Texte und bereits geöffnete Dialoge gemeinsam an.
            RequestedTheme = _isLight ? ElementTheme.Light : ElementTheme.Dark;
            SetBrush("BackgroundBrush", _isLight ? "#FCFCFD" : "#101319");
            SetBrush("SidebarBrush", _isLight ? "#F0F0F0" : "#262930");
            SetBrush("SurfaceBrush", _isLight ? "#FFFFFF" : "#262930");
            SetBrush("BorderBrush", _isLight ? "#E1E2E6" : "#3D4048");
            SetBrush("TextBrush", _isLight ? "#101319" : "#ECEEF3");
            SetBrush("MutedTextBrush", _isLight ? "#525B6A" : "#B8C1D2");
            SetBrush("InputBrush", _isLight ? "#F0F0F0" : "#20232A");
            ApplyAccent();
        }

        private void ApplyAccent()
        {
            Color accent = ParseColor(_accentHex);
            Color foreground = BestAccentForeground(accent);
            Color hover = Mix(accent, Microsoft.UI.Colors.White, 0.12);
            Color pressed = Mix(accent,
                foreground == Microsoft.UI.Colors.Black ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black, 0.06);

            SetBrush("AccentBrush", accent);
            SetBrush("AccentHoverBrush", hover);
            SetBrush("AccentPressedBrush", pressed);
            SetBrush("AccentTextBrush", foreground);
            SetBrush("ToggleSwitchFillOn", accent);
            SetBrush("ToggleSwitchFillOnPointerOver", hover);
            SetBrush("ToggleSwitchFillOnPressed", pressed);
            SetBrush("TextControlBorderBrushFocused", accent);
        }

        private SolidColorBrush Brush(string key) => (SolidColorBrush)Resources[key];

        private void SetBrush(string key, string hex) => SetBrush(key, ParseColor(hex));

        private void SetBrush(string key, Color color) => Brush(key).Color = color;

        private static Color ParseColor(string hex) => Color.FromArgb(
            0xFF,
            byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        private static Color Mix(Color color, Color target, double amount) => Color.FromArgb(
            0xFF,
            (byte)Math.Round(color.R + (target.R - color.R) * amount),
            (byte)Math.Round(color.G + (target.G - color.G) * amount),
            (byte)Math.Round(color.B + (target.B - color.B) * amount));

        private static Color BestAccentForeground(Color color)
        {
            static double Linear(byte value)
            {
                double channel = value / 255.0;
                return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
            }

            double luminance = 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
            double blackContrast = (luminance + 0.05) / 0.05;
            double whiteContrast = 1.05 / (luminance + 0.05);
            return blackContrast >= whiteContrast ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
        }

        private void Kunden_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Neue Kunden beobachten, entfernte Kunden abmelden: Dadurch rechnen die
            // Kennzahlen auch nach Änderungen, ohne alte Einträge im Speicher festzuhalten.
            foreach (Kunde removed in _observedCustomers.Where(customer => !Kunden.Contains(customer)).ToArray())
            {
                removed.PropertyChanged -= Customer_PropertyChanged;
                _observedCustomers.Remove(removed);
            }

            foreach (Kunde customer in Kunden)
            {
                if (_observedCustomers.Add(customer))
                    customer.PropertyChanged += Customer_PropertyChanged;
            }

            if (_isInitialized)
                UpdateStatistics();
        }

        private void Customer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(Kunde.Benutzer) or nameof(Kunde.Status) or null or "")
                UpdateStatistics();
        }

        private void UpdateStatistics()
        {
            // Solange noch kein Laden gelungen ist, kennen wir die Zahlen nicht.
            // Ein Strich bedeutet „noch unbekannt“; eine 0 würde fälschlich „keine Daten“ behaupten.
            if (!_hasDataLoaded)
            {
                CustomerCountText.Text = TrialCountText.Text = UserCountText.Text = "–";
                return;
            }

            CustomerCountText.Text = Kunden.Count.ToString(CultureInfo.CurrentCulture);
            TrialCountText.Text = Kunden.Count(customer => customer.Status == "Testphase").ToString(CultureInfo.CurrentCulture);
            UserCountText.Text = Kunden.Sum(customer => (long)customer.Benutzer).ToString(CultureInfo.CurrentCulture);
        }

        private async void CreateCustomer_Click(object sender, RoutedEventArgs e) => await EditCustomerAsync(null);

        private async void EditCustomer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Kunde customer })
                await EditCustomerAsync(customer);
        }

        private async Task EditCustomerAsync(Kunde? customer)
        {
            if (!EnsureDashboardAccess())
                return;

            if (_dialogOpen || _isLoading || !_canEditCustomers || XamlRoot is null)
                return;

            _dialogOpen = true;
            UpdateDataActions();
            bool accessRevoked = false;
            try
            {
                var companyBox = CreateTextBox(customer?.Firmenname ?? "");
                var identifierBox = CreateTextBox(customer?.Kennung ?? "");
                var emailBox = CreateTextBox(customer?.AdminEmail ?? "");
                // Die Eingaben werden schon im Formular begrenzt. Das Repository prüft
                // dieselben Grenzen erneut, bevor es die Daten an Neon überträgt.
                companyBox.MaxLength = 200;
                identifierBox.MaxLength = 100;
                emailBox.MaxLength = 254;
                // Unsere bestehende Unternehmenstabelle speichert auch Anschrift und Kontakt.
                // Beim Bearbeiten übernehmen wir die vorhandenen Werte, statt sie zu überschreiben.
                var addressBox = CreateTextBox(customer?.Adresse ?? "");
                var postcodeBox = CreateTextBox(customer?.Plz ?? "");
                var cityBox = CreateTextBox(customer?.Ort ?? "");
                var phoneBox = CreateTextBox(customer?.Telefon ?? "");
                var contactEmailBox = CreateTextBox(customer?.KontaktEmail ?? "");
                var contactPersonBox = CreateTextBox(customer?.Kontaktperson ?? "");
                addressBox.MaxLength = 300;
                postcodeBox.MaxLength = 20;
                cityBox.MaxLength = 150;
                contactPersonBox.MaxLength = 200;
                phoneBox.MaxLength = 50;
                contactEmailBox.MaxLength = 254;
                emailBox.InputScope = new Microsoft.UI.Xaml.Input.InputScope
                {
                    Names = { new Microsoft.UI.Xaml.Input.InputScopeName { NameValue = Microsoft.UI.Xaml.Input.InputScopeNameValue.EmailSmtpAddress } }
                };

                var statusBox = new ComboBox
                {
                    Style = (Style)Resources["FormComboBox"],
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                statusBox.Items.Add(new ComboBoxItem { Content = UnternehmenStatus.Testphase, Tag = UnternehmenStatus.Testphase });
                statusBox.Items.Add(new ComboBoxItem { Content = UnternehmenStatus.Aktiv, Tag = UnternehmenStatus.Aktiv });
                // „Inaktiv“ sperrt einen vorhandenen Kunden. Ein neuer Kunde startet nie gesperrt.
                if (customer is not null)
                    statusBox.Items.Add(new ComboBoxItem { Content = UnternehmenStatus.Inaktiv, Tag = UnternehmenStatus.Inaktiv });
                // Den gespeicherten Status auswählen. Sonst würde schon das Ändern der Adresse
                // einen gesperrten Kunden unbemerkt wieder freischalten.
                statusBox.SelectedIndex = customer?.Status switch
                {
                    UnternehmenStatus.Aktiv => 1,
                    UnternehmenStatus.Inaktiv => 2,
                    _ => 0
                };

                // Solange „Inaktiv“ gewählt ist, erklärt dieser Hinweis die Folgen für alle Konten der Firma.
                var lockWarning = new TextBlock
                {
                    Text = $"Inaktiv sperrt die Firma ({customer?.Benutzer ?? 0} Benutzer): Nur Geschäftsführung und Kundenadmin " +
                        "können sich noch anmelden und dann nur die Monatsstatistik ansehen und als PDF herunterladen. Alle " +
                        "anderen sind gesperrt; wer gerade angemeldet ist, wird bei der nächsten Aktion abgemeldet. Alle Daten " +
                        "bleiben erhalten. Mit „Aktiv“ oder „Testphase“ ist die Firma wieder freigeschaltet.",
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(ParseColor(_isLight ? "#B91C1C" : "#FF939B"))
                };
                void UpdateLockWarning() => lockWarning.Visibility =
                    (statusBox.SelectedItem as ComboBoxItem)?.Tag as string == UnternehmenStatus.Inaktiv
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                statusBox.SelectionChanged += (_, _) => UpdateLockWarning();
                UpdateLockWarning();

                var errorText = new TextBlock
                {
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(ParseColor(_isLight ? "#B91C1C" : "#FF939B")),
                    Visibility = Visibility.Collapsed
                };
                var form = new StackPanel
                {
                    Spacing = 18,
                    Width = Math.Min(488, Math.Max(0, XamlRoot.Size.Width - 96))
                };
                AddField(form, "Firmenname", companyBox);
                AddField(form, "Unternehmenskennung (für die Anmeldung)", identifierBox);
                AddField(form, "E-Mail des Kundenadmins", emailBox);
                AddField(form, "Status", statusBox);
                form.Children.Add(lockWarning);
                form.Children.Add(new TextBlock
                {
                    Text = "Kontakt und Anschrift",
                    FontSize = 18,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Brush("TextBrush")
                });
                AddField(form, "Straße und Hausnummer", addressBox);
                AddField(form, "Postleitzahl", postcodeBox);
                AddField(form, "Ort", cityBox);
                AddField(form, "Telefon", phoneBox);
                AddField(form, "Kontaktperson", contactPersonBox);
                AddField(form, "Kontakt-E-Mail (leer = E-Mail des Kundenadmins)", contactEmailBox);

                var dialog = CreateDialog(customer is null ? "Neuen Kunden anlegen" : "Kunden bearbeiten");
                var cancelButton = new Button
                {
                    Content = "Abbrechen",
                    Style = (Style)Resources["DashboardButton"]
                };
                var saveButton = new Button
                {
                    Content = "Speichern",
                    Style = (Style)Resources["PrimaryButton"]
                };
                var footer = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                footer.Children.Add(cancelButton);
                footer.Children.Add(saveButton);
                // Eigene Footer-Buttons behalten ihren Stil auch beim Tastaturfokus.
                dialog.PrimaryButtonText = "";
                dialog.SecondaryButtonText = "";
                dialog.CloseButtonText = "";
                // Nur die Felder scrollen. Meldung und Buttons bleiben sichtbar, auch wenn
                // ein ungültiges Feld weiter oben den Fokus bekommt.
                var dialogContent = new Grid { MaxHeight = 560, Width = form.Width };
                dialogContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                dialogContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                dialogContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                dialogContent.Children.Add(new ScrollViewer
                {
                    Content = form,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                });
                errorText.Margin = new Thickness(0, 12, 0, 0);
                Grid.SetRow(errorText, 1);
                dialogContent.Children.Add(errorText);
                Grid.SetRow(footer, 2);
                dialogContent.Children.Add(footer);
                dialog.Content = dialogContent;

                bool isSaving = false;
                void ShowError(string message, Control field)
                {
                    errorText.Text = message;
                    errorText.Visibility = Visibility.Visible;
                    field.Focus(FocusState.Programmatic);
                }

                companyBox.TextChanged += (_, _) => errorText.Visibility = Visibility.Collapsed;
                identifierBox.TextChanged += (_, _) => errorText.Visibility = Visibility.Collapsed;
                emailBox.TextChanged += (_, _) => errorText.Visibility = Visibility.Collapsed;
                foreach (TextBox field in new[] { addressBox, postcodeBox, cityBox, phoneBox, contactEmailBox, contactPersonBox })
                    field.TextChanged += (_, _) => errorText.Visibility = Visibility.Collapsed;
                dialog.Opened += (_, _) => companyBox.Focus(FocusState.Programmatic);
                async Task TrySaveAsync()
                {
                    // Auch ein Doppelklick oder Enter während des Wartens darf kein
                    // zweites INSERT auslösen. Ein Speichervorgang läuft zur selben Zeit.
                    if (isSaving)
                        return;

                    string company = companyBox.Text.Trim();
                    string identifier = identifierBox.Text.Trim();
                    string email = emailBox.Text.Trim();
                    if (company.Length == 0)
                    {
                        ShowError("Bitte einen Firmennamen eingeben.", companyBox);
                        return;
                    }
                    if (identifier.Length == 0)
                    {
                        ShowError("Bitte eine Unternehmenskennung eingeben.", identifierBox);
                        return;
                    }
                    if (Kunden.Any(other => !ReferenceEquals(other, customer) &&
                        string.Equals(other.Kennung.Trim(), identifier, StringComparison.OrdinalIgnoreCase)))
                    {
                        ShowError("Diese Unternehmenskennung wird bereits verwendet.", identifierBox);
                        return;
                    }
                    if (!MailAddress.TryCreate(email, out MailAddress? address) ||
                        !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
                    {
                        ShowError("Bitte eine gültige E-Mail-Adresse eingeben.", emailBox);
                        return;
                    }

                    string status = ((ComboBoxItem)statusBox.SelectedItem).Tag as string ?? "Testphase";
                    foreach ((TextBox field, string label) in new[]
                    {
                        (addressBox, "Straße und Hausnummer"), (postcodeBox, "Postleitzahl"),
                        (cityBox, "Ort"), (phoneBox, "Telefon"), (contactPersonBox, "Kontaktperson")
                    })
                    {
                        if (string.IsNullOrWhiteSpace(field.Text))
                        {
                            ShowError($"Bitte {label} eingeben.", field);
                            return;
                        }
                    }
                    string contactEmail = string.IsNullOrWhiteSpace(contactEmailBox.Text)
                        ? email : contactEmailBox.Text.Trim();
                    if (!MailAddress.TryCreate(contactEmail, out MailAddress? contactAddress) ||
                        !string.Equals(contactAddress.Address, contactEmail, StringComparison.OrdinalIgnoreCase))
                    {
                        ShowError("Bitte eine gültige Kontakt-E-Mail-Adresse eingeben.", contactEmailBox);
                        return;
                    }

                    var draft = new UnternehmenEntwurf(company, identifier, email, status,
                        addressBox.Text.Trim(), postcodeBox.Text.Trim(), cityBox.Text.Trim(),
                        phoneBox.Text.Trim(), contactEmail, contactPersonBox.Text.Trim());
                    isSaving = true;
                    saveButton.IsEnabled = false;
                    cancelButton.IsEnabled = false;
                    companyBox.IsEnabled = identifierBox.IsEnabled = emailBox.IsEnabled = statusBox.IsEnabled = false;
                    foreach (TextBox field in new[] { addressBox, postcodeBox, cityBox, phoneBox, contactEmailBox, contactPersonBox })
                        field.IsEnabled = false;
                    saveButton.Content = "Speichert …";
                    errorText.Visibility = Visibility.Collapsed;
                    try
                    {
                        // null bedeutet „neues Unternehmen“. Beim Bearbeiten schicken wir die
                        // unveränderliche Id und die Version vom letzten Laden mit.
                        Unternehmen saved = customer is null
                            ? await _unternehmenRepository.CreateAsync(draft)
                            : await _unternehmenRepository.UpdateAsync(customer.Id, customer.Version, draft);

                        // Erst NACH der Bestätigung aus Neon ändern wir die Anzeige.
                        // Bei einem Fehler bleiben die alte Liste und alle Formulareingaben erhalten.
                        bool nowLocked = saved.Status == UnternehmenStatus.Inaktiv &&
                            customer?.Status != UnternehmenStatus.Inaktiv;
                        if (customer is null)
                            Kunden.Add(CreateDisplayedCustomer(saved));
                        else
                            customer.ApplySavedCompany(saved);

                        EmptyCustomersText.Visibility = Visibility.Collapsed;
                        DatabaseStatusText.Text = nowLocked
                            ? $"{saved.Firmenname} ist jetzt inaktiv. Nur Geschäftsführung und Kundenadmin sehen noch die Statistik."
                            : "Unternehmen wurde gespeichert.";
                        isSaving = false;
                        dialog.Hide();
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Erst den Dialog schließen, dann zur Anmeldung navigieren. Ein offener
                        // ContentDialog darf nicht an einer bereits verlassenen Seite hängen bleiben.
                        accessRevoked = true;
                        isSaving = false;
                        dialog.Hide();
                    }
                    catch (UnternehmenKennungVergebenException)
                    {
                        // Die lokale Liste kann eine parallele Anlage eines Kollegen noch nicht
                        // kennen. Deshalb prüft zusätzlich die Datenbank eindeutige Kennungen.
                        errorText.Text = "Diese Unternehmenskennung wird bereits verwendet.";
                        errorText.Visibility = Visibility.Visible;
                    }
                    catch (UnternehmenKonfliktException)
                    {
                        errorText.Text = "Dieses Unternehmen wurde inzwischen geändert oder gelöscht. Brich ab, lade die Liste erneut und bearbeite den aktuellen Stand.";
                        errorText.Visibility = Visibility.Visible;
                    }
                    catch (Exception)
                    {
                        errorText.Text = "Speichern konnte nicht bestätigt werden. Deine Eingaben bleiben erhalten. Prüfe die Verbindung und lade bei Unsicherheit die Liste erneut.";
                        errorText.Visibility = Visibility.Visible;
                    }
                    finally
                    {
                        isSaving = false;
                        saveButton.IsEnabled = cancelButton.IsEnabled = true;
                        companyBox.IsEnabled = identifierBox.IsEnabled = emailBox.IsEnabled = statusBox.IsEnabled = true;
                        foreach (TextBox field in new[] { addressBox, postcodeBox, cityBox, phoneBox, contactEmailBox, contactPersonBox })
                            field.IsEnabled = true;
                        saveButton.Content = "Speichern";
                        if (errorText.Visibility == Visibility.Visible)
                            identifierBox.Focus(FocusState.Programmatic);
                    }
                }

                saveButton.Click += async (_, _) => await TrySaveAsync();
                cancelButton.Click += (_, _) => { if (!isSaving) dialog.Hide(); };
                dialog.Closing += (_, args) => args.Cancel = isSaving;
                dialog.PreviewKeyDown += async (_, args) =>
                {
                    // Eine geöffnete Auswahlliste verarbeitet Enter/Escape selbst.
                    if (statusBox.IsDropDownOpen || isSaving)
                        return;

                    if (args.Key == Windows.System.VirtualKey.Escape)
                    {
                        args.Handled = true;
                        dialog.Hide();
                    }
                    else if (args.Key == Windows.System.VirtualKey.Enter &&
                        Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is not (Button or ComboBox))
                    {
                        args.Handled = true;
                        await TrySaveAsync();
                    }
                };

                await dialog.ShowAsync();
            }
            finally
            {
                _dialogOpen = false;
                UpdateDataActions();
                if (accessRevoked)
                    ReturnToLogin("Dein Zugriff ist nicht mehr verfügbar. Bitte melde dich erneut an.");
            }
        }

        private async void CreateKundenadmin_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Kunde customer })
                await CreateKundenadminAsync(customer);
        }

        // Der MegakrasserAdmin legt für ein Unternehmen den Kundenadmin an.
        // Weitere Benutzer legt danach der Kundenadmin selbst an.
        private async Task CreateKundenadminAsync(Kunde customer)
        {
            if (!EnsureDashboardAccess())
                return;

            if (_dialogOpen || _isLoading || !_canEditCustomers || XamlRoot is null)
                return;

            _dialogOpen = true;
            UpdateDataActions();
            bool accessRevoked = false;
            try
            {
                var firstNameBox = CreateTextBox("");
                var lastNameBox = CreateTextBox("");
                // Vorausgefüllt mit der E-Mail, die beim Unternehmen für den Kundenadmin hinterlegt ist.
                var emailBox = CreateTextBox(customer.AdminEmail);
                var passwordBox = CreatePasswordBox();
                var repeatBox = CreatePasswordBox();
                firstNameBox.MaxLength = 100;
                lastNameBox.MaxLength = 100;
                emailBox.MaxLength = 254;

                var errorText = new TextBlock
                {
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(ParseColor(_isLight ? "#B91C1C" : "#FF939B")),
                    Visibility = Visibility.Collapsed,
                    Margin = new Thickness(0, 12, 0, 0)
                };
                var form = new StackPanel
                {
                    Spacing = 18,
                    Width = Math.Min(488, Math.Max(0, XamlRoot.Size.Width - 96))
                };
                form.Children.Add(new TextBlock
                {
                    Text = $"Für {customer.Firmenname} (Kennung: {customer.Kennung})",
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("MutedTextBrush")
                });
                AddField(form, "Vorname", firstNameBox);
                AddField(form, "Nachname", lastNameBox);
                AddField(form, "E-Mail (für die Anmeldung)", emailBox);
                AddField(form, "Startpasswort (mindestens 8 Zeichen)", passwordBox);
                AddField(form, "Startpasswort wiederholen", repeatBox);

                var dialog = CreateDialog("Kundenadmin anlegen");
                var cancelButton = new Button { Content = "Abbrechen", Style = (Style)Resources["DashboardButton"] };
                var saveButton = new Button { Content = "Speichern", Style = (Style)Resources["PrimaryButton"] };
                var footer = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                footer.Children.Add(cancelButton);
                footer.Children.Add(saveButton);
                dialog.PrimaryButtonText = "";
                dialog.SecondaryButtonText = "";
                dialog.CloseButtonText = "";

                // Gleicher Aufbau wie beim Kundendialog: Felder scrollen, Meldung und Buttons bleiben sichtbar.
                var dialogContent = new Grid { MaxHeight = 560, Width = form.Width };
                dialogContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                dialogContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                dialogContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                dialogContent.Children.Add(new ScrollViewer
                {
                    Content = form,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                });
                Grid.SetRow(errorText, 1);
                dialogContent.Children.Add(errorText);
                Grid.SetRow(footer, 2);
                dialogContent.Children.Add(footer);
                dialog.Content = dialogContent;

                bool isSaving = false;
                void ShowError(string message, Control field)
                {
                    errorText.Text = message;
                    errorText.Visibility = Visibility.Visible;
                    field.Focus(FocusState.Programmatic);
                }

                void SetInputsEnabled(bool enabled)
                {
                    firstNameBox.IsEnabled = lastNameBox.IsEnabled = emailBox.IsEnabled = enabled;
                    passwordBox.IsEnabled = repeatBox.IsEnabled = enabled;
                    saveButton.IsEnabled = cancelButton.IsEnabled = enabled;
                }

                foreach (TextBox field in new[] { firstNameBox, lastNameBox, emailBox })
                    field.TextChanged += (_, _) => errorText.Visibility = Visibility.Collapsed;
                passwordBox.PasswordChanged += (_, _) => errorText.Visibility = Visibility.Collapsed;
                repeatBox.PasswordChanged += (_, _) => errorText.Visibility = Visibility.Collapsed;
                dialog.Opened += (_, _) => firstNameBox.Focus(FocusState.Programmatic);

                async Task TrySaveAsync()
                {
                    // Doppelklick oder Enter während des Speicherns darf kein zweites Konto anlegen.
                    if (isSaving)
                        return;

                    string email = emailBox.Text.Trim();
                    if (firstNameBox.Text.Trim().Length == 0)
                    {
                        ShowError("Bitte einen Vornamen eingeben.", firstNameBox);
                        return;
                    }
                    if (lastNameBox.Text.Trim().Length == 0)
                    {
                        ShowError("Bitte einen Nachnamen eingeben.", lastNameBox);
                        return;
                    }
                    if (!MailAddress.TryCreate(email, out MailAddress? address) ||
                        !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
                    {
                        ShowError("Bitte eine gültige E-Mail-Adresse eingeben.", emailBox);
                        return;
                    }
                    if (passwordBox.Password.Length < 8)
                    {
                        ShowError("Das Startpasswort braucht mindestens 8 Zeichen.", passwordBox);
                        return;
                    }
                    if (!string.Equals(passwordBox.Password, repeatBox.Password, StringComparison.Ordinal))
                    {
                        ShowError("Die beiden Passwörter stimmen nicht überein.", repeatBox);
                        return;
                    }

                    var draft = new BenutzerEntwurf(customer.Id, firstNameBox.Text, lastNameBox.Text,
                        email, passwordBox.Password);
                    isSaving = true;
                    SetInputsEnabled(false);
                    saveButton.Content = "Speichert …";
                    errorText.Visibility = Visibility.Collapsed;
                    try
                    {
                        await _benutzerRepository.KundenadminAnlegenAsync(draft);

                        // Erst nach der Bestätigung aus Neon die Anzeige ändern.
                        // Benutzer++ aktualisiert Karte und Kennzahl „Benutzer“ automatisch.
                        customer.Benutzer++;
                        DatabaseStatusText.Text = $"Kundenadmin für {customer.Firmenname} wurde angelegt.";
                        isSaving = false;
                        dialog.Hide();
                    }
                    catch (UnauthorizedAccessException)
                    {
                        accessRevoked = true;
                        isSaving = false;
                        dialog.Hide();
                    }
                    catch (BenutzerEmailVergebenException exception)
                    {
                        errorText.Text = exception.Message;
                        errorText.Visibility = Visibility.Visible;
                    }
                    catch (ArgumentException exception)
                    {
                        // Diese Meldungen stammen aus unserer eigenen Prüfung im Repository.
                        errorText.Text = exception.Message;
                        errorText.Visibility = Visibility.Visible;
                    }
                    catch (Exception)
                    {
                        errorText.Text = "Speichern konnte nicht bestätigt werden. Prüfe die Verbindung und lade die Liste neu, bevor du es erneut versuchst.";
                        errorText.Visibility = Visibility.Visible;
                    }
                    finally
                    {
                        isSaving = false;
                        SetInputsEnabled(true);
                        saveButton.Content = "Speichern";
                    }
                }

                saveButton.Click += async (_, _) => await TrySaveAsync();
                cancelButton.Click += (_, _) => { if (!isSaving) dialog.Hide(); };
                dialog.Closing += (_, args) => args.Cancel = isSaving;
                dialog.PreviewKeyDown += async (_, args) =>
                {
                    if (isSaving)
                        return;

                    if (args.Key == Windows.System.VirtualKey.Escape)
                    {
                        args.Handled = true;
                        dialog.Hide();
                    }
                    else if (args.Key == Windows.System.VirtualKey.Enter &&
                        Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is not Button)
                    {
                        args.Handled = true;
                        await TrySaveAsync();
                    }
                };

                await dialog.ShowAsync();
            }
            finally
            {
                _dialogOpen = false;
                UpdateDataActions();
                if (accessRevoked)
                    ReturnToLogin("Dein Zugriff ist nicht mehr verfügbar. Bitte melde dich erneut an.");
            }
        }

        // PasswordBox zeigt Punkte statt Klartext. Optik wie FormTextBox, da dieser Style nur für TextBox gilt.
        private PasswordBox CreatePasswordBox() => new()
        {
            Background = Brush("InputBrush"),
            Foreground = Brush("TextBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            MinHeight = 50,
            Padding = new Thickness(14, 10, 14, 10),
            FontSize = 16,
            MaxLength = 72,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        private TextBox CreateTextBox(string text) => new()
        {
            Text = text,
            Style = (Style)Resources["FormTextBox"],
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        private void AddField(StackPanel form, string label, Control input)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, label);
            var field = new StackPanel { Spacing = 8 };
            field.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 16,
                Foreground = Brush("TextBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            field.Children.Add(input);
            form.Children.Add(field);
        }

        private ContentDialog CreateDialog(string title)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = RequestedTheme,
                Title = new TextBlock
                {
                    Text = title,
                    FontSize = 24,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("TextBrush")
                },
                Background = Brush("SurfaceBrush"),
                Foreground = Brush("TextBrush"),
                BorderBrush = Brush("BorderBrush"),
                CornerRadius = new CornerRadius(20),
                DefaultButton = ContentDialogButton.None,
                PrimaryButtonStyle = (Style)Resources["PrimaryButton"],
                CloseButtonStyle = (Style)Resources["DashboardButton"]
            };

            foreach (string key in new[]
            {
                "BackgroundBrush", "SidebarBrush", "SurfaceBrush", "BorderBrush", "TextBrush",
                "MutedTextBrush", "InputBrush", "AccentBrush", "AccentTextBrush", "AccentHoverBrush",
                "AccentPressedBrush", "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver",
                "ToggleSwitchFillOnPressed", "TextControlBorderBrushFocused"
            })
                dialog.Resources[key] = Resources[key];

            dialog.Resources["ContentDialogBackground"] = Brush("SurfaceBrush");
            dialog.Resources["ContentDialogForeground"] = Brush("TextBrush");
            dialog.Resources["ContentDialogTitleForeground"] = Brush("TextBrush");
            dialog.Resources["ContentDialogContentForeground"] = Brush("TextBrush");
            dialog.Resources["ContentDialogBorderBrush"] = Brush("BorderBrush");
            dialog.Resources["ContentDialogCornerRadius"] = new CornerRadius(20);
            dialog.Resources["ContentDialogMaxWidth"] = 620d;
            return dialog;
        }

        private async void LoginCustomer_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureDashboardAccess())
                return;

            if (_dialogOpen || _isLoading || XamlRoot is null || sender is not Button { DataContext: Kunde customer })
                return;

            _dialogOpen = true;
            UpdateDataActions();
            bool accessRevoked = false;
            bool opened = false;
            try
            {
                // Der Admin bleibt mit seinem eigenen Konto angemeldet und benutzt kein Kundenkonto.
                // Vorher bestätigen, weil jeder Zugang ins Aktionsprotokoll der Firma geschrieben wird.
                var dialog = CreateDialog("Firmenansicht öffnen");
                dialog.Content = new TextBlock
                {
                    Text = $"Sie öffnen die Firmenansicht von {customer.Firmenname} als MegakrassenAdmin. " +
                        "Dieser Zugang wird im Aktionsprotokoll der Firma gespeichert.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 460,
                    Foreground = Brush("TextBrush")
                };
                dialog.PrimaryButtonText = "Öffnen";
                dialog.CloseButtonText = "Abbrechen";
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;

                DatabaseStatusText.Text = $"Firmenansicht von {customer.Firmenname} wird geöffnet …";
                // Prüft die Admin-Rolle erneut in Neon, liest die Firma neu und protokolliert den Zugang.
                AdminFirmenzugang zugang = await _unternehmenRepository.FirmenzugangOeffnenAsync(customer.Id);
                if (_returningToLogin || !IsLoaded)
                    return;

                BenutzerSitzung.FirmenzugangStarten(zugang);
                var appearance = new DashboardAppearance { AccentHex = _accentHex, IsLight = _isLight };
                if (Frame is not Frame frame || !frame.Navigate(typeof(FirmenAnsicht), appearance))
                {
                    BenutzerSitzung.FirmenzugangBeenden();
                    throw new InvalidOperationException();
                }

                // Zurück geht es über „Zurück zum Admin-Dashboard“, nicht über den Navigationsverlauf.
                frame.BackStack.Clear();
                opened = true;
            }
            catch (UnauthorizedAccessException)
            {
                accessRevoked = true;
            }
            catch (UnternehmenKonfliktException)
            {
                DatabaseStatusText.Text = "Dieses Unternehmen gibt es nicht mehr. Bitte laden Sie die Liste erneut.";
            }
            catch (Exception)
            {
                DatabaseStatusText.Text = "Die Firmenansicht konnte nicht geöffnet werden. Bitte prüfen Sie die Verbindung und versuchen Sie es erneut.";
            }
            finally
            {
                _dialogOpen = false;
                if (!opened)
                    UpdateDataActions();
                if (accessRevoked)
                    ReturnToLogin("Ihr Zugriff ist nicht mehr verfügbar. Bitte melden Sie sich erneut an.");
            }
        }

        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            if (!_dialogOpen)
                ReturnToLogin();
        }

    }

    // Diese beiden Einstellungen werden beim Wechsel zwischen Login und Dashboard mitgegeben.
    public sealed class DashboardAppearance
    {
        public string AccentHex { get; set; } = "#7B2CBF";
        public bool IsLight { get; set; }
    }

    // Dieses Anzeigemodell enthält zusätzlich Farben. Das reine Datenbankmodell Unternehmen
    // kennt keine WinUI-Elemente und kann deshalb auch unabhängig von einem Fenster getestet werden.
    public sealed class Kunde : INotifyPropertyChanged
    {
        private readonly Brush _accentBrush;
        private readonly Brush _borderBrush;
        private Unternehmen _savedCompany;
        private string _firmenname;
        private string _kennung;
        private string _adminEmail;
        private int _benutzer;
        private string _status;

        public Kunde(Unternehmen company, Brush accentBrush, Brush borderBrush)
        {
            _savedCompany = company;
            _firmenname = company.Firmenname;
            _kennung = company.Kennung;
            _adminEmail = company.AdminEmail;
            _benutzer = company.Benutzer;
            _status = company.Status;
            _accentBrush = accentBrush;
            _borderBrush = borderBrush;
        }

        // Die Id bleibt gleich, auch wenn Firmenname oder Kennung geändert werden.
        public long Id => _savedCompany.Id;
        public DateTime Version => _savedCompany.Version;
        public string Adresse => _savedCompany.Adresse;
        public string Plz => _savedCompany.Plz;
        public string Ort => _savedCompany.Ort;
        public string Telefon => _savedCompany.Telefon;
        public string KontaktEmail => _savedCompany.KontaktEmail;
        public string Kontaktperson => _savedCompany.Kontaktperson;

        public void ApplySavedCompany(Unternehmen saved)
        {
            _savedCompany = saved;
            Firmenname = saved.Firmenname;
            Kennung = saved.Kennung;
            AdminEmail = saved.AdminEmail;
            Benutzer = saved.Benutzer;
            Status = saved.Status;
        }

        public string Firmenname { get => _firmenname; set => Set(ref _firmenname, value); }
        public string Kennung { get => _kennung; set => Set(ref _kennung, value); }
        public string AdminEmail { get => _adminEmail; set => Set(ref _adminEmail, value); }
        public int Benutzer { get => _benutzer; set => Set(ref _benutzer, value); }
        public string Status
        {
            get => _status;
            set
            {
                if (Set(ref _status, value))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBorderBrush)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Deckkraft)));
                }
            }
        }

        public Brush StatusBorderBrush => Status == "Testphase" ? _accentBrush : _borderBrush;

        // Gesperrte Kunden erscheinen blasser. Status und Knöpfe bleiben gut lesbar.
        public double Deckkraft => Status == UnternehmenStatus.Inaktiv ? 0.55 : 1;

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            // Nur echte Änderungen melden. PropertyChanged teilt der XAML-Bindung mit,
            // welche Eigenschaft sie neu anzeigen muss; die Zeile muss nicht neu gebaut werden.
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }
    }
}
