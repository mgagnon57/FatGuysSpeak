using System.Diagnostics;
using FatGuysSpeak.Shared;
using Velopack;
using Velopack.Sources;

namespace FatGuysSpeak.Client;

public record HandoverResult(bool Success, string? Error);

/// <summary>0.13.2 bridge: FatGuysSpeak is now Squelch. Downloads the Squelch setup from the
/// pinned v0.14.0 release (NOT /latest — this bridge release is published after 0.14.0 and would
/// itself become "latest"), installs it silently, opens Squelch, and starts the Velopack uninstall
/// of FatGuysSpeak. If Squelch is already installed (an earlier handover whose uninstall didn't
/// happen, or a hand install) it is only opened — never reinstalled over a newer Squelch.
/// Settings are NOT touched: Squelch imports them on its own first launch from the old MAUI
/// settings folder, which Velopack's uninstall (install root only) leaves alone.</summary>
public static class SquelchHandover
{
    public const string SetupUrl = "https://github.com/mgagnon57/Squelch/releases/download/v0.14.0/Squelch.Client-win-Setup.exe";

    /// <summary>Overall deadline for the whole handover (the page's CancellationTokenSource).</summary>
    public static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The single place the setup location is read.</summary>
    internal static string ResolveSetupUrl() => SetupUrl;

    /// <summary>True only for a Velopack-installed client; dev/unpackaged runs skip the handover.</summary>
    public static bool ShouldRun()
    {
        try { return new UpdateManager(new GithubSource("https://github.com/mgagnon57/FatGuysSpeak", null, false)).IsInstalled; }
        catch { return false; }
    }

    /// <summary>Install (if needed) and open Squelch, then start the FatGuysSpeak uninstall.
    /// Success means Squelch is open and the uninstall has started — the caller just quits.
    /// Never throws; cancellation/timeouts come back as a failure result.</summary>
    public static async Task<HandoverResult> RunAsync(IProgress<string> status, CancellationToken ct)
    {
        void Step(string text) { Log(text); status.Report(text); }
        HandoverResult Fail(string error) { Log("FAILED: " + error); return new HandoverResult(false, error); }

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var squelchExe = SquelchHandoverPlan.SquelchExePath(localAppData);
            var setupPath = Path.Combine(Path.GetTempPath(), SquelchHandoverPlan.SetupFileName);
            Log($"Handover start (app dir {AppContext.BaseDirectory})");

            if (SquelchHandoverPlan.Decide(File.Exists(squelchExe)) == HandoverStart.DownloadAndInstall)
            {
                // 1. Download
                Step("Downloading Squelch…");
                try { await DownloadAsync(ResolveSetupUrl(), setupPath, status, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                { return Fail("Moving to Squelch took too long and was stopped."); }
                catch (OperationCanceledException)
                { return Fail("Couldn't download Squelch: the download stalled."); }
                catch (Exception ex)
                { return Fail($"Couldn't download Squelch: {ex.Message}"); }

                // 2. Silent install with a deadline. Velopack's Setup --silent does NOT start the app.
                Step("Installing Squelch…");
                try
                {
                    using var setup = Process.Start(new ProcessStartInfo(setupPath, "--silent")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetTempPath(),
                    }) ?? throw new InvalidOperationException("the installer didn't start");

                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(SetupTimeout);
                    try { await setup.WaitForExitAsync(deadline.Token); }
                    catch (OperationCanceledException)
                    {
                        try { setup.Kill(entireProcessTree: true); } catch { }
                        return Fail(ct.IsCancellationRequested
                            ? "Moving to Squelch took too long and was stopped."
                            : "The Squelch installer didn't finish in time and was stopped.");
                    }
                    try { File.Delete(setupPath); } catch { }   // ~95 MB; best-effort cleanup

                    Log($"Setup exited with code {setup.ExitCode}");
                    var failure = SquelchHandoverPlan.SetupFailure(setup.ExitCode, File.Exists(squelchExe));
                    if (failure is not null) return Fail(failure);
                }
                catch (Exception ex)
                {
                    return Fail($"Couldn't install Squelch: {ex.Message}");
                }
            }
            else
            {
                Log("Squelch already installed; skipping download and install");
            }

            // 3. Open Squelch
            Step("Opening Squelch…");
            try
            {
                Process.Start(new ProcessStartInfo(squelchExe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(squelchExe)!,
                });
            }
            catch (Exception ex)
            {
                return Fail($"Squelch is installed, but couldn't be opened: {ex.Message}");
            }

            // 4. Remove FatGuysSpeak (detached; Update.exe stops this process itself)
            Step("Removing FatGuysSpeak…");
            if (!StartUninstall())
                return Fail("Squelch is installed, but FatGuysSpeak couldn't remove itself. "
                          + "You can uninstall FatGuysSpeak from Windows Settings → Apps.");

            Log("Uninstall started; quitting");
            return new HandoverResult(true, null);
        }
        catch (Exception ex)
        {
            return Fail($"Something went wrong moving you to Squelch: {ex.Message}");
        }
    }

    /// <summary>Start FatGuysSpeak's Velopack uninstall, detached (not awaited). Update.exe stops
    /// this app's process itself, removes the install root, shortcuts and the uninstall entry.</summary>
    private static bool StartUninstall()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var updateExe = SquelchHandoverPlan.UpdateExePath(AppContext.BaseDirectory, localAppData);
            if (!File.Exists(updateExe)) { Log($"Update.exe not found at {updateExe}"); return false; }
            Process.Start(new ProcessStartInfo(updateExe, "uninstall --silent")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetTempPath(),   // not inside the folder being deleted
            });
            return true;
        }
        catch (Exception ex) { Log($"Uninstall start failed: {ex.Message}"); return false; }
    }

    /// <summary>One line per step/failure to %TEMP%\FatGuysSpeak-handover.log. Best-effort.</summary>
    private static void Log(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "FatGuysSpeak-handover.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { }
    }

    private static async Task DownloadAsync(string url, string dest, IProgress<string> status, CancellationToken ct)
    {
        // A local path or file:// URL (rehearsal builds only — the committed source is always https).
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            File.Copy(uri.LocalPath, dest, overwrite: true);
            return;
        }

        // Stall watchdog: cancels if no response headers / no bytes arrive for 60 s; reset per chunk.
        // The overall deadline comes in through ct (linked).
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(DownloadStallTimeout);

        using var handler = new HttpClientHandler { AllowAutoRedirect = true };   // GitHub release assets redirect
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };   // deadlines via tokens
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FatGuysSpeak-Client/0.13.2");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;

        await using (var src = await resp.Content.ReadAsStreamAsync(stall.Token))
        await using (var file = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long read = 0;
            int lastPct = -1, n;
            while ((n = await src.ReadAsync(buffer, stall.Token)) > 0)
            {
                stall.CancelAfter(DownloadStallTimeout);
                await file.WriteAsync(buffer.AsMemory(0, n), stall.Token);
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
