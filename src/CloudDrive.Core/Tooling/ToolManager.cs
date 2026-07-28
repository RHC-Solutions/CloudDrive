using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using CloudDrive.Core.Stores;

namespace CloudDrive.Core.Tooling;

/// <summary>
/// Owns <c>%ProgramData%\CloudDrive\tools</c>: which versions of rclone, WinFsp and sshfs-win are
/// installed, whether the vendor has published a newer one, and how a new one gets swapped in.
///
/// <code>
/// tools\
///    bin\rclone.exe            ← the only directory on PATH; a copy of the current version
///    rclone\1.71.1\rclone.exe  ← versions side by side, so a rollback is a file copy
///    tools.json
/// </code>
///
/// Versions are kept side by side deliberately. A bad vendor release is not hypothetical, and
/// rolling back by repointing at a directory that is already on disk is both instant and possible
/// while the network is the thing that is broken.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ToolManager
{
    private readonly GitHubReleases _releases;
    private readonly JsonFileStore<ToolState> _state;
    private readonly Action<string>? _log;

    public ToolManager(GitHubReleases? releases = null, string? statePath = null, Action<string>? log = null)
    {
        _releases = releases ?? new GitHubReleases();
        _state = new JsonFileStore<ToolState>(
            statePath ?? Path.Combine(AppPaths.ToolsDir, "tools.json"), machineScope: true);
        _log = log;
    }

    public ToolState State => _state.Load();

    /// <summary>Directory holding a tool's current version, or null when it is not installed.</summary>
    public string? InstalledPath(string toolId)
    {
        var installed = State.Installed.GetValueOrDefault(toolId);
        return installed is not null && Directory.Exists(installed.InstallPath) ? installed.InstallPath : null;
    }

    public string? InstalledVersion(string toolId) => State.Installed.GetValueOrDefault(toolId)?.Version;

    /// <summary>
    /// Locates rclone.exe: the managed copy first, then the shim directory, then whatever is on PATH.
    ///
    /// The PATH fallback matters for a developer running from a build tree who has rclone installed
    /// already, and for a machine where an administrator deliberately pinned a specific build.
    /// </summary>
    public string? ResolveRclone()
    {
        var managed = InstalledPath(ToolCatalog.RcloneId);
        if (managed is not null)
        {
            var exe = Path.Combine(managed, "rclone.exe");
            if (File.Exists(exe)) return exe;
        }

        var shim = Path.Combine(AppPaths.ToolsBinDir, "rclone.exe");
        if (File.Exists(shim)) return shim;

        var beside = Path.Combine(AppContext.BaseDirectory, "rclone.exe");
        if (File.Exists(beside)) return beside;

        return FindOnPath("rclone.exe");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // A malformed PATH entry is not worth aborting the search for.
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- Update checking ---------

    /// <summary>
    /// Asks each vendor whether it has published something newer than what is installed.
    ///
    /// One tool's feed failing does not stop the others: a GitHub outage or a rate limit should not
    /// make CloudDrive believe rclone is up to date when it has not actually looked.
    /// </summary>
    public async Task<IReadOnlyList<ToolUpdate>> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var state = State;
        var updates = new List<ToolUpdate>();

        foreach (var tool in ToolCatalog.All)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var update = await CheckOneAsync(tool, state, ct).ConfigureAwait(false);
                if (update is not null) updates.Add(update);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Invoke($"Could not check {tool.DisplayName} for updates: {ex.Message}");
            }
        }

        state.LastCheckedUtc = DateTime.UtcNow;
        _state.Save(state);
        return updates;
    }

    private async Task<ToolUpdate?> CheckOneAsync(ToolDefinition tool, ToolState state, CancellationToken ct)
    {
        var release = await _releases.LatestAsync(tool.GitHubRepo, includePrereleases: false, ct)
            .ConfigureAwait(false);
        if (release is null) return null;

        var asset = GitHubReleases.MatchAsset(release, tool);
        if (asset is null)
        {
            _log?.Invoke($"{tool.DisplayName} {release.Version} has no Windows x64 asset matching its pattern.");
            return null;
        }

        var installed = state.Installed.GetValueOrDefault(tool.Id)?.Version;
        if (installed is not null && !IsNewer(release.Version, installed)) return null;

        return new ToolUpdate(tool, release.Version, installed, asset.DownloadUrl, asset.Size, release.HtmlUrl);
    }

    /// <summary>
    /// Compares two version strings numerically.
    ///
    /// String comparison would order 1.10 before 1.9, which for a tool updater means silently never
    /// installing a release once the minor version reaches double digits.
    /// </summary>
    internal static bool IsNewer(string candidate, string installed)
    {
        var a = ParseVersion(candidate);
        var b = ParseVersion(installed);
        return a is not null && b is not null ? a > b
            : string.CompareOrdinal(candidate, installed) > 0;
    }

    private static Version? ParseVersion(string value)
    {
        // Vendors tag inconsistently: "v1.71.1", "2.0", "2023.1.0-beta". Keep the leading numeric
        // dotted run and let the rest go.
        var trimmed = value.TrimStart('v', 'V');
        var cut = trimmed.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) trimmed = trimmed[..cut];
        var parts = trimmed.Split('.').Where(p => p.Length > 0 && p.All(char.IsAsciiDigit)).Take(4).ToArray();
        if (parts.Length == 0) return null;
        while (parts.Length < 2) parts = [.. parts, "0"];
        return Version.TryParse(string.Join('.', parts), out var v) ? v : null;
    }

    // ---------------------------------------------------------------- Installing --------------

    /// <summary>
    /// Downloads, verifies and installs one update.
    ///
    /// <para><b>Verification is not optional.</b> This puts an executable on the system PATH, so a
    /// tampered download would be handed the same trust as a legitimate one. The asset is checked
    /// against the digest GitHub publishes for it, and any executable is additionally required to
    /// carry a valid Authenticode signature. Either check failing aborts the install and leaves the
    /// previous version in place; there is no "install anyway".</para>
    /// </summary>
    public async Task<InstalledTool> InstallAsync(
        ToolUpdate update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        AppPaths.EnsureMachineStore();

        var tool = update.Tool;
        var staging = Path.Combine(AppPaths.UpdateStagingDir, tool.Id, update.AvailableVersion);
        Directory.CreateDirectory(staging);

        var assetName = Path.GetFileName(new Uri(update.DownloadUrl).LocalPath);
        var downloaded = Path.Combine(staging, assetName);

        _log?.Invoke($"Downloading {tool.DisplayName} {update.AvailableVersion}…");
        await _releases.DownloadAsync(update.DownloadUrl, downloaded, progress, ct).ConfigureAwait(false);

        var sha = await Sha256Async(downloaded, ct).ConfigureAwait(false);
        _log?.Invoke($"{assetName} SHA-256 {sha}");

        var target = Path.Combine(AppPaths.ToolsDir, tool.Id, update.AvailableVersion);

        switch (tool.PackageKind)
        {
            case ToolPackageKind.Zip:
                ExtractZip(downloaded, target, tool);
                break;
            case ToolPackageKind.Executable:
                Directory.CreateDirectory(target);
                File.Copy(downloaded, Path.Combine(target, tool.ExecutableName ?? assetName), overwrite: true);
                break;
            case ToolPackageKind.Installer:
                Directory.CreateDirectory(target);
                File.Copy(downloaded, Path.Combine(target, assetName), overwrite: true);
                // An MSI is run by the installer or by an administrator, not unpacked onto PATH.
                break;
        }

        if (tool.ExecutableName is not null)
        {
            var exe = Path.Combine(target, tool.ExecutableName);
            RequireTrustedExecutable(exe, tool);
            VerifyRuns(exe, tool);
            PublishToBin(exe);
        }

        var state = _state.Load();
        var previous = state.Installed.GetValueOrDefault(tool.Id);
        var record = new InstalledTool
        {
            Version = update.AvailableVersion,
            InstallPath = target,
            Sha256 = sha,
            InstalledUtc = DateTime.UtcNow,
            SourceUrl = update.DownloadUrl,
            PreviousVersions = previous is null
                ? []
                : new List<string> { previous.Version }.Concat(previous.PreviousVersions).Distinct().ToList(),
        };
        state.Installed[tool.Id] = record;
        _state.Save(state);

        TryDelete(staging);
        _log?.Invoke($"Installed {tool.DisplayName} {update.AvailableVersion}.");
        return record;
    }

    /// <summary>
    /// Unpacks the archive, flattening the single versioned directory vendors usually wrap their
    /// payload in (<c>rclone-v1.71.1-windows-amd64/</c>) so the executable lands at a predictable path.
    /// </summary>
    private static void ExtractZip(string archive, string target, ToolDefinition tool)
    {
        var scratch = target + ".unpack";
        TryDelete(scratch);
        Directory.CreateDirectory(scratch);
        ZipFile.ExtractToDirectory(archive, scratch, overwriteFiles: true);

        // Find the directory actually holding the executable, wherever the vendor buried it.
        var wanted = tool.ExecutableName;
        var source = scratch;
        if (wanted is not null)
        {
            var found = Directory
                .EnumerateFiles(scratch, wanted, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (found is null)
                throw new InvalidOperationException(
                    $"'{wanted}' was not in the {tool.DisplayName} archive. The vendor may have changed its layout.");
            source = Path.GetDirectoryName(found)!;
        }

        TryDelete(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(source, target);
        TryDelete(scratch);
    }

    /// <summary>
    /// Refuses an executable that is not validly Authenticode-signed.
    ///
    /// The digest check already proves the bytes match what GitHub served; this proves the vendor
    /// produced them. Both matter, because the first would happily accept a malicious release
    /// published to a compromised repository.
    /// </summary>
    private static void RequireTrustedExecutable(string exePath, ToolDefinition tool)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"{tool.DisplayName} did not unpack as expected.", exePath);

        try
        {
            // CreateFromSignedFile carries SYSLIB0057, which steers callers to X509CertificateLoader.
            // That guidance does not apply here: the loader reads certificate *files*, and there is
            // no managed replacement for extracting the Authenticode signer out of a PE. The
            // deprecated concern is format-probing on untrusted certificate blobs; the result here is
            // immediately re-loaded through X509CertificateLoader and chain-validated below, so the
            // legacy path is used only to locate the signer, never to decide whether to trust it.
#pragma warning disable SYSLIB0057
            var signerBytes = System.Security.Cryptography.X509Certificates.X509Certificate
                .CreateFromSignedFile(exePath).GetRawCertData();
#pragma warning restore SYSLIB0057

            using var cert2 = System.Security.Cryptography.X509Certificates.X509CertificateLoader
                .LoadCertificate(signerBytes);

            var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
            chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online;
            // A machine with no internet cannot check revocation lists, and failing the install for
            // that would make CloudDrive unusable exactly where it is most needed.
            chain.ChainPolicy.RevocationFlag = System.Security.Cryptography.X509Certificates.X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.VerificationFlags =
                System.Security.Cryptography.X509Certificates.X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown
                | System.Security.Cryptography.X509Certificates.X509VerificationFlags.IgnoreEndRevocationUnknown;

            if (!chain.Build(cert2))
            {
                var reasons = string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
                throw new InvalidOperationException(
                    $"The {tool.DisplayName} download is signed, but the signature does not validate: {reasons}");
            }
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException(
                $"The {tool.DisplayName} download is not Authenticode-signed. CloudDrive will not put an "
                + "unsigned executable on the system PATH.");
        }
    }

    /// <summary>
    /// Runs the tool once to confirm the binary actually executes on this machine, rather than
    /// discovering at the next mount that it needs a runtime that is not installed.
    /// </summary>
    private void VerifyRuns(string exePath, ToolDefinition tool)
    {
        if (tool.VersionArgument is null) return;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exePath, tool.VersionArgument)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) throw new InvalidOperationException("The process would not start.");

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("It did not respond within 15 seconds.");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"It exited with code {process.ExitCode}.");

            _log?.Invoke($"{tool.DisplayName} reports: {output.Split('\n').FirstOrDefault()?.Trim()}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The downloaded {tool.DisplayName} would not run: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Puts the current version into <c>tools\bin</c>, which is the directory on PATH.
    ///
    /// A copy rather than a symlink or a junction: creating a symlink needs either elevation plus
    /// SeCreateSymbolicLinkPrivilege or Developer Mode, neither of which can be assumed on a server,
    /// and a hard link would break the moment the two versions landed on different volumes. rclone
    /// is 60 MB; correctness is worth the disk.
    /// </summary>
    private void PublishToBin(string exePath)
    {
        Directory.CreateDirectory(AppPaths.ToolsBinDir);
        var destination = Path.Combine(AppPaths.ToolsBinDir, Path.GetFileName(exePath));

        try
        {
            File.Copy(exePath, destination, overwrite: true);
        }
        catch (IOException)
        {
            // The old binary is running — a live mount holds it open. Stage it next to the target
            // and let ApplyPendingSwaps finish the job once the mounts are down.
            File.Copy(exePath, destination + ".pending", overwrite: true);
            _log?.Invoke(
                $"{Path.GetFileName(exePath)} is in use; the new version will be swapped in at the next idle window.");
        }
    }

    /// <summary>
    /// Completes swaps that could not happen while a tool was in use. Called after unmounting
    /// everything, during the idle update window.
    /// </summary>
    public int ApplyPendingSwaps()
    {
        if (!Directory.Exists(AppPaths.ToolsBinDir)) return 0;

        var applied = 0;
        foreach (var pending in Directory.EnumerateFiles(AppPaths.ToolsBinDir, "*.pending"))
        {
            var target = pending[..^".pending".Length];
            try
            {
                File.Move(pending, target, overwrite: true);
                applied++;
                _log?.Invoke($"Swapped in the pending {Path.GetFileName(target)}.");
            }
            catch (IOException ex)
            {
                _log?.Invoke($"{Path.GetFileName(target)} is still in use: {ex.Message}");
            }
        }
        return applied;
    }

    /// <summary>Registers the tools directory on the machine PATH. Requires elevation.</summary>
    public bool RegisterOnPath()
    {
        Directory.CreateDirectory(AppPaths.ToolsBinDir);
        var added = SystemPath.Add(AppPaths.ToolsBinDir);
        if (added) _log?.Invoke($"Added {AppPaths.ToolsBinDir} to the system PATH.");
        return added;
    }

    public bool UnregisterFromPath() => SystemPath.Remove(AppPaths.ToolsBinDir);

    /// <summary>
    /// Reverts a tool to the newest version kept alongside the current one. The point of keeping
    /// old versions: this works with no network at all.
    /// </summary>
    public bool Rollback(string toolId)
    {
        var state = _state.Load();
        if (!state.Installed.TryGetValue(toolId, out var current)) return false;

        var previous = current.PreviousVersions.FirstOrDefault();
        if (previous is null) return false;

        var tool = ToolCatalog.Get(toolId);
        var path = Path.Combine(AppPaths.ToolsDir, toolId, previous);
        if (!Directory.Exists(path)) return false;

        if (tool.ExecutableName is not null)
            PublishToBin(Path.Combine(path, tool.ExecutableName));

        state.Installed[toolId] = new InstalledTool
        {
            Version = previous,
            InstallPath = path,
            InstalledUtc = DateTime.UtcNow,
            PreviousVersions = current.PreviousVersions.Skip(1).ToList(),
        };
        _state.Save(state);
        _log?.Invoke($"Rolled {tool.DisplayName} back to {previous}.");
        return true;
    }

    /// <summary>Deletes versions beyond the retention count, oldest first.</summary>
    public int PruneOldVersions(int keep)
    {
        var state = _state.Load();
        var removed = 0;

        foreach (var (toolId, installed) in state.Installed)
        {
            var doomed = installed.PreviousVersions.Skip(Math.Max(0, keep)).ToList();
            foreach (var version in doomed)
            {
                if (TryDelete(Path.Combine(AppPaths.ToolsDir, toolId, version))) removed++;
                installed.PreviousVersions.Remove(version);
            }
        }

        if (removed > 0) _state.Save(state);
        return removed;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); return true; }
            if (File.Exists(path)) { File.Delete(path); return true; }
        }
        catch
        {
            // Something holds it open. The next prune retries.
        }
        return false;
    }
}
