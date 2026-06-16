using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace FatGuysSpeak.ClientInstaller;

public partial class ExtractionWindow : Window
{
    private readonly InstallConfig _config;
    private readonly string _resourceName;

    public ExtractionWindow(InstallConfig config, string resourceName)
    {
        InitializeComponent();
        _config = config;
        _resourceName = resourceName;
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
            MessageBox.Show($"Extraction failed:\n\n{ex.Message}", "Setup Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
        }
    }

    private void Extract()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"FatGuysSpeak-Client-Setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(_resourceName)!;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var entries = zip.Entries;
        int total = entries.Count;
        int done = 0;

        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                done++;
                continue;
            }

            var destPath = Path.Combine(tempDir, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);

            done++;
            int pct = (int)((double)done / total * 100);
            Dispatcher.Invoke(() =>
            {
                Progress.Value = pct;
                StatusText.Text = $"Extracting files... {pct}%";
            });
        }

        _config.TempClientPath = tempDir;
    }
}
