using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;

namespace FatGuysSpeak.Installer;

public partial class ExtractionWindow : Window
{
    private readonly InstallConfig _config;
    private readonly string _resourceName;

    public ExtractionWindow(InstallConfig config, string resourceName)
    {
        _config = config;
        _resourceName = resourceName;
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Run(Extract);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to extract installer files:\n\n{ex.Message}",
                "FatGuysSpeak Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
        }
    }

    private void Extract()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"FatGuysSpeak-Setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(_resourceName)!;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        int total = entries.Count;
        int done = 0;

        foreach (var entry in entries)
        {
            var dest = Path.Combine(tempDir, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
            done++;

            var pct = (double)done / total * 100;
            Dispatcher.Invoke(() =>
            {
                Progress.Value = pct;
                StatusText.Text = $"Extracting files... {(int)pct}%";
            });
        }

        _config.TempServerPath = tempDir;
    }
}
