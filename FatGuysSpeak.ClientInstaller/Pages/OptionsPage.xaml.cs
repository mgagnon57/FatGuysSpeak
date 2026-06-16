using System.IO;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace FatGuysSpeak.ClientInstaller.Pages;

public partial class OptionsPage : UserControl, IWizardPage
{
    private readonly InstallConfig _config;

    public string Title => "Install Options";
    public bool CanAdvance => !string.IsNullOrWhiteSpace(InstallPathBox?.Text);
    public bool IsTerminal => false;
    public event EventHandler<bool>? AdvanceChanged;

    public OptionsPage(InstallConfig config)
    {
        _config = config;
        InitializeComponent();
    }

    public void OnActivated()
    {
        InstallPathBox.Text = _config.InstallPath;
        DesktopShortcutCheck.IsChecked = _config.CreateDesktopShortcut;
        StartMenuCheck.IsChecked = _config.CreateStartMenuShortcut;
        UpdatePathNote();
    }

    private void InstallPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _config.InstallPath = InstallPathBox.Text.Trim();
        UpdatePathNote();
        AdvanceChanged?.Invoke(this, CanAdvance);
    }

    private void Shortcut_Changed(object sender, RoutedEventArgs e)
    {
        if (DesktopShortcutCheck == null || StartMenuCheck == null) return;
        _config.CreateDesktopShortcut   = DesktopShortcutCheck.IsChecked == true;
        _config.CreateStartMenuShortcut = StartMenuCheck.IsChecked == true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description        = "Select installation folder",
            SelectedPath       = InstallPathBox.Text,
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            InstallPathBox.Text = dlg.SelectedPath;
    }

    private void UpdatePathNote()
    {
        if (PathNote == null) return;
        try
        {
            var root = Path.GetPathRoot(_config.InstallPath);
            var drive = new DriveInfo(root ?? "C:\\");
            long freeMb = drive.AvailableFreeSpace / 1024 / 1024;
            PathNote.Text = $"{freeMb:N0} MB free on {root}";
        }
        catch
        {
            PathNote.Text = string.Empty;
        }
    }
}
