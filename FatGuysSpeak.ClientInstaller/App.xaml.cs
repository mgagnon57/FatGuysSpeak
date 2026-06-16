using System.Reflection;
using System.Windows;
using Application = System.Windows.Application;

namespace FatGuysSpeak.ClientInstaller;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = new InstallConfig();
        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("client-bundle.zip"));

        if (resource != null)
        {
            var extractWin = new ExtractionWindow(config, resource);
            extractWin.ShowDialog();
            if (config.TempClientPath == null) { Shutdown(); return; }
        }

        var mainWin = new MainWindow(config);
        mainWin.Closed += (_, _) => { TryCleanupTemp(config.TempClientPath); Shutdown(); };
        mainWin.Show();
    }

    private static void TryCleanupTemp(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { System.IO.Directory.Delete(path, recursive: true); } catch { }
    }
}
