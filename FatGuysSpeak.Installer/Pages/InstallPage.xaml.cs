using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FatGuysSpeak.Installer.Pages;

public partial class InstallPage : UserControl, IWizardPage
{
    private readonly InstallConfig _config;
    private string _dashboardUrl = "";

    public event Action? InstallComplete;

    public InstallPage(InstallConfig config)
    {
        _config = config;
        InitializeComponent();
    }

    public void OnActivated()
    {
        _ = RunInstallAsync();
    }

    public bool CanAdvance() => false;

    private async Task RunInstallAsync()
    {
        try
        {
            await Step("Creating install directory...", 10, () =>
                Directory.CreateDirectory(_config.InstallPath));

            await Step("Copying server files...", 30, () =>
                CopyServerFiles());

            await Step("Writing configuration...", 55, () =>
                WriteConfig());

            if (_config.AddFirewallRule)
                await Step("Adding firewall rule...", 70, () =>
                    AddFirewallRule());

            if (_config.InstallAsService)
                await Step("Registering Windows Service...", 85, () =>
                    InstallService());

            await Step("Done.", 100, () => { });

            ShowComplete();
        }
        catch (Exception ex)
        {
            Progress.Value = 0;
            StatusText.Text = "Installation failed.";
            ErrorText.Text = ex.Message;
            ErrorBorder.Visibility = Visibility.Visible;
            CompleteView.Visibility = Visibility.Visible;
            InstallingView.Visibility = Visibility.Collapsed;
        }
    }

    private async Task Step(string message, int progress, Action work)
    {
        StatusText.Text = message;
        await Task.Run(work);
        Progress.Value = progress;
        await Task.Delay(120);
    }

    private void CopyServerFiles()
    {
        // Prefer pre-extracted temp path (set by ExtractionWindow at startup)
        var sourceDir = _config.TempServerPath
            ?? Path.Combine(AppContext.BaseDirectory, "server");

        if (!Directory.Exists(sourceDir))
            throw new InvalidOperationException(
                "This is a demo build — server files are not bundled.\n\n" +
                "Download the full installer from github.com/mgagnon57/FatGuysSpeak/releases");

        foreach (var srcFile in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, srcFile);
            var dest = Path.Combine(_config.InstallPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(srcFile, dest, overwrite: true);
        }
    }

    private void WriteConfig()
    {
        var jwtKey = GenerateSecret(64);
        var dbPath = Path.Combine(_config.InstallPath, "fatguys.db").Replace("\\", "\\\\");

        var json = $$"""
            {
              "Urls": "http://0.0.0.0:{{_config.Port}}",
              "Jwt": {
                "Key": "{{jwtKey}}",
                "Issuer": "FatGuysSpeak",
                "Audience": "FatGuysSpeak"
              },
              "Dashboard": {
                "Username": "{{JsonEscape(_config.AdminUsername)}}",
                "Password": "{{JsonEscape(_config.AdminPassword)}}"
              },
              "ConnectionStrings": {
                "Default": "Data Source={{dbPath}}"
              }
            }
            """;

        File.WriteAllText(
            Path.Combine(_config.InstallPath, "appsettings.Production.json"),
            json,
            Encoding.UTF8);
    }

    private static string GenerateSecret(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*";
        var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        var bytes = new byte[length];
        rng.GetBytes(bytes);
        var sb = new StringBuilder(length);
        foreach (var b in bytes)
            sb.Append(chars[b % chars.Length]);
        return sb.ToString();
    }

    private static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private void AddFirewallRule()
    {
        var args = $"advfirewall firewall add rule " +
                   $"name=\"FatGuysSpeak Server\" " +
                   $"dir=in action=allow protocol=TCP " +
                   $"localport={_config.Port} " +
                   $"description=\"FatGuysSpeak Server inbound\"";
        RunProcess("netsh", args);
    }

    private void InstallService()
    {
        var exe = Path.Combine(_config.InstallPath, "FatGuysSpeak.Server.exe");
        RunProcess("sc", $"create FatGuysSpeak binPath= \"{exe}\" start= auto DisplayName= \"FatGuysSpeak Server\"");
        RunProcess("sc", "start FatGuysSpeak");
    }

    private static void RunProcess(string fileName, string args)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(10_000);
    }

    private void ShowComplete()
    {
        var port = _config.Port;
        var lan = $"http://{_config.LocalIp}:{port}";
        // Land on the login form: hitting /dashboard with no session returns a raw 401 (the request
        // routes to the JWT challenge), whereas /dashboard/login serves the cookie sign-in page.
        _dashboardUrl = $"{lan}/dashboard/login";

        TxtDashboardUrl.Text = _dashboardUrl;
        TxtLanUrl.Text = lan;

        if (!string.IsNullOrWhiteSpace(_config.PublicIp))
            TxtPublicUrl.Text = $"http://{_config.PublicIp}:{port}";
        else
            PublicRow.Visibility = Visibility.Collapsed;

        LaunchNote.Text = _config.InstallAsService
            ? "The server is now running as a Windows Service and will start automatically with Windows."
            : $"To start the server, run FatGuysSpeak.Server.exe from:\n{_config.InstallPath}";

        InstallingView.Visibility = Visibility.Collapsed;
        CompleteView.Visibility = Visibility.Visible;
        InstallComplete?.Invoke();
    }

    private void DashUrl_Click(object sender, MouseButtonEventArgs e)
    {
        if (!string.IsNullOrEmpty(_dashboardUrl))
            Process.Start(new ProcessStartInfo(_dashboardUrl) { UseShellExecute = true });
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (!_config.InstallAsService)
        {
            var exe = Path.Combine(_config.InstallPath, "FatGuysSpeak.Server.exe");
            if (File.Exists(exe))
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }

        if (!string.IsNullOrEmpty(_dashboardUrl))
            Process.Start(new ProcessStartInfo(_dashboardUrl) { UseShellExecute = true });
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Window.GetWindow(this)?.Close();
    }
}
