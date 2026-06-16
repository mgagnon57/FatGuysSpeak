namespace FatGuysSpeak.ClientInstaller;

public class InstallConfig
{
    public string InstallPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "FatGuysSpeak", "Client");
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;
    public string? TempClientPath { get; set; }
}
