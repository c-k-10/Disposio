using System;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Disposio.Services;
using Windows.System;

namespace Disposio.Views.PasswortVergessen;

public sealed partial class passwortVergessen : Page
{
    private const int MinPasswortLaenge = 8; // an die Regel in Neon Auth anpassen
    private string _email = "";

    public passwortVergessen()
    {
        InitializeComponent();
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
        if (Frame?.CanGoBack == true)
            Frame.GoBack();
        // TODO: sonst zur LoginPage navigieren, z. B. Frame.Navigate(typeof(LoginPage...));
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
