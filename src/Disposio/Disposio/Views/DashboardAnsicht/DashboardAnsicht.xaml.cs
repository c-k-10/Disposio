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
using Windows.UI;

namespace Disposio.Views
{
    public sealed partial class DashboardAnsicht : Page
    {
        private static readonly string[] AccentColors =
        {
            "#FF004D", "#007BFF", "#00C853", "#FF9100", "#7B2CBF"
        };

        private readonly HashSet<Kunde> _observedCustomers = new();
        private bool _isInitialized;
        private bool _dialogOpen;
        private bool _isLight;
        private string _accentHex = "#7B2CBF";

        public ObservableCollection<Kunde> Kunden { get; } = new();

        public DashboardAnsicht()
        {
            InitializeComponent();
            Kunden.CollectionChanged += Kunden_CollectionChanged;

            // Beispieldaten bleiben nur während dieser Dashboard-Sitzung im Speicher.
            int[] users = { 24, 12, 41, 6 };
            for (int index = 0; index < users.Length; index++)
            {
                int number = index + 1;
                Kunden.Add(new Kunde(
                    $"Testfirma {number}",
                    $"testfirma{number}",
                    $"admin@testfirma{number}.invalid",
                    users[index],
                    number == 4 ? "Testphase" : "Aktiv",
                    Brush("AccentBrush"),
                    Brush("BorderBrush")));
            }

            SetAppearanceControls();
            _isInitialized = true;
            ApplyAppearance();
            UpdateStatistics();
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
            // Auch Clear/Reset und extern hinzugefügte Kunden korrekt beobachten.
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
            if (_dialogOpen || XamlRoot is null)
                return;

            _dialogOpen = true;
            try
            {
                var companyBox = CreateTextBox(customer?.Firmenname ?? "");
                var identifierBox = CreateTextBox(customer?.Kennung ?? "");
                var emailBox = CreateTextBox(customer?.AdminEmail ?? "");
                emailBox.InputScope = new Microsoft.UI.Xaml.Input.InputScope
                {
                    Names = { new Microsoft.UI.Xaml.Input.InputScopeName { NameValue = Microsoft.UI.Xaml.Input.InputScopeNameValue.EmailSmtpAddress } }
                };

                var statusBox = new ComboBox
                {
                    Style = (Style)Resources["FormComboBox"],
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                statusBox.Items.Add(new ComboBoxItem { Content = "Testphase", Tag = "Testphase" });
                statusBox.Items.Add(new ComboBoxItem { Content = "Aktiv", Tag = "Aktiv" });
                statusBox.SelectedIndex = customer?.Status == "Aktiv" ? 1 : 0;

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
                form.Children.Add(errorText);

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
                form.Children.Add(footer);
                // Eigene Footer-Buttons behalten ihren Stil auch beim Tastaturfokus.
                dialog.PrimaryButtonText = "";
                dialog.SecondaryButtonText = "";
                dialog.CloseButtonText = "";
                dialog.Content = new ScrollViewer
                {
                    Content = form,
                    MaxHeight = 560,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };

                CustomerDraft? acceptedDraft = null;
                void ShowError(string message, Control field)
                {
                    errorText.Text = message;
                    errorText.Visibility = Visibility.Visible;
                    field.Focus(FocusState.Programmatic);
                }

                companyBox.TextChanged += (_, _) => errorText.Visibility = Visibility.Collapsed;
                identifierBox.TextChanged += (_, _) => errorText.Visibility = Visibility.Collapsed;
                emailBox.TextChanged += (_, _) => errorText.Visibility = Visibility.Collapsed;
                dialog.Opened += (_, _) => companyBox.Focus(FocusState.Programmatic);
                bool TryAccept()
                {
                    if (acceptedDraft is not null)
                        return true;

                    string company = companyBox.Text.Trim();
                    string identifier = identifierBox.Text.Trim();
                    string email = emailBox.Text.Trim();
                    if (company.Length == 0)
                    {
                        ShowError("Bitte einen Firmennamen eingeben.", companyBox);
                        return false;
                    }
                    if (identifier.Length == 0)
                    {
                        ShowError("Bitte eine Unternehmenskennung eingeben.", identifierBox);
                        return false;
                    }
                    if (Kunden.Any(other => !ReferenceEquals(other, customer) &&
                        string.Equals(other.Kennung.Trim(), identifier, StringComparison.OrdinalIgnoreCase)))
                    {
                        ShowError("Diese Unternehmenskennung wird bereits verwendet.", identifierBox);
                        return false;
                    }
                    if (!MailAddress.TryCreate(email, out MailAddress? address) ||
                        !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
                    {
                        ShowError("Bitte eine gültige E-Mail-Adresse eingeben.", emailBox);
                        return false;
                    }

                    string status = ((ComboBoxItem)statusBox.SelectedItem).Tag as string ?? "Testphase";
                    acceptedDraft = new CustomerDraft(company, identifier, email, status);
                    saveButton.IsEnabled = false;
                    cancelButton.IsEnabled = false;
                    dialog.Hide();
                    return true;
                }

                saveButton.Click += (_, _) => TryAccept();
                cancelButton.Click += (_, _) => dialog.Hide();
                dialog.PreviewKeyDown += (_, args) =>
                {
                    // Eine geöffnete Auswahlliste verarbeitet Enter/Escape selbst.
                    if (statusBox.IsDropDownOpen)
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
                        TryAccept();
                    }
                };

                await dialog.ShowAsync();
                // Hide liefert None; nur der validierte Speichern-Pfad setzt acceptedDraft.
                if (acceptedDraft is CustomerDraft draft)
                {
                    if (customer is null)
                    {
                        Kunden.Add(new Kunde(draft.Firmenname, draft.Kennung, draft.AdminEmail, 0,
                            draft.Status, Brush("AccentBrush"), Brush("BorderBrush")));
                    }
                    else
                    {
                        customer.Firmenname = draft.Firmenname;
                        customer.Kennung = draft.Kennung;
                        customer.AdminEmail = draft.AdminEmail;
                        customer.Status = draft.Status;
                    }
                }
            }
            finally
            {
                _dialogOpen = false;
            }
        }

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
            if (_dialogOpen || XamlRoot is null || sender is not Button { DataContext: Kunde customer })
                return;

            _dialogOpen = true;
            try
            {
                var dialog = CreateDialog("Kundenansicht");
                dialog.Content = new TextBlock
                {
                    Text = $"Die Kundenansicht für {customer.Firmenname} ist noch nicht angebunden. Es wurde kein Login durchgeführt.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 460,
                    Foreground = Brush("TextBrush")
                };
                dialog.CloseButtonText = "Verstanden";
                await dialog.ShowAsync();
            }
            finally
            {
                _dialogOpen = false;
            }
        }

        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            if (!_dialogOpen && Frame is Frame frame && frame.Navigate(typeof(LoginPage),
                new DashboardAppearance { AccentHex = _accentHex, IsLight = _isLight }))
                frame.BackStack.Clear();
        }

        private sealed record CustomerDraft(string Firmenname, string Kennung, string AdminEmail, string Status);
    }

    public sealed class DashboardAppearance
    {
        public string AccentHex { get; set; } = "#7B2CBF";
        public bool IsLight { get; set; }
    }

    public sealed class Kunde : INotifyPropertyChanged
    {
        private readonly Brush _accentBrush;
        private readonly Brush _borderBrush;
        private string _firmenname;
        private string _kennung;
        private string _adminEmail;
        private int _benutzer;
        private string _status;

        public Kunde(string firmenname, string kennung, string adminEmail, int benutzer,
            string status, Brush accentBrush, Brush borderBrush)
        {
            _firmenname = firmenname;
            _kennung = kennung;
            _adminEmail = adminEmail;
            _benutzer = benutzer;
            _status = status;
            _accentBrush = accentBrush;
            _borderBrush = borderBrush;
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
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBorderBrush)));
            }
        }

        public Brush StatusBorderBrush => Status == "Testphase" ? _accentBrush : _borderBrush;

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }
    }
}
