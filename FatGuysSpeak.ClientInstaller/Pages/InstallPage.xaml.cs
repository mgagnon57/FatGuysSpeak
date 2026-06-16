using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UserControl = System.Windows.Controls.UserControl;

namespace FatGuysSpeak.ClientInstaller.Pages;

public partial class InstallPage : UserControl, IWizardPage
{
    private readonly InstallConfig _config;
    private bool _done;
    private bool _failed;

    public string Title => _done ? "Installation Complete" : "Installing";
    public bool CanAdvance => _done || _failed;
    public bool IsTerminal => _done || _failed;
    public event EventHandler<bool>? AdvanceChanged;

    public InstallPage(InstallConfig config)
    {
        _config = config;
        InitializeComponent();
    }

    public void OnActivated()
    {
        if (_done || _failed) return;
        _ = RunInstallAsync();
    }

    private async Task RunInstallAsync()
    {
        try
        {
            await Task.Run(DoInstall);
            Dispatcher.Invoke(ShowSuccess);
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => ShowError(ex.Message));
        }
    }

    private void DoInstall()
    {
        Report("Copying files...", 10);
        CopyClientFiles();

        Report("Creating shortcuts...", 60);
        if (_config.CreateDesktopShortcut)
            CreateShortcut(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "FatGuysSpeak.lnk");

        if (_config.CreateStartMenuShortcut)
        {
            var startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "FatGuysSpeak");
            Directory.CreateDirectory(startMenu);
            CreateShortcut(startMenu, "FatGuysSpeak.lnk");
        }

        Report("Registering uninstaller...", 85);
        WriteUninstallEntry();

        Report("Done.", 100);
    }

    private void CopyClientFiles()
    {
        var sourceDir = _config.TempClientPath
            ?? Path.Combine(AppContext.BaseDirectory, "client");

        if (!Directory.Exists(sourceDir))
            throw new InvalidOperationException(
                "This is a demo build — client files are not bundled.\n\n" +
                "Download the full installer from github.com/mgagnon57/FatGuysSpeak/releases");

        Directory.CreateDirectory(_config.InstallPath);

        var srcFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        int total = srcFiles.Length;
        int done = 0;

        foreach (var srcFile in srcFiles)
        {
            var relative = Path.GetRelativePath(sourceDir, srcFile);
            var dest = Path.Combine(_config.InstallPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(srcFile, dest, overwrite: true);

            done++;
            int pct = 10 + (int)((double)done / total * 45);
            Report($"Copying {Path.GetFileName(srcFile)}...", pct);
        }
    }

    private void CreateShortcut(string directory, string filename)
    {
        var exePath = Path.Combine(_config.InstallPath, "FatGuysSpeak.Client.exe");
        if (!File.Exists(exePath)) return;

        var shell = new Type[] { Type.GetTypeFromProgID("WScript.Shell")! };
        dynamic wsh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic shortcut = wsh.CreateShortcut(Path.Combine(directory, filename));
        shortcut.TargetPath       = exePath;
        shortcut.WorkingDirectory = _config.InstallPath;
        shortcut.Description      = "FatGuysSpeak – self-hosted voice and chat";
        shortcut.Save();
    }

    private void WriteUninstallEntry()
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FatGuysSpeak.Client";
        var exePath = Path.Combine(_config.InstallPath, "FatGuysSpeak.Client.exe");

        using var reg = Registry.CurrentUser.CreateSubKey(key);
        reg.SetValue("DisplayName",        "FatGuysSpeak Client");
        reg.SetValue("UninstallString",    $"\"{exePath}\" --uninstall");
        reg.SetValue("InstallLocation",    _config.InstallPath);
        reg.SetValue("Publisher",          "FatGuysSpeak");
        reg.SetValue("DisplayVersion",     "1.0.0");
        reg.SetValue("NoModify",           1, RegistryValueKind.DWord);
        reg.SetValue("NoRepair",           1, RegistryValueKind.DWord);
    }

    private void Report(string message, int pct)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text       = message;
            InstallProgress.Value = pct;
            PercentText.Text      = $"{pct}%";
        });
    }

    private void ShowSuccess()
    {
        _done = true;
        HeadingText.Text       = "Installation Complete";
        ProgressPanel.Visibility = Visibility.Collapsed;
        SuccessPanel.Visibility  = Visibility.Visible;
        AdvanceChanged?.Invoke(this, true);

        if (LaunchCheck.IsChecked == true)
            TryLaunch();
    }

    private void ShowError(string message)
    {
        _failed = true;
        ErrorPanel.Visibility    = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text           = message;
        AdvanceChanged?.Invoke(this, true);
    }

    private void TryLaunch()
    {
        try
        {
            var exe = Path.Combine(_config.InstallPath, "FatGuysSpeak.Client.exe");
            if (File.Exists(exe))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch { }
    }
}
