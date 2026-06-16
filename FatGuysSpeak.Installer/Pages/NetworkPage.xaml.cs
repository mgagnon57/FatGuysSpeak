using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FatGuysSpeak.Installer.Pages;

public partial class NetworkPage : UserControl, IWizardPage
{
    private readonly InstallConfig _config;
    private bool _portVerified;

    public NetworkPage(InstallConfig config)
    {
        _config = config;
        InitializeComponent();
    }

    public void OnActivated()
    {
        _ = DetectIpsAsync();
        UpdateFirewallLabel();
    }

    private async Task DetectIpsAsync()
    {
        TxtLocalIp.Text = DetectLocalIp();
        TxtPublicIp.Text = "Detecting...";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var pub = (await http.GetStringAsync("https://api.ipify.org")).Trim();
            TxtPublicIp.Text = pub;
            _config.PublicIp = pub;
        }
        catch
        {
            TxtPublicIp.Text = "Unable to detect — check internet connection";
        }
    }

    private string DetectLocalIp()
    {
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ip = addr.Address.ToString();
                        _config.LocalIp = ip;
                        return ip;
                    }
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }

    private void TestPort_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtPort.Text.Trim(), out var port) || port is < 1024 or > 65535)
        {
            SetPortStatus("Invalid port number", isOk: false);
            return;
        }

        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            SetPortStatus($"Port {port} is available", isOk: true);
            _portVerified = true;
            _config.Port = port;
            UpdateFirewallLabel();
        }
        catch
        {
            SetPortStatus($"Port {port} is in use — choose another", isOk: false);
            _portVerified = false;
        }
    }

    private void SetPortStatus(string text, bool isOk)
    {
        PortStatus.Text = (isOk ? "✓ " : "✗ ") + text;
        PortStatus.Foreground = isOk
            ? new SolidColorBrush(Color.FromRgb(0x36, 0xb8, 0x64))
            : new SolidColorBrush(Color.FromRgb(0xe0, 0x55, 0x55));
    }

    private void UpdateFirewallLabel()
    {
        FirewallPortRun.Text = TxtPort.Text.Trim();
    }

    private void Chk_Changed(object sender, RoutedEventArgs e)
    {
        _config.AddFirewallRule = ChkFirewall.IsChecked == true;
    }

    public bool CanAdvance()
    {
        if (!int.TryParse(TxtPort.Text.Trim(), out var port) || port is < 1024 or > 65535)
        {
            ShowError("Enter a valid port number between 1024 and 65535.");
            return false;
        }

        if (!_portVerified)
        {
            ShowError("Click \"Test\" to verify the port is available before continuing.");
            return false;
        }

        ErrorBorder.Visibility = Visibility.Collapsed;
        _config.Port = port;
        _config.AddFirewallRule = ChkFirewall.IsChecked == true;
        return true;
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorBorder.Visibility = Visibility.Visible;
    }
}
