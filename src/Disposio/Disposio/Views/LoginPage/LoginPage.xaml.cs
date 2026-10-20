using Disposio.Services;
using Disposio.Views.PasswortVergessen;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI;
using Disposio.Models;
using Disposio.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Disposio.Views
{
    public sealed partial class LoginPage : Page
    {
        private bool _isSigningIn;

        public LoginPage()
        {
            this.InitializeComponent();
            UpdateAccountType();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            DashboardAppearance? appearance = e.Parameter as DashboardAppearance;
            if (e.Parameter is LoginNavigationParameter parameter)
            {
                appearance = parameter.Appearance;
                ShowLoginStatus(parameter.Hinweis);
            }
            if (appearance is not null)
            {
                foreach (ComboBoxItem item in ColorCombo.Items)
                {
                    if (string.Equals(item.Tag as string, appearance.AccentHex, StringComparison.OrdinalIgnoreCase))
                    {
                        ColorCombo.SelectedItem = item;
                        break;
                    }
                }
                ModeSwitch.IsOn = appearance.IsLight;
                RequestedTheme = appearance.IsLight ? ElementTheme.Light : ElementTheme.Dark;
                ApplyTheme(appearance.IsLight);
            }
        }

        private bool IsPlatformAccount => AccountTypeCombo.SelectedItem is ComboBoxItem { Tag: "Plattform" };

        private void AccountTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAccountType();

        private void UpdateAccountType()
        {
            // SelectionChanged kann schon beim Aufbau der XAML-Datei ausgelöst werden.
            // Nach InitializeComponent sind alle Felder vorhanden; vorher warten wir ab.
            if (CompanyField is null || PasswordBox is null || LoginStatusText is null)
                return;

            CompanyField.Visibility = IsPlatformAccount ? Visibility.Collapsed : Visibility.Visible;
            PasswordBox.Password = "";
            LoginStatusText.Visibility = Visibility.Collapsed;
        }

        private void ShowLoginStatus(string message)
        {
            LoginStatusText.Text = message;
            LoginStatusText.Visibility = Visibility.Visible;
        }

        private void ColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if ((sender as ComboBox)?.SelectedItem is ComboBoxItem item && item.Tag is string hex)
            {
                try
                {
                    var color = Color.FromArgb(0xFF,
                        Convert.ToByte(hex.Substring(1, 2), 16),
                        Convert.ToByte(hex.Substring(3, 2), 16),
                        Convert.ToByte(hex.Substring(5, 2), 16));

                    if (this.Resources["PrimaryBrush"] is SolidColorBrush sb)
                    {
                        sb.Color = color;
                    }
                }
                catch { }
            }
        }

        private void ModeSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                bool isLight = ts.IsOn;
                // set control theme
                this.RequestedTheme = isLight ? ElementTheme.Light : ElementTheme.Dark;
                // update page resources to match theme (backgrounds, text colors)
                ApplyTheme(isLight);
            }
        }

        private void ApplyTheme(bool isLight)
        {
            try
            {
                if (this.Resources["PanelDarkBrush"] is SolidColorBrush panelDark)
                    panelDark.Color = isLight ? Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3) : Windows.UI.Color.FromArgb(0xFF, 0x1B, 0x1E, 0x23);

                if (this.Resources["PanelBrush"] is SolidColorBrush panel)
                    panel.Color = isLight ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0xFF, 0x26, 0x29, 0x2D);

                if (this.Resources["TextMain"] is SolidColorBrush textMain)
                    textMain.Color = isLight ? Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1F, 0x2A) : Windows.UI.Color.FromArgb(0xFF, 0xE6, 0xE9, 0xF0);

                if (this.Resources["TextMuted"] is SolidColorBrush textMuted)
                    textMuted.Color = isLight ? Windows.UI.Color.FromArgb(0xFF, 0x4A, 0x4F, 0x55) : Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xA3, 0xAF);

                // background gradient
                if (this.Resources["AppBackground"] is LinearGradientBrush bg)
                {
                    if (bg.GradientStops.Count >= 2)
                    {
                        bg.GradientStops[0].Color = isLight ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0xFF, 0x0B, 0x0D, 0x12);
                        bg.GradientStops[1].Color = isLight ? Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3) : Windows.UI.Color.FromArgb(0xFF, 0x13, 0x16, 0x1A);
                    }
                }

                // inputs: background and border follow theme (ensure visible in light mode)
                if (this.Resources["InputBackground"] is SolidColorBrush inputBg)
                    inputBg.Color = isLight ? Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3) : Windows.UI.Color.FromArgb(0xFF, 0x22, 0x24, 0x26);

                if (this.Resources["InputBorder"] is SolidColorBrush inputBorder)
                    inputBorder.Color = isLight ? Windows.UI.Color.FromArgb(0xFF, 0xD1, 0xD5, 0xDB) : Windows.UI.Color.FromArgb(0xFF, 0x2F, 0x31, 0x3A);

                // speech bubble colors
                if (this.Resources["SpeechBubbleBackground"] is SolidColorBrush speechBg)
                    speechBg.Color = isLight ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0xFF, 0x22, 0x24, 0x29);

                if (this.Resources["SpeechBorderBrush"] is SolidColorBrush speechBorder)
                    speechBorder.Color = isLight ? Windows.UI.Color.FromArgb(0xFF, 0xD1, 0xD5, 0xDB) : Windows.UI.Color.FromArgb(0xFF, 0x2F, 0x31, 0x3A);

                // adjust button background if it uses PrimaryBrush (keep primary color)
                if (this.Resources["ButtonTextOnPrimary"] is SolidColorBrush btnText)
                    btnText.Color = isLight ? Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00) : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            }
            catch
            {
                // ignore resource update errors
            }
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            // await lässt das Fenster weiter reagieren. Deshalb sperren wir vor dem Warten
            // alle Login-Eingaben: Ein Doppelklick darf keine zweite Anmeldung starten.
            if (_isSigningIn)
                return;

            _isSigningIn = true;
            LoginButton.IsEnabled = AccountTypeCombo.IsEnabled = CompanyBox.IsEnabled =
                EmailBox.IsEnabled = PasswordBox.IsEnabled = false;
            LoginButton.Content = "Anmeldung läuft …";
            LoginStatusText.Visibility = Visibility.Collapsed;
            try
            {
                // Eine leere Firmenkennung bedeutet ausdrücklich „Plattformkonto“.
                // AuthService prüft trotzdem Passwort und Rolle; die Auswahl selbst erteilt keine Rechte.
                string company = IsPlatformAccount ? "" : CompanyBox.Text.Trim();
                string email = EmailBox.Text.Trim();
                string password = PasswordBox.Password;
                if ((!IsPlatformAccount && company.Length == 0) || email.Length == 0 || password.Length == 0)
                {
                    ShowLoginStatus("Bitte alle Pflichtfelder ausfüllen.");
                    return;
                }

                AngemeldeterBenutzer? user;
                try
                {
                    user = await AuthService.LoginAsync(company, email, password);
                }
                catch (Exception)
                {
                    // Eine unterbrochene Verbindung ist etwas anderes als falsche Zugangsdaten.
                    // Technische Details und Passwörter werden hier niemals angezeigt.
                    ShowLoginStatus("Die Anmeldung ist gerade nicht möglich. Bitte prüfen Sie die Verbindung und versuchen Sie es erneut.");
                    return;
                }

                if (user == null)
                {
                    // Die Meldung verrät nicht, ob ein bestimmtes Konto existiert.
                    ShowLoginStatus("Die Zugangsdaten oder der gewählte Kontotyp sind ungültig.");
                    return;
                }

                if (!user.IstMegakrasserAdmin && !user.HatFirmenArbeitsbereich)
                {
                    // Passwort stimmt, aber für den PC fehlt eine passende Rolle. Es wird keine Sitzung gestartet.
                    ShowLoginStatus(user.FirmenRollen.Contains(FirmenRolle.Fahrer)
                        ? "Für Fahrer gibt es am PC keinen Arbeitsbereich. Bitte verwenden Sie die Fahrer-App."
                        : "Ihrem Konto ist noch keine Rolle zugewiesen. Bitte wenden Sie sich an Ihren Kundenadmin.");
                    return;
                }

                try
                {
                    // Plattformadministratoren öffnen die globale Kundenverwaltung, Firmenkonten
                    // ihre Firmenansicht. Beide Seiten prüfen den Zugriff beim Öffnen erneut.
                    BenutzerSitzung.Starten(user);
                    var accent = ColorCombo.SelectedItem as ComboBoxItem;
                    var accentHex = accent?.Tag as string ?? "#FF004D";
                    var appearance = new DashboardAppearance { AccentHex = accentHex, IsLight = ModeSwitch.IsOn };
                    Type target = user.IstMegakrasserAdmin ? typeof(DashboardAnsicht) : typeof(FirmenAnsicht);

                    if (Frame is not Frame frame || !frame.Navigate(target, appearance))
                        throw new InvalidOperationException();
                    // BackStack löschen, damit Zurück nicht zur LoginPage führt
                    frame.BackStack.Clear();
                }
                catch (Exception)
                {
                    BenutzerSitzung.Abmelden();
                    ShowLoginStatus("Die Ansicht konnte nicht geöffnet werden. Bitte melden Sie sich erneut an.");
                }
            }
            finally
            {
                // Das Passwort wird nur für diesen Versuch gelesen und nie als Einstellung
                // gespeichert. Nach Erfolg oder Fehler löschen wir das sichtbare Eingabefeld.
                PasswordBox.Password = "";
                _isSigningIn = false;
                LoginButton.IsEnabled = AccountTypeCombo.IsEnabled = CompanyBox.IsEnabled =
                    EmailBox.IsEnabled = PasswordBox.IsEnabled = true;
                LoginButton.Content = "Anmelden";
            }
        }

      

        private void PasswortVergessen_Click(object sender, RoutedEventArgs e)
            {
                Frame.Navigate(typeof(passwortVergessen));
            }

}

    // Übergabe beim Abmelden: Farbe/Thema bleiben erhalten, der Hinweis enthält keine Kontodaten.
    internal sealed record LoginNavigationParameter(DashboardAppearance Appearance, string Hinweis);
}


       