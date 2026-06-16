using System.Reflection;
using System.Windows;

namespace FatGuysSpeak.Installer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = new InstallConfig();

        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("server-bundle.zip"));

        if (resource != null)
        {
            var extractWin = new ExtractionWindow(config, resource);
            extractWin.ShowDialog();

            if (config.TempServerPath == null)
            {
                Shutdown();
                return;
            }
        }

        var mainWin = new MainWindow(config);
        mainWin.Closed += (_, _) =>
        {
            TryCleanupTemp(config.TempServerPath);
            Shutdown();
        };
        mainWin.Show();
    }

    private static void TryCleanupTemp(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { System.IO.Directory.Delete(path, recursive: true); } catch { }
    }
}
