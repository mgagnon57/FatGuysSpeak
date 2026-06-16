using System.Windows;
using System.Windows.Controls;

namespace FatGuysSpeak.Installer.Pages;

public partial class CredentialsPage : UserControl, IWizardPage
{
    private readonly InstallConfig _config;

    public CredentialsPage(InstallConfig config)
    {
        _config = config;
        InitializeComponent();
    }

    public void OnActivated() { }

    public bool CanAdvance()
    {
        var username = TxtUsername.Text.Trim();
        var password = PwdPassword.Password;
        var confirm  = PwdConfirm.Password;

        if (string.IsNullOrWhiteSpace(username))
            return ShowError("Username cannot be empty.");

        if (password.Length < 6)
            return ShowError("Password must be at least 6 characters.");

        if (password != confirm)
            return ShowError("Passwords do not match.");

        ErrorBorder.Visibility = Visibility.Collapsed;
        _config.AdminUsername = username;
        _config.AdminPassword = password;
        return true;
    }

    private bool ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBorder.Visibility = Visibility.Visible;
        return false;
    }

    private void Pwd_Changed(object sender, RoutedEventArgs e)
    {
        ErrorBorder.Visibility = Visibility.Collapsed;
    }
}
