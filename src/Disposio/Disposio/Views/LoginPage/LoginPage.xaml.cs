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
using Disposio.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Disposio.Views
{
    public sealed partial class LoginPage : Page
    {
        public LoginPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is DashboardAppearance appearance)
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
            var company = (this.FindName("CompanyBox") as TextBox)?.Text?.Trim();
            var email = (this.FindName("EmailBox") as TextBox)?.Text?.Trim();
            var pwd = (this.FindName("PasswordBox") as PasswordBox)?.Password;

            if (string.IsNullOrEmpty(company) || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(pwd))
            {
                var dlg = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = this.RequestedTheme,
                    Title = "Fehler",
                    Content = "Bitte alle Pflichtfelder ausfüllen.",
                    CloseButtonText = "OK"
                };
                await dlg.ShowAsync();
                return;
            }

<<<<<<< HEAD
            AngemeldeterBenutzer? user;
            try { user = await AuthService.LoginAsync(company, email, pwd); }
            catch { user = null; }

            var resultDlg = new ContentDialog
            {
                Title = user is null ? "Fehler" : "Erfolgreich",
                Content = user is null ? "Firma, E-Mail oder Passwort falsch." : $"Willkommen, {user.Vorname}!",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await resultDlg.ShowAsync();
=======
            // The existing demo login opens the customer dashboard.
            // A backend authentication service can be connected here later.
            var appearance = new DashboardAppearance
            {
                AccentHex = (ColorCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "#FF004D",
                IsLight = ModeSwitch.IsOn
            };
            Frame?.Navigate(typeof(DashboardAnsicht), appearance);
            Frame?.BackStack.Clear();
>>>>>>> 9ef0d635b931af1ca0360d3721c7aaaabd549de3
        }
    }
}
