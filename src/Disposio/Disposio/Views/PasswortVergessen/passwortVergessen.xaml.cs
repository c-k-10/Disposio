using System;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Disposio.Services;
using Windows.System;

namespace Disposio.Views.PasswortVergessen;

public sealed partial class passwortVergessen : Page
{
    private const int MinPasswortLaenge = 8; // an die Regel in Neon Auth anpassen
    private string _email = "";

    // Farbe und Light/Dark Mode von der LoginPage. Wird beim Zurückgehen wieder mitgegeben.
    private DashboardAppearance? _appearance;

    public passwortVergessen()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is DashboardAppearance appearance)
        {
            _appearance = appearance;
            ApplyAppearance(appearance);
        }
    }

    // Übernimmt Akzentfarbe und Light/Dark Mode. Die Farbwerte entsprechen der LoginPage.
    private void ApplyAppearance(DashboardAppearance appearance)
    {
        bool hell = appearance.IsLight;
        RequestedTheme = hell ? ElementTheme.Light : ElementTheme.Dark;

        SetzeFarbe("PrimaryBrush", appearance.AccentHex);
        SetzeFarbe("PanelDarkBrush", hell ? "#F3F3F3" : "#1B1E23");
        SetzeFarbe("CardBorder", hell ? "#D1D5DB" : "#2A2C31");
        SetzeFarbe("TextMain", hell ? "#1A1F2A" : "#E6E9F0");
        SetzeFarbe("TextMuted", hell ? "#4A4F55" : "#9CA3AF");
        SetzeFarbe("InputBackground", hell ? "#FFFFFF" : "#222426");
        SetzeFarbe("InputBorder", hell ? "#D1D5DB" : "#2F313A");
        SetzeFarbe("ButtonTextOnPrimary", hell ? "#000000" : "#FFFFFF");

        if (Resources.TryGetValue("AppBackground", out var hintergrund)
            && hintergrund is LinearGradientBrush verlauf && verlauf.GradientStops.Count >= 2)
        {
            verlauf.GradientStops[0].Color = HexZuFarbe(hell ? "#FFFFFF" : "#0B0D12");
            verlauf.GradientStops[1].Color = HexZuFarbe(hell ? "#F3F3F3" : "#13161A");
        }
    }

    private void SetzeFarbe(string schluessel, string hex)
    {
        if (Resources.TryGetValue(schluessel, out var wert) && wert is SolidColorBrush pinsel)
            pinsel.Color = HexZuFarbe(hex);
    }

    private static Windows.UI.Color HexZuFarbe(string hex)
    {
        try
        {
            return Windows.UI.Color.FromArgb(0xFF,
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16));
        }
        catch
        {
            // Ungültiger Farbwert: Standard-Akzentfarbe verwenden.
            return Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x00, 0x4D);
        }
    }

    // ---- Schritt 1: Code anfordern ----

    private async void CodeSendenButton_Click(object sender, RoutedEventArgs e) => await CodeSendenAsync();

    private async void EmailBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) await CodeSendenAsync();
    }

    private async System.Threading.Tasks.Task CodeSendenAsync()
    {
        var email = EmailBox.Text.Trim();
        if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            Zeige("Bitte eine gültige E-Mail-Adresse eingeben.", InfoBarSeverity.Warning);
            return;
        }

        SetzeBusy(true);
        var ergebnis = await NeonAuthClient.Instanz.CodeSendenAsync(email);
        SetzeBusy(false);

        if (!ergebnis.Erfolg)
        {
            Zeige(ergebnis.Fehler ?? "Code konnte nicht gesendet werden.", InfoBarSeverity.Error);
            return;
        }

        _email = email;
        // Bewusst neutrale Formulierung: verrät nicht, ob die Adresse registriert ist.
        CodeHinweis.Text = $"Falls ein Konto für {email} existiert, haben wir einen Code gesendet.";
        SchrittEmail.Visibility = Visibility.Collapsed;
        SchrittCode.Visibility = Visibility.Visible;
        Meldung.IsOpen = false;
        CodeBox.Focus(FocusState.Programmatic);
    }

    // ---- Schritt 2: Passwort zurücksetzen ----

    private async void ZuruecksetzenButton_Click(object sender, RoutedEventArgs e) => await ZuruecksetzenAsync();

    private async void PasswortWiederholenBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) await ZuruecksetzenAsync();
    }

    private async System.Threading.Tasks.Task ZuruecksetzenAsync()
    {
        var code = CodeBox.Text.Trim();
        var neu = PasswortBox.Password;

        if (code.Length == 0)
        {
            Zeige("Bitte den Code aus der E-Mail eingeben.", InfoBarSeverity.Warning);
            return;
        }
        if (neu.Length < MinPasswortLaenge)
        {
            Zeige($"Das Passwort muss mindestens {MinPasswortLaenge} Zeichen lang sein.", InfoBarSeverity.Warning);
            return;
        }
        if (neu != PasswortWiederholenBox.Password)
        {
            Zeige("Die Passwörter stimmen nicht überein.", InfoBarSeverity.Warning);
            return;
        }

        SetzeBusy(true);
        var ergebnis = await NeonAuthClient.Instanz.PasswortZuruecksetzenAsync(_email, code, neu);
        SetzeBusy(false);

        if (!ergebnis.Erfolg)
        {
            Zeige(ergebnis.Fehler ?? "Zurücksetzen fehlgeschlagen. Code falsch oder abgelaufen?", InfoBarSeverity.Error);
            return;
        }

        PasswortBox.Password = "";
        PasswortWiederholenBox.Password = "";
        Zeige("Passwort geändert. Sie können sich jetzt anmelden.", InfoBarSeverity.Success);
        ZuruecksetzenButton.IsEnabled = false;
    }

    private void NeuerCode_Click(object sender, RoutedEventArgs e)
    {
        CodeBox.Text = "";
        SchrittCode.Visibility = Visibility.Collapsed;
        SchrittEmail.Visibility = Visibility.Visible;
        EmailBox.Text = _email;
        Meldung.IsOpen = false;
    }

    private void Zurueck_Click(object sender, RoutedEventArgs e)
    {
        // Mit Farbe/Modus zurück zur LoginPage, damit die Einstellung erhalten bleibt.
        if (_appearance is not null && Frame is Frame frame)
        {
            frame.Navigate(typeof(LoginPage), _appearance);
            frame.BackStack.Clear();
            return;
        }

        if (Frame?.CanGoBack == true)
            Frame.GoBack();
    }

    // ---- Hilfsmethoden ----

    private void SetzeBusy(bool busy)
    {
        Laedt.IsActive = busy;
        CodeSendenButton.IsEnabled = !busy;
        ZuruecksetzenButton.IsEnabled = !busy;
    }

    private void Zeige(string text, InfoBarSeverity art)
    {
        Meldung.Severity = art;
        Meldung.Message = text;
        Meldung.IsOpen = true;
    }
}
