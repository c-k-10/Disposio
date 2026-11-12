using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
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
            "#263749", "#FF004D", "#007BFF", "#00C853", "#FF9100", "#7B2CBF"
        };

        // Hauptfarbe der Fahrer-App. Im Dark Mode wäre das dunkle Blau kaum sichtbar,
        // deshalb wird es dort (wie bei Material Design) zu einem hellen Blau.
        private const string DisposioBlau = "#263749";
        private const string DisposioBlauDunkel = "#A9C4E4";

        // Die Monatsstatistik. Bei gesperrter Firma ist sie die einzige erreichbare Ansicht.
        private const string StatistikId = "G02";

        // Nur Büro-Rollen. Fahrer haben am PC keinen Arbeitsbereich.
        private IReadOnlyList<FirmenRolle> _rollen = Array.Empty<FirmenRolle>();
        // Firma im Admin-Dashboard auf „Inaktiv“ gesetzt: Geschäftsführung und Kundenadmin sehen nur noch G02.
        private bool _nurStatistik;
        private bool _returningToLogin;
        // Standard wie in der Fahrer-App: hell.
        private bool _isLight = DarstellungSpeicher.IstHell;
        // Zuletzt markierter Menüpunkt, damit das Menü nach dem Umschalten von Hell/Dunkel neu gezeichnet werden kann.
        private string _menueAktiv = "";
        // Reißzwecke links oben: angeheftet = breite Leiste mit Namen, sonst schmal nur mit Symbolen.
        private bool _angeheftet = DarstellungSpeicher.MenueAngeheftet;
        private string _accentHex = DarstellungSpeicher.Akzent;

        // Auswahl im Farb-Menü oben rechts.
        private static readonly (string Name, string Hex)[] Farben =
        {
            ("Disposio-Blau", "#263749"), ("Blau", "#007BFF"), ("Grün", "#00C853"),
            ("Orange", "#FF9100"), ("Pink-Rot", "#FF004D"), ("Lila", "#7B2CBF")
        };

        public FirmenAnsicht()
        {
            InitializeComponent();
            // Kürzel im runden Feld oben rechts, z. B. „Christina Kiesl“ → „CK“.
            AccountNameText.RegisterPropertyChangedCallback(TextBlock.TextProperty,
                (_, _) => AvatarText.Text = Initialen(AccountNameText.Text));
            RailAnwenden();
            ApplyAppearance();
            FarbenAufbauen();
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
                FarbenAufbauen();
            }

            if (!EnsureAccess())
                return;

            // Vorschau und Admin-Zugang zeigen alle Büro-Rollen, ein Firmenkonto nur seine eigenen.
            IEnumerable<FirmenRolle> rollen = BenutzerSitzung.IstFirmenVorschau || BenutzerSitzung.Firmenzugang is not null
                ? FirmenRollenKatalog.Alle
                : BenutzerSitzung.AktuellerBenutzer?.FirmenRollen ?? Array.Empty<FirmenRolle>();
            _rollen = rollen.Where(rolle => rolle != FirmenRolle.Fahrer).ToArray();
            _eigeneRollen = EigeneRollenDerSitzung();
            // Gesperrte Firma: keine eigenen Rollen, nur die Statistik. Der Admin-Zugang sieht weiter alles.
            _nurStatistik = BenutzerSitzung.Firmenzugang is null && BenutzerSitzung.AktuellerBenutzer?.FirmaGesperrt == true;
            if (_nurStatistik)
                _eigeneRollen = Array.Empty<EigeneRolle>();
            // Ohne Standardrolle und ohne Menüpunkt aus einer eigenen Rolle bleibt nichts anzuzeigen.
            if (_rollen.Count == 0 && EigeneMenuepunkteGesamt() == 0)
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
        // Wer nur eine eigene Rolle hat, startet bei deren erstem Menüpunkt. Bei gesperrter Firma: die Statistik.
        private string StartId() => _nurStatistik ? StatistikId
            : _rollen.Count > 0 ? AnsichtenKatalog.Menue(_rollen[0])[0].Id : ErsteEigeneAnsicht();

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

            if (_nurStatistik)
            {
                AdminAccessText.Text = "Ihr Unternehmen ist bei Disposio zurzeit gesperrt. Sie können nur noch die " +
                    "Monatsstatistik ansehen und als PDF herunterladen. Bitte wenden Sie sich an Disposio.";
                BannerDashboardButton.Visibility = Visibility.Collapsed;
                AdminAccessBanner.Visibility = Visibility.Visible;
            }
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

        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            // Für D01 „Seit Ihrer letzten Schicht“ den Zeitpunkt merken. Läuft im Hintergrund weiter,
            // die Abmeldung wartet nicht darauf und klappt auch ohne Verbindung.
            if (BenutzerSitzung.AktuellerBenutzer is { } benutzer)
                _ = TagesuebersichtRepository.AbmeldungSpeichernAsync(benutzer.Id);
            ReturnToLogin();
        }

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
            // Bei gesperrter Firma nur die Statistik, auch für den Kundenadmin.
            if (ansicht is null || !(_nurStatistik
                    ? ansicht.Id == StatistikId
                    : _rollen.Contains(ansicht.Rolle) || EigeneRollenRegeln.Erlaubt(_eigeneRollen, ansicht)))
            {
                ShowNoAccess();
                return;
            }

            string bereich = _nurStatistik ? "Nur Statistik" : FirmenRollenKatalog.Bezeichnung(ansicht.Rolle);
            CrumbText.Text = $"{bereich} / {ansicht.Titel} · {ansicht.Id}";
            PageTitle.Text = ansicht.Titel;
            PurposeText.Text = ansicht.Zweck;

            var kontext = new FirmenAnsichtKontext(ansicht, _rollen, BenutzerSitzung.IstFirmenVorschau, parameter, Open,
                () => ReturnToLogin("Ihr Zugriff ist nicht mehr verfügbar. Bitte melden Sie sich erneut an."));
            try
            {
                // K01–K04 baut die Firmenansicht selbst (FirmenAnsicht.Kundenadmin.cs, FirmenAnsicht.EigeneRollen.cs).
                // Alle anderen Ansichten kommen aus der Datei ihres Rollenbereichs, zum Beispiel F01 und F02.
                ViewHost.Child = ansicht.Id switch
                {
                    "K01" => CreateUsersView(),
                    "K02" => CreateUserEditView(),
                    "K03" => CreateRollenUndRechteView(kontext),
                    "K04" => CreatePasswordPolicyView(),
                    _ => CreateView(kontext) ?? CreatePlaceholder(ansicht)
                };
            }
            catch (Exception)
            {
                // Ein Fehler in einer einzelnen Ansicht soll nicht die ganze Firmenansicht beenden.
                var content = new StackPanel { Spacing = 18 };
                content.Children.Add(Text("Diese Ansicht konnte nicht geöffnet werden. Bitte versuchen Sie es erneut."));
                content.Children.Add(GoButton("Zur Startansicht", StartId(), primary: false));
                ViewHost.Child = Card(content);
            }

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
            _menueAktiv = activeId;
            MenuPanel.Children.Clear();
            if (_nurStatistik)
            {
                // Gesperrte Firma: ein einziger Menüpunkt, unabhängig von den Rollen.
                MenueAbschnitt("Nur Statistik", new[] { AnsichtenKatalog.Finden(StatistikId)! }, activeId);
                return;
            }

            foreach (FirmenRolle rolle in _rollen)
                MenueAbschnitt(FirmenRollenKatalog.Bezeichnung(rolle), AnsichtenKatalog.Menue(rolle), activeId);

            // Danach je eigene Rolle ein eigener Menüabschnitt (FirmenAnsicht.EigeneRollen.cs).
            EigeneRollenMenue(activeId);
        }

        // Abschnitt der Navigationsleiste: kleine Überschrift (Rolle), darunter die Menüpunkte mit Symbol.
        private void MenueAbschnitt(string titel, IEnumerable<AnsichtInfo> menuepunkte, string activeId)
        {
            // Zwischen den Abschnitten ein Strich über die ganze Breite.
            if (MenuPanel.Children.Count > 0)
                MenuPanel.Children.Add(Trennstrich(breit: true));
            MenuPanel.Children.Add(RailUeberschrift(titel));
            bool erster = true;
            foreach (AnsichtInfo item in menuepunkte)
            {
                // Zwischen den Menüpunkten ein kurzer, feiner Strich zur Abgrenzung.
                if (!erster)
                    MenuPanel.Children.Add(Trennstrich(breit: false));
                MenuPanel.Children.Add(RailButton(item, item.Id == activeId));
                erster = false;
            }
        }

        private static Border Trennstrich(bool breit) => new()
        {
            Height = 1,
            Margin = breit ? new Thickness(0, 8, 0, 0) : new Thickness(18, 0, 18, 0),
            Background = Brush("BorderBrush")
        };

        private void Pin_Click(object sender, RoutedEventArgs e)
        {
            _angeheftet = !_angeheftet;
            DarstellungSpeicher.MenueAngeheftet = _angeheftet;
            RailAnwenden();
            BuildMenu(_menueAktiv);
        }

        private void RailAnwenden()
        {
            RailColumn.Width = new GridLength(_angeheftet ? 230 : 68);
            // Angeheftet: Symbol „Lösen“, eingeklappt: Symbol „Anheften“.
            PinSymbol.Glyph = _angeheftet ? "\uE77A" : "\uE718";
            string hinweis = _angeheftet ? "Menü einklappen (nur Symbole)" : "Menü anheften (mit Namen)";
            ToolTipService.SetToolTip(PinButton, hinweis);
            AutomationProperties.SetName(PinButton, hinweis);
        }

        private TextBlock RailUeberschrift(string titel) => new()
        {
            Visibility = _angeheftet ? Visibility.Visible : Visibility.Collapsed,
            Text = titel.ToUpper(CultureInfo.CurrentCulture),
            // Gut sichtbare Abschnittsüberschrift: größer, fett und in der Hauptfarbe.
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            CharacterSpacing = 80,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = Brush("AccentBrush"),
            Margin = new Thickness(12, MenuPanel.Children.Count == 0 ? 2 : 12, 4, 6)
        };

        // Menüpunkt wie in der NavigationBar der Fahrer-App: Symbol in einer runden „Pille“, darunter der Name.
        // Der aktive Punkt hat eine graue Pille, ein Symbol in der Hauptfarbe und fette Schrift.
        private Button RailButton(AnsichtInfo item, bool aktiv)
        {
            var symbol = new FontIcon
            {
                Glyph = MenueSymbol(item.Id),
                FontSize = 18,
                Foreground = Brush(aktiv ? "AccentBrush" : "MutedTextBrush")
            };
            // Eingeklappt: nur das Symbol in einer runden Pille. Angeheftet: Symbol links, Name daneben,
            // der aktive Punkt bekommt die graue Pille über die ganze Zeile (wie ein Navigationsmenü bei Material Design).
            var pille = new Border
            {
                Width = _angeheftet ? 36 : 44,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = aktiv && !_angeheftet ? Brush("StatusBoxBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Child = symbol
            };
            var name = new TextBlock
            {
                Text = item.Menuename,
                FontSize = 14,
                Visibility = _angeheftet ? Visibility.Visible : Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = aktiv ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Brush(aktiv ? "TextBrush" : "MutedTextBrush")
            };
            var inhalt = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = _angeheftet ? HorizontalAlignment.Left : HorizontalAlignment.Center
            };
            inhalt.Children.Add(pille);
            inhalt.Children.Add(name);

            var button = new Button { Content = inhalt, Style = Stil("RailButton") };
            if (_angeheftet)
            {
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.Padding = new Thickness(6, 4, 8, 4);
                button.CornerRadius = new CornerRadius(19);
                if (aktiv)
                    button.Background = Brush("StatusBoxBrush");
            }
            ToolTipService.SetToolTip(button, item.Titel);
            AutomationProperties.SetName(button, item.Menuename ?? item.Titel);
            if (aktiv)
                // In kleinen Fenstern scrollt das Menü, bis der aktive Punkt sichtbar ist.
                button.Loaded += (_, _) => button.StartBringIntoView();
            button.Click += (_, _) => Open(item.Id);
            return button;
        }

        // Symbole aus „Segoe Fluent Icons“ je Ansicht.
        private static string MenueSymbol(string id) => id switch
        {
            "D01" or "G01" or "L01" => "\uE80F",          // Startseite
            "D02" => "\uE7B8",                            // Paket
            "D04" => "\uE8F0",                            // Route
            "D08" => "\uE707",                            // Karte / Pin
            "D09" => "\uE8BD",                            // Nachricht
            "D10" => "\uE715",                            // Mail
            "D11" => "\uE8A5",                            // Dokument
            "D15" or "L03" => "\uE7BA",                   // Meldung
            "F01" or "L02" => "\uE804",                   // Fahrzeug
            "F03" => "\uE77B",                            // Fahrer
            "F04" => "\uE90F",                            // Wartung
            "G02" or "L05" => "\uE9D2",                   // Kennzahlen
            "K01" => "\uE716",                            // Benutzer
            "K06" => "\uE902",                            // Abteilungen
            "K03" => "\uE8D7",                            // Rollen
            "K04" => "\uE72E",                            // Passwort
            "K05" or "L06" => "\uE81C",                   // Protokoll
            _ => "\uE8A9"
        };

        private static string Initialen(string name)
        {
            string[] teile = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return teile.Length switch
            {
                0 => "",
                1 => teile[0][..1].ToUpper(CultureInfo.CurrentCulture),
                _ => (teile[0][..1] + teile[^1][..1]).ToUpper(CultureInfo.CurrentCulture)
            };
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

        // Tabellenzelle für die Listen in K01 und K03.
        private TextBlock Cell(string text, bool header, bool bold) => new()
        {
            Text = text,
            FontSize = header ? 14 : 16,
            FontWeight = header || bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(header ? "MutedTextBrush" : "TextBrush")
        };

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
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(20, 18, 20, 18),
            Child = child
        };

        // ── Darstellung (wie im Admin-Dashboard) ─────────────────────────────────────────

        // Knopf oben rechts: zwischen Hell und Dunkel wechseln. Alle Farben hängen an denselben Brush-Objekten,
        // deshalb passt sich die geöffnete Ansicht sofort an. Nur das Menü wird neu gezeichnet.
        private void Modus_Click(object sender, RoutedEventArgs e)
        {
            _isLight = !_isLight;
            DarstellungSpeicher.IstHell = _isLight;
            ApplyAppearance();
            BuildMenu(_menueAktiv);
            FarbenAufbauen();
        }

        // Runde Farbpunkte im Flyout. Der gewählte bekommt einen Ring.
        private void FarbenAufbauen()
        {
            FarbAuswahl.Children.Clear();
            foreach (var (name, hex) in Farben)
            {
                bool gewaehlt = string.Equals(hex, _accentHex, StringComparison.OrdinalIgnoreCase);
                string anzeige = !_isLight && hex == DisposioBlau ? DisposioBlauDunkel : hex;
                var punkt = new Border
                {
                    Width = 30,
                    Height = 30,
                    CornerRadius = new CornerRadius(15),
                    BorderThickness = new Thickness(2),
                    BorderBrush = gewaehlt ? Brush("TextBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Child = new Ellipse
                    {
                        Width = 20,
                        Height = 20,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Fill = new SolidColorBrush(ParseColor(anzeige))
                    }
                };
                var knopf = new Button
                {
                    Content = punkt,
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    MinHeight = 0,
                    BorderThickness = new Thickness(0),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    CornerRadius = new CornerRadius(15)
                };
                ToolTipService.SetToolTip(knopf, name);
                AutomationProperties.SetName(knopf, name);
                knopf.Click += (_, _) => FarbeWaehlen(hex);
                FarbAuswahl.Children.Add(knopf);
            }
        }

        private void FarbeWaehlen(string hex)
        {
            _accentHex = hex;
            DarstellungSpeicher.Akzent = hex;
            ApplyAppearance();
            BuildMenu(_menueAktiv);
            FarbenAufbauen();
            FarbFlyout.Hide();
        }

        private void ApplyAppearance()
        {
            // Mond = zu Dunkel wechseln, Sonne = zu Hell wechseln.
            ModusSymbol.Glyph = _isLight ? "\uE708" : "\uE706";
            ToolTipService.SetToolTip(ModusButton, _isLight ? "Zu Dark Mode wechseln" : "Zu Light Mode wechseln");
            AutomationProperties.SetName(ModusButton, _isLight ? "Dark Mode einschalten" : "Light Mode einschalten");

            RequestedTheme = _isLight ? ElementTheme.Light : ElementTheme.Dark;
            MainWindow.TitelleisteFaerben(_isLight);
            // Farben der Fahrer-App (DisposioFarben) und ihr Gegenstück im Dark Mode.
            SetBrush("BackgroundBrush", _isLight ? "#F4F5F9" : "#12161C");
            SetBrush("SidebarBrush", _isLight ? "#FFFFFF" : "#1B2129");
            SetBrush("SurfaceBrush", _isLight ? "#FFFFFF" : "#1B2129");
            SetBrush("BorderBrush", _isLight ? "#D5D9E0" : "#2E3641");
            SetBrush("TextBrush", _isLight ? "#1F2933" : "#E6EAF0");
            SetBrush("MutedTextBrush", _isLight ? "#6B7280" : "#9AA3B0");
            SetBrush("InputBrush", _isLight ? "#FFFFFF" : "#12161C");
            SetBrush("ErrorBrush", _isLight ? "#C62828" : "#FF7B7B");
            SetBrush("StatusBoxBrush", _isLight ? "#F0F1F3" : "#232A33");
            SetBrush("LateBrush", _isLight ? "#C62828" : "#FF7B7B");

            string accentHex = !_isLight && string.Equals(_accentHex, DisposioBlau, StringComparison.OrdinalIgnoreCase)
                ? DisposioBlauDunkel
                : _accentHex;
            Color accent = ParseColor(accentHex);
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
