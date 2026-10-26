using System.Diagnostics;
using FatGuysSpeak.Shared;
using Velopack;
using Velopack.Sources;

namespace FatGuysSpeak.Client;

public record HandoverResult(bool Success, string? Error);

/// <summary>0.13.2 bridge: FatGuysSpeak is now Squelch. Downloads the Squelch setup from the
/// pinned v0.14.0 release (NOT /latest — this bridge release is published after 0.14.0 and would
/// itself become "latest"), installs it silently, opens Squelch, and starts the Velopack uninstall
/// of FatGuysSpeak. Settings are NOT touched: Squelch imports them on its own first launch from the
/// old MAUI settings folder, which Velopack's uninstall (install root only) leaves alone.</summary>
public static class SquelchHandover
{
    public const string SetupUrl = "https://github.com/mgagnon57/Squelch/releases/download/v0.14.0/Squelch.Client-win-Setup.exe";

    /// <summary>The single place the setup location is read.</summary>
    internal static string ResolveSetupUrl() => SetupUrl;

    /// <summary>True only for a Velopack-installed client; dev/unpackaged runs skip the handover.</summary>
    public static bool ShouldRun()
    {
        try { return new UpdateManager(new GithubSource("https://github.com/mgagnon57/FatGuysSpeak", null, false)).IsInstalled; }
        catch { return false; }
    }

    /// <summary>Download, silently install and open Squelch. On success the caller starts the
    /// uninstall via <see cref="StartUninstall"/> and quits.</summary>
    public static async Task<HandoverResult> RunAsync(IProgress<string> status, CancellationToken ct)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var setupPath = Path.Combine(Path.GetTempPath(), SquelchHandoverPlan.SetupFileName);

        // 1. Download
        try
        {
            status.Report("Downloading Squelch…");
            await DownloadAsync(ResolveSetupUrl(), setupPath, status, ct);
        }
        catch (Exception ex)
        {
            return new HandoverResult(false, $"Couldn't download Squelch: {ex.Message}");
        }

        // 2. Silent install, wait for it. Velopack's Setup --silent does NOT start the app.
        status.Report("Installing Squelch…");
        try
        {
            using var setup = Process.Start(new ProcessStartInfo(setupPath, "--silent")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            }) ?? throw new InvalidOperationException("the installer didn't start");
            await setup.WaitForExitAsync(ct);
            try { File.Delete(setupPath); } catch { }   // ~95 MB; best-effort cleanup

            var squelchExe = SquelchHandoverPlan.SquelchExePath(localAppData);
            var failure = SquelchHandoverPlan.SetupFailure(setup.ExitCode, File.Exists(squelchExe));
            if (failure is not null) return new HandoverResult(false, failure);

            // 3. Open Squelch
            status.Report("Opening Squelch…");
            Process.Start(new ProcessStartInfo(squelchExe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(squelchExe)!,
            });
        }
        catch (Exception ex)
        {
            return new HandoverResult(false, $"Couldn't install Squelch: {ex.Message}");
        }

        return new HandoverResult(true, null);
    }

    /// <summary>Start FatGuysSpeak's Velopack uninstall, detached (not awaited). Update.exe stops
    /// this app's process itself, removes the install root, shortcuts and the uninstall entry.</summary>
    public static bool StartUninstall()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var updateExe = SquelchHandoverPlan.UpdateExePath(AppContext.BaseDirectory, localAppData);
            if (!File.Exists(updateExe)) return false;
            Process.Start(new ProcessStartInfo(updateExe, "uninstall --silent")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetTempPath(),   // not inside the folder being deleted
            });
            return true;
        }
        catch { return false; }
    }

    private static async Task DownloadAsync(string url, string dest, IProgress<string> status, CancellationToken ct)
    {
        // A local path or file:// URL (rehearsal builds only — the committed source is always https).
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            File.Copy(uri.LocalPath, dest, overwrite: true);
            return;
        }

        using var handler = new HttpClientHandler { AllowAutoRedirect = true };   // GitHub release assets redirect
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FatGuysSpeak-Client/0.13.2");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var file = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long read = 0;
            int lastPct = -1, n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total is > 0)
                {
                    var pct = (int)(read * 100 / total.Value);
                    if (pct != lastPct) { lastPct = pct; status.Report($"Downloading Squelch… {pct}%"); }
                }
            }
        }

        if (total is > 0 && new FileInfo(dest).Length != total.Value)
            throw new IOException("the download was incomplete");
    }
}
