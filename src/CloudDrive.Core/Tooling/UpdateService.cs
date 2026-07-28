using System.Reflection;
using System.Runtime.Versioning;
using CloudDrive.Core.Models;
using CloudDrive.Core.Stores;

namespace CloudDrive.Core.Tooling;

/// <summary>A CloudDrive release newer than what is running.</summary>
public sealed record AvailableUpdate(
    string Version,
    string? Name,
    string? ReleaseNotes,
    string? ReleaseUrl,
    string DownloadUrl,
    long SizeBytes,
    DateTime? PublishedUtc,
    bool IsPrerelease);

/// <summary>
/// Checks <c>RHC-Solutions/CloudDrive</c> for new releases and installs them when the machine is
/// quiet.
///
/// <para><b>"Push" is a poll, and the distinction is worth being honest about.</b> Nothing GitHub
/// offers reaches a machine behind NAT without that machine holding a connection open, so there is
/// no push transport here. What is delivered is the *effect* users mean by push: the update arrives
/// and applies without anyone asking. The feed is polled on a jittered interval and conditional
/// requests keep an unchanged feed almost free.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateService
{
    /// <summary>The repository releases are published to.</summary>
    public const string Repository = "RHC-Solutions/CloudDrive";

    /// <summary>Substrings identifying the installer asset in a release.</summary>
    private static readonly string[] InstallerAsset = ["CloudDrive", "Setup", ".exe"];

    private readonly GitHubReleases _releases;
    private readonly Action<string>? _log;

    public UpdateService(GitHubReleases? releases = null, Action<string>? log = null)
    {
        _releases = releases ?? new GitHubReleases();
        _log = log;
    }

    /// <summary>The running version, from the assembly's informational version.</summary>
    public static string CurrentVersion
    {
        get
        {
            var raw = Assembly.GetEntryAssembly()?
                          .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                      ?? "0.0.0";
            // The SDK appends "+<commit sha>" to the informational version; it is not part of the
            // version for comparison purposes.
            var plus = raw.IndexOf('+');
            return plus >= 0 ? raw[..plus] : raw;
        }
    }

    /// <summary>
    /// Looks for a newer release, or returns null when up to date.
    ///
    /// A version the user explicitly skipped is not offered again, and neither is one older than or
    /// equal to what is running — which also covers the case of someone installing a newer build by
    /// hand than the feed knows about.
    /// </summary>
    public async Task<AvailableUpdate?> CheckAsync(UpdateSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.CheckForUpdates) return null;

        var release = await _releases
            .LatestAsync(Repository, settings.IncludePrereleases, ct)
            .ConfigureAwait(false);
        if (release is null) return null;

        if (!ToolManager.IsNewer(release.Version, CurrentVersion)) return null;

        if (!string.IsNullOrWhiteSpace(settings.SkippedVersion)
            && !ToolManager.IsNewer(release.Version, settings.SkippedVersion!))
        {
            _log?.Invoke($"Release {release.Version} is available but was skipped.");
            return null;
        }

        var asset = release.Assets.FirstOrDefault(a =>
            InstallerAsset.All(s => a.Name.Contains(s, StringComparison.OrdinalIgnoreCase)));
        if (asset is null)
        {
            _log?.Invoke($"Release {release.Version} has no installer asset; ignoring it.");
            return null;
        }

        return new AvailableUpdate(
            release.Version, release.Name, release.Body, release.HtmlUrl,
            asset.DownloadUrl, asset.Size, release.PublishedAt, release.Prerelease);
    }

    /// <summary>
    /// Downloads the installer into the staging directory and returns its path.
    ///
    /// Downloading is separated from installing so the bytes are already on disk when the idle
    /// window opens. Waiting for quiet and *then* starting a 90 MB download would often mean the
    /// window closed before it finished.
    /// </summary>
    public async Task<string> DownloadAsync(
        AvailableUpdate update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        AppPaths.EnsureMachineStore();

        var directory = Path.Combine(AppPaths.UpdateStagingDir, update.Version);
        var path = Path.Combine(directory, $"CloudDrive-Setup-{update.Version}.exe");

        if (File.Exists(path) && new FileInfo(path).Length == update.SizeBytes)
        {
            _log?.Invoke($"CloudDrive {update.Version} is already downloaded.");
            return path;
        }

        _log?.Invoke($"Downloading CloudDrive {update.Version}…");
        await _releases.DownloadAsync(update.DownloadUrl, path, progress, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Launches the downloaded installer silently and returns immediately.
    ///
    /// <para>It has to be fire-and-forget: the installer stops the service, replaces the very
    /// binaries this code is running from, and starts it again. Waiting for it would mean waiting
    /// for a process that is going to kill the waiter. The installer is responsible for restoring
    /// the mounts, which it gets for free — the service converges on the configuration in
    /// <c>%ProgramData%</c>, which the upgrade leaves alone.</para>
    /// </summary>
    /// <param name="installerPath">Path returned by <see cref="DownloadAsync"/>.</param>
    /// <param name="restartService">Ask the installer to start the service again when it finishes.</param>
    public void LaunchInstaller(string installerPath, bool restartService = true)
    {
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("The downloaded installer is missing.", installerPath);

        // Inno Setup's silent switches. /NORESTART because a mount service must never trigger a
        // surprise reboot; anything needing one can wait for a scheduled restart.
        var arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-";
        if (restartService) arguments += " /RESTARTSERVICE";

        _log?.Invoke($"Launching {Path.GetFileName(installerPath)} {arguments}");

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>
    /// Deletes staged downloads for versions other than <paramref name="keepVersion"/>. Installers
    /// are ~90 MB each and there is no reason to keep the ones already applied.
    /// </summary>
    public static int CleanStaging(string? keepVersion = null)
    {
        if (!Directory.Exists(AppPaths.UpdateStagingDir)) return 0;

        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(AppPaths.UpdateStagingDir))
        {
            var name = Path.GetFileName(directory);
            if (keepVersion is not null && string.Equals(name, keepVersion, StringComparison.OrdinalIgnoreCase))
                continue;
            try { Directory.Delete(directory, recursive: true); removed++; }
            catch { /* in use; the next sweep retries */ }
        }
        return removed;
    }

    /// <summary>
    /// A poll interval with a deterministic per-machine offset of up to an hour.
    ///
    /// A fleet cloned from one image would otherwise poll GitHub at the same second forever, which
    /// looks like an attack from GitHub's side and gets rate-limited from ours. Derived from the
    /// machine name rather than randomly so the offset survives a restart and a machine does not
    /// drift into checking constantly.
    /// </summary>
    public static TimeSpan JitteredInterval(int baseHours)
    {
        var hours = Math.Max(1, baseHours);
        var offsetMinutes = Math.Abs(Environment.MachineName.GetHashCode(StringComparison.Ordinal)) % 60;
        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(offsetMinutes);
    }
}
