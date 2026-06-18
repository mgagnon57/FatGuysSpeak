#if WINDOWS
using FatGuysSpeak.Shared;
using Velopack;
using Velopack.Sources;

namespace FatGuysSpeak.Client.Services;

public enum UpdateSyncResult { UpToDate, Applying, Unavailable }

/// <summary>Pins the client to the connected server's exact version via Velopack
/// (upgrade or downgrade). Best-effort: failures return Unavailable, never throw.</summary>
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

    public async Task<UpdateSyncResult> SyncToServerVersionAsync(string serverVersion)
    {
        try
        {
            var channel = UpdateChannel.ForVersion(serverVersion);
            if (channel is null) return UpdateSyncResult.Unavailable;

            var mgr = new UpdateManager(
                new GithubSource(RepoUrl, null, false),
                new UpdateOptions { ExplicitChannel = channel, AllowVersionDowngrade = true });

            if (!mgr.IsInstalled) return UpdateSyncResult.UpToDate;   // dev / not Velopack-installed

            var info = await mgr.CheckForUpdatesAsync();
            if (info is null) return UpdateSyncResult.UpToDate;        // already matches the server

            await mgr.DownloadUpdatesAsync(info);
            mgr.ApplyUpdatesAndRestart(info.TargetFullRelease);        // exits + relaunches at server version
            return UpdateSyncResult.Applying;
        }
        catch { return UpdateSyncResult.Unavailable; }
    }
}
#else
namespace FatGuysSpeak.Client.Services;

public enum UpdateSyncResult { UpToDate, Applying, Unavailable }

public sealed class UpdateService
{
    public string? InstalledVersion => null;
    public Task<UpdateSyncResult> SyncToServerVersionAsync(string serverVersion)
        => Task.FromResult(UpdateSyncResult.UpToDate);
}
#endif
