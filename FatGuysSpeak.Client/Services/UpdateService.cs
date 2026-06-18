#if WINDOWS
using FatGuysSpeak.Shared;
using Velopack;
using Velopack.Sources;

namespace FatGuysSpeak.Client.Services;

public enum UpdateSyncOutcome { Compatible, Prepared, Unavailable }

/// <summary>Pins the client to the connected server's exact version via Velopack
/// (upgrade or downgrade). PrepareAsync downloads but never restarts; ApplyAndRestart
/// performs the swap+relaunch. Best-effort: failures return Unavailable, never throw.</summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/mgagnon57/FatGuysSpeak";

    /// <summary>The version Velopack reports as installed, or null in dev/unpackaged debug.</summary>
    private string? _installedVersion;
    private bool _installedVersionChecked;
    public string? InstalledVersion
    {
        get
        {
            if (_installedVersionChecked) return _installedVersion;
            _installedVersionChecked = true;
            try
            {
                var m = new UpdateManager(new GithubSource(RepoUrl, null, false));
                _installedVersion = m.IsInstalled ? m.CurrentVersion?.ToString() : null;
            }
            catch { _installedVersion = null; }
            return _installedVersion;
        }
    }

    private UpdateManager? _pendingMgr;
    private UpdateInfo? _pendingInfo;

    /// <summary>Resolve + download (with progress) the build matching the server's exact
    /// version. Does NOT restart. Caller invokes ApplyAndRestart on Prepared.</summary>
    public async Task<UpdateSyncOutcome> PrepareAsync(string serverVersion, IProgress<int>? downloadProgress = null)
    {
        try
        {
            var channel = UpdateChannel.ForVersion(serverVersion);
            if (channel is null) return UpdateSyncOutcome.Unavailable;

            var mgr = new UpdateManager(
                new GithubSource(RepoUrl, null, false),
                new UpdateOptions { ExplicitChannel = channel, AllowVersionDowngrade = true });

            if (!mgr.IsInstalled) return UpdateSyncOutcome.Compatible;  // dev / not Velopack-installed

            var info = await mgr.CheckForUpdatesAsync();
            if (info is null) return UpdateSyncOutcome.Unavailable;      // no published build for that version

            await mgr.DownloadUpdatesAsync(info, p => downloadProgress?.Report(p));

            _pendingMgr = mgr;
            _pendingInfo = info;
            return UpdateSyncOutcome.Prepared;
        }
        catch { return UpdateSyncOutcome.Unavailable; }
    }

    /// <summary>Apply the build downloaded by PrepareAsync and relaunch. Exits the process.</summary>
    public void ApplyAndRestart()
    {
        if (_pendingMgr is null || _pendingInfo is null) return;
        _pendingMgr.ApplyUpdatesAndRestart(_pendingInfo.TargetFullRelease);
    }
}
#else
namespace FatGuysSpeak.Client.Services;

public enum UpdateSyncOutcome { Compatible, Prepared, Unavailable }

public sealed class UpdateService
{
    public string? InstalledVersion => null;
    public Task<UpdateSyncOutcome> PrepareAsync(string serverVersion, IProgress<int>? downloadProgress = null)
        => Task.FromResult(UpdateSyncOutcome.Compatible);
    public void ApplyAndRestart() { }
}
#endif
