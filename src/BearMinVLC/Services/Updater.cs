using Velopack;
using Velopack.Sources;

namespace BearMinVLC.Services;

/// <summary>
/// Checks GitHub Releases for a newer version, downloads it in the background, and applies it when the
/// app closes (or right away on "Restart now"). Does nothing when running from a build folder.
/// </summary>
public sealed class Updater
{
    public const string RepoUrl = "https://github.com/bearminimumdev/BearMinVLC";

    readonly UpdateManager? _manager;
    VelopackAsset? _ready;

    public Updater()
    {
        try { _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false)); }
        catch (Exception) { _manager = null; }
    }

    public bool IsInstalled => _manager?.IsInstalled == true;

    /// <summary>Returns the new version once it's downloaded and ready, or null if there's nothing to do.</summary>
    public async Task<string?> CheckAndDownloadAsync()
    {
        if (_manager is not { IsInstalled: true }) return null;
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info == null) return null;
            await _manager.DownloadUpdatesAsync(info);
            _ready = info.TargetFullRelease;
            return _ready.Version.ToString();
        }
        catch (Exception)
        {
            return null; // offline, GitHub unreachable or rate-limited: try again next launch
        }
    }

    /// <summary>Hands the downloaded update to Velopack's updater, which installs it after this process exits.</summary>
    public void ApplyOnExit()
    {
        if (_manager == null || _ready == null) return;
        try { _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: false); }
        catch (Exception) { }
    }

    public void RestartNow()
    {
        if (_manager == null || _ready == null) return;
        _manager.ApplyUpdatesAndRestart(_ready);
    }
}
