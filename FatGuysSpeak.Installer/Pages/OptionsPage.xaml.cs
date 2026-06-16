using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FatGuysSpeak.Installer.Pages;

public partial class OptionsPage : UserControl, IWizardPage
{
    private readonly InstallConfig _config;

    public OptionsPage(InstallConfig config)
    {
        _config = config;
        InitializeComponent();
    }

    public void OnActivated()
    {
        TxtPath.Text = _config.InstallPath;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Navigate to the install folder, then click Save",
            FileName = "Select This Folder",
            Filter = "Folder|*.InstallHere",
            CheckFileExists = false,
            CheckPathExists = false,
            InitialDirectory = _config.InstallPath,
        };
        if (dlg.ShowDialog() == true)
        {
            var folder = Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrEmpty(folder))
            {
                TxtPath.Text = folder;
                _config.InstallPath = folder;
            }
        }
    }

    private void Standalone_Click(object sender, MouseButtonEventArgs e)
    {
        RadioStandalone.IsChecked = true;
    }

    private void Service_Click(object sender, MouseButtonEventArgs e)
    {
        RadioService.IsChecked = true;
    }

    private void RadioStandalone_Checked(object sender, RoutedEventArgs e)
    {
        StandaloneBorder.Color = Color.FromRgb(0xf0, 0x40, 0x10);
        ServiceBorder.Color    = Color.FromRgb(0x25, 0x25, 0x25);
        _config.InstallAsService = false;
    }

    private void RadioService_Checked(object sender, RoutedEventArgs e)
    {
        StandaloneBorder.Color = Color.FromRgb(0x25, 0x25, 0x25);
        ServiceBorder.Color    = Color.FromRgb(0xf0, 0x40, 0x10);
        _config.InstallAsService = true;
    }

    public bool CanAdvance()
    {
        var path = TxtPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowError("Installation path cannot be empty.");
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
                throw new InvalidOperationException();
        }
        catch
        {
            ShowError("Invalid installation path.");
            return false;
        }

        ErrorBorder.Visibility = Visibility.Collapsed;
        _config.InstallPath = path;
        _config.InstallAsService = RadioService.IsChecked == true;
        return true;
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorBorder.Visibility = Visibility.Visible;
    }
}
