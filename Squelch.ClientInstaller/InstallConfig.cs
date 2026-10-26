namespace Squelch.ClientInstaller;

public class InstallConfig
{
    public string InstallPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "Squelch", "Client");
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;
    public string? TempClientPath { get; set; }
}
