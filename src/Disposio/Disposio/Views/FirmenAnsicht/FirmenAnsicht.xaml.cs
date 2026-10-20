using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace Disposio.Views
{
    // Rahmen für alle Firmenkonten: links das Menü je Rolle, rechts die gewählte Ansicht.
    // Die Ansichten selbst liegen in eigenen Dateien je Rollenbereich (Kundenadmin/, Fuhrpark/ …).
    // Jede Ansicht mit Firmendaten prüft vor dem Laden AuthService.FirmenZugriffPruefenAsync.
    public sealed partial class FirmenAnsicht : Page
    {
        private static readonly string[] AccentColors =
        {
            "#FF004D", "#007BFF", "#00C853", "#FF9100", "#7B2CBF"
        };

        // Nur Büro-Rollen. Fahrer haben am PC keinen Arbeitsbereich.
        private IReadOnlyList<FirmenRolle> _rollen = Array.Empty<FirmenRolle>();
        private bool _returningToLogin;
        private bool _isLight;
        private string _accentHex = "#7B2CBF";

        public FirmenAnsicht()
        {
            InitializeComponent();
            ApplyAppearance();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is DashboardAppearance appearance)
            {
                if (AccentColors.Contains(appearance.AccentHex, StringComparer.OrdinalIgnoreCase))
                    _accentHex = appearance.AccentHex.ToUpperInvariant();

                _isLight = appearance.IsLight;
                ApplyAppearance();
            }

            if (!EnsureAccess())
                return;

            // Vorschau und Admin-Zugang zeigen alle Büro-Rollen, ein Firmenkonto nur seine eigenen.
            IEnumerable<FirmenRolle> rollen = BenutzerSitzung.IstFirmenVorschau || BenutzerSitzung.Firmenzugang is not null
                ? FirmenRollenKatalog.Alle
                : BenutzerSitzung.AktuellerBenutzer?.FirmenRollen ?? Array.Empty<FirmenRolle>();
            _rollen = rollen.Where(rolle => rolle != FirmenRolle.Fahrer).ToArray();
            _eigeneRollen = EigeneRollenDerSitzung();
            if (_rollen.Count == 0 && EigeneMenuepunkteGesamt() == 0)
                if (_rollen.Count == 0)
            {
                // Erst nach der laufenden Navigation zurück, damit WinUI nicht verschachtelt navigiert.
                _returningToLogin = true;
                DispatcherQueue.TryEnqueue(() => ReturnToLogin(
                    "Ihrem Konto ist noch keine Rolle zugewiesen. Bitte wenden Sie sich an Ihren Kundenadmin."));
                return;
            }

            UpdateAccountDisplay();
            Open(StartId());
        }

        // Startansicht: der erste Menüpunkt der ersten Rolle (bei Disponenten die Tagesübersicht).
        // Wer nur eine eigene Rolle hat, startet bei deren erstem Menüpunkt.
        private string StartId() => _rollen.Count > 0 ? AnsichtenKatalog.Menue(_rollen[0])[0].Id : ErsteEigeneAnsicht();

        private void UpdateAccountDisplay()
        {
            if (BenutzerSitzung.IstFirmenVorschau)
            {
                // Eine Debug-Vorschau ist kein angemeldetes Konto und wird klar benannt.
                CompanyNameText.Text = "Entwicklungsansicht";
                CompanyDetailText.Text = "Alle Büro-Rollen ohne Anmeldung";
                AccountNameText.Text = "Firmenansicht-Vorschau";
                AccountRoleText.Text = "Es werden keine Firmendaten geladen.";
                return;
            }

            AngemeldeterBenutzer? user = BenutzerSitzung.AktuellerBenutzer;
            if (BenutzerSitzung.Firmenzugang is AdminFirmenzugang zugang)
            {
                CompanyNameText.Text = zugang.Firmenname;
                CompanyDetailText.Text = "Admin-Zugang";
                AccountNameText.Text = user?.Anzeigename ?? "Entwicklungsansicht";
                AccountRoleText.Text = "MegakrassenAdmin";
                AdminAccessText.Text = $"Sie sehen die Firmenansicht von {zugang.Firmenname} als MegakrassenAdmin. " +
                    "Der Zugang wurde im Aktionsprotokoll gespeichert.";
                AdminAccessBanner.Visibility = Visibility.Visible;
                BackToDashboardLink.Visibility = Visibility.Visible;
                return;
            }

            CompanyNameText.Text = user?.Firmenname ?? "";
            CompanyDetailText.Text = _rollen.Contains(FirmenRolle.Abteilungsleiter) && !string.IsNullOrWhiteSpace(user?.Abteilung)
                ? $"Abteilung {user!.Abteilung}"
                : "Eigenes Unternehmen";
            AccountNameText.Text = user?.Anzeigename ?? "";
            AccountRoleText.Text = string.Join(", ", (user?.FirmenRollen ?? _rollen).Select(FirmenRollenKatalog.Bezeichnung));
        }

        private bool EnsureAccess()
        {
            // Eine direkte Navigation zur Seite reicht nicht. Erst eine Anmeldung mit Firmenrolle
            // (oder die ausdrückliche Debug-Vorschau) öffnet die Firmenansicht.
            if (!_returningToLogin && BenutzerSitzung.FirmenansichtErlaubt)
                return true;

            if (!_returningToLogin)
            {
                _returningToLogin = true;
                // Wie im Dashboard: erst nach der laufenden Navigation zurück zum Login.
                DispatcherQueue.TryEnqueue(() => ReturnToLogin("Bitte melden Sie sich mit einem Firmenkonto an."));
            }
            return false;
        }

        private void ReturnToLogin(string? message = null)
        {
            _returningToLogin = true;
            BenutzerSitzung.Abmelden();
            MenuPanel.Children.Clear();
            ViewHost.Child = null;
            var appearance = new DashboardAppearance { AccentHex = _accentHex, IsLight = _isLight };
            object parameter = message is null ? appearance : new LoginNavigationParameter(appearance, message);
            if (Frame is Frame frame && ReferenceEquals(frame.Content, this) && frame.Navigate(typeof(LoginPage), parameter))
                frame.BackStack.Clear();
        }

        private void Logout_Click(object sender, RoutedEventArgs e) => ReturnToLogin();

        // Beendet nur den Admin-Zugang. Der MegakrasserAdmin bleibt angemeldet.
        private void BackToDashboard_Click(object sender, RoutedEventArgs e)
        {
            BenutzerSitzung.FirmenzugangBeenden();
            _returningToLogin = true;
            var appearance = new DashboardAppearance { AccentHex = _accentHex, IsLight = _isLight };
            if (Frame is Frame frame && ReferenceEquals(frame.Content, this) && frame.Navigate(typeof(DashboardAnsicht), appearance))
                frame.BackStack.Clear();
        }

        // ── Ansichten wechseln ───────────────────────────────────────────────────────────

        // parameter ist ein optionaler Startwert, zum Beispiel die Fahrzeug-Id, wenn F01 die Ansicht F02 öffnet.
        private void Open(string id, object? parameter = null)
        {
            if (!EnsureAccess())
                return;

            // Auch eine Ansicht, die nicht im Menü steht, öffnet sich nur mit der passenden Rolle.
            AnsichtInfo? ansicht = AnsichtenKatalog.Finden(id);
            // Eine Ansicht öffnet sich über die passende Standardrolle oder eine eigene Rolle mit dieser Berechtigung.
            if (ansicht is null || (!_rollen.Contains(ansicht.Rolle) && !EigeneRollenRegeln.Erlaubt(_eigeneRollen, ansicht)))
            {
                ShowNoAccess();
                return;
            }

            CrumbText.Text = $"{FirmenRollenKatalog.Bezeichnung(ansicht.Rolle)} / {ansicht.Titel} · {ansicht.Id}";
            PageTitle.Text = ansicht.Titel;
            PurposeText.Text = ansicht.Zweck;
            ViewHost.Child = ansicht.Id switch
            {
                "K01" => CreateUsersView(),
                "K02" => CreateUserEditView(),
                "K03" => CreateRollenUndRechteView(),
                "K04" => CreatePasswordPolicyView(),
                _ => CreatePlaceholder(ansicht)
            };
            // Bei Unteransichten (zum Beispiel K02) bleibt ihr Menüpunkt (K01) markiert.
            BuildMenu(ansicht.Oberansicht ?? ansicht.Id);
            ContentScroll.ChangeView(null, 0, null, true);
        }

        // Jede fertige Ansicht steht in der Datei ihres Rollenbereichs, zum Beispiel Fuhrpark/FuhrparkAnsichten.cs.
        // So arbeitet jeder in seinem eigenen Ordner. Ohne Eintrag erscheint der Platzhalter.
        private static UIElement? CreateView(FirmenAnsichtKontext kontext) => kontext.Ansicht.Rolle switch
        {
            FirmenRolle.Disponent => DispositionAnsichten.Erstellen(kontext),
            FirmenRolle.Fuhrparkmanager => FuhrparkAnsichten.Erstellen(kontext),
            FirmenRolle.Abteilungsleiter => AbteilungAnsichten.Erstellen(kontext),
            FirmenRolle.Inhaber => GeschaeftsfuehrungAnsichten.Erstellen(kontext),
            FirmenRolle.Kundenadmin => KundenadminAnsichten.Erstellen(kontext),
            _ => null
        };

        private void BuildMenu(string activeId)
        {
            MenuPanel.Children.Clear();
            foreach (FirmenRolle rolle in _rollen)
            {
                MenuPanel.Children.Add(new TextBlock
                {
                    Text = FirmenRollenKatalog.Bezeichnung(rolle).ToUpper(CultureInfo.CurrentCulture),
                    FontSize = 12,
                    CharacterSpacing = 100,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("MutedTextBrush"),
                    Margin = new Thickness(4, MenuPanel.Children.Count == 0 ? 0 : 18, 0, 6)
                });

                foreach (AnsichtInfo item in AnsichtenKatalog.Menue(rolle))
                {
                    var button = new Button { Content = item.Menuename, Style = Stil("MenuButton") };
                    if (item.Id == activeId)
                    {
                        button.Background = Brush("AccentBrush");
                        button.Foreground = Brush("AccentTextBrush");
                        button.FontWeight = FontWeights.SemiBold;
                        // In kleinen Fenstern scrollt das Menü, bis der aktive Punkt sichtbar ist.
                        button.Loaded += (_, _) => button.StartBringIntoView();
                    }
                    button.Click += (_, _) => Open(item.Id);
                    MenuPanel.Children.Add(button);
                }
            }

            // Danach je eigene Rolle ein eigener Menüabschnitt (FirmenAnsicht.EigeneRollen.cs).
            EigeneRollenMenue(activeId);
        }

        // S03: nachvollziehbarer Hinweis statt einer leeren Seite.
        private void ShowNoAccess()
        {
            CrumbText.Text = "Kein Zugriff · S03";
            PageTitle.Text = "Kein Zugriff";
            PurposeText.Text = "Nachvollziehbarer Fehler bei fehlenden Rechten.";
            var content = new StackPanel { Spacing = 18 };
            content.Children.Add(Text("Für diese Ansicht fehlt Ihrem Konto die passende Rolle. Bitte wenden Sie sich an Ihren Kundenadmin."));
            content.Children.Add(GoButton("Zur Startansicht", StartId(), primary: true));
            ViewHost.Child = Card(content);
            BuildMenu("");
        }

        private UIElement CreatePlaceholder(AnsichtInfo ansicht)
        {
            var content = new StackPanel { Spacing = 18 };
            content.Children.Add(Text("Diese Ansicht ist vorbereitet. Ihr Inhalt folgt im nächsten Schritt."));
            content.Children.Add(Muted($"Wireframe {ansicht.Id} · Rolle {FirmenRollenKatalog.Bezeichnung(ansicht.Rolle)}"));

            // Nur ein echtes Firmenkonto hat eine eigene Abteilung. Beim Admin-Zugang gibt es keine.
            if (ansicht.Rolle == FirmenRolle.Abteilungsleiter &&
                BenutzerSitzung.AktuellerBenutzer is { UnternehmenId: not null } user &&
                string.IsNullOrWhiteSpace(user.Abteilung))
            {
                content.Children.Add(Text("Ihrem Konto ist noch keine Abteilung zugeordnet. Bitte wenden Sie sich an Ihren Kundenadmin."));
            }

            // Unteransichten sind nicht im Menü. So bleiben trotzdem alle Wireframes erreichbar.
            IReadOnlyList<AnsichtInfo> unteransichten = AnsichtenKatalog.Unteransichten(ansicht.Id);
            if (unteransichten.Count > 0)
            {
                content.Children.Add(Muted("Von hier aus erreichbar:"));
                var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                foreach (AnsichtInfo unteransicht in unteransichten)
                    links.Children.Add(GoButton($"{unteransicht.Titel} · {unteransicht.Id}", unteransicht.Id, primary: false));
                content.Children.Add(new ScrollViewer
                {
                    Content = links,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollMode = ScrollMode.Enabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
                });
            }

            if (ansicht.Oberansicht is string oberId && AnsichtenKatalog.Finden(oberId) is AnsichtInfo ober)
                content.Children.Add(GoButton($"Zurück zu {ober.Titel}", ober.Id, primary: false));

            return Card(content);
        }

        // ── Kleine Bausteine ─────────────────────────────────────────────────────────────

        private TextBlock Text(string text) => new()
        {
            Text = text,
            FontSize = 17,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextBrush")
        };

        private TextBlock Muted(string text) => new()
        {
            Text = text,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("MutedTextBrush")
        };

        private Button GoButton(string label, string id, bool primary)
        {
            var button = new Button
            {
                Content = label,
                Style = Stil(primary ? "PrimaryButton" : "DashboardButton"),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            button.Click += (_, _) => Open(id);
            return button;
        }

        private Border Card(UIElement child) => new()
        {
            Background = Brush("SurfaceBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(26, 24, 26, 24),
            Child = child
        };

        // ── Darstellung (wie im Admin-Dashboard) ─────────────────────────────────────────

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
            SetBrush("ErrorBrush", _isLight ? "#B91C1C" : "#FF939B");

            Color accent = ParseColor(_accentHex);
            Color foreground = BestAccentForeground(accent);
            SetBrush("AccentBrush", accent);
            SetBrush("AccentHoverBrush", Mix(accent, Microsoft.UI.Colors.White, 0.12));
            SetBrush("AccentPressedBrush", Mix(accent,
                foreground == Microsoft.UI.Colors.Black ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black, 0.06));
            SetBrush("AccentTextBrush", foreground);
        }

        // Farben und Stile liegen in Themes/FirmenStil.xaml und gelten für alle Firmenansichten.
        private static SolidColorBrush Brush(string key) => (SolidColorBrush)Application.Current.Resources[key];

        private static Style Stil(string key) => (Style)Application.Current.Resources[key];

        private static void SetBrush(string key, string hex) => SetBrush(key, ParseColor(hex));

        private static void SetBrush(string key, Color color) => Brush(key).Color = color;

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

            // Schwarz oder Weiß, je nachdem, was auf der Akzentfarbe besser lesbar ist.
            double luminance = 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
            double blackContrast = (luminance + 0.05) / 0.05;
            double whiteContrast = 1.05 / (luminance + 0.05);
            return blackContrast >= whiteContrast ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
        }
    }
}
