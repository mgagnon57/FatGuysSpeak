namespace FatGuysSpeak.Installer;

public class InstallConfig
{
    public string AdminUsername { get; set; } = "admin";
    public string AdminPassword { get; set; } = "";
    public int Port { get; set; } = 5238;
    public bool AddFirewallRule { get; set; } = true;
    public bool InstallAsService { get; set; } = false;
    public string InstallPath { get; set; } = @"C:\Program Files\FatGuysSpeak\Server";
    public string LocalIp { get; set; } = "127.0.0.1";
    public string PublicIp { get; set; } = "";

    // Set by ExtractionWindow when server files are pre-extracted from the embedded resource.
    // CopyServerFiles reads from here instead of copying from the embedded ZIP again.
    public string? TempServerPath { get; set; }
}
