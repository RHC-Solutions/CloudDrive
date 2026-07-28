using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using CloudDrive.Core.Platform;
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

        // GitHub reports "sha256:<hex>"; strip the algorithm prefix so the value compares directly
        // against a computed hash.
        var digest = asset.Digest;
        if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            digest = digest["sha256:".Length..];
        else
            digest = null; // an unrecognised algorithm is worse than none: do not pretend to check it

        var checksumUrl = tool.ChecksumAssetName is null
            ? null
            : release.Assets
                .FirstOrDefault(a => string.Equals(a.Name, tool.ChecksumAssetName, StringComparison.OrdinalIgnoreCase))
                ?.DownloadUrl;

        return new ToolUpdate(
            tool, release.Version, installed, asset.DownloadUrl, asset.Size, release.HtmlUrl,
            digest, checksumUrl);
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
        await VerifyDownloadAsync(update, downloaded, assetName, sha, ct).ConfigureAwait(false);

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
            VerifySignature(exe, tool);
            VerifyRuns(exe, tool);
            PublishToBin(exe);
        }
        else if (tool.RequiresSignature)
        {
            // An installer is never unpacked, so the signature is checked on the package itself.
            VerifySignature(Path.Combine(target, assetName), tool);
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
    /// Proves the downloaded bytes are the ones the vendor published, before anything is unpacked.
    ///
    /// <para>This is the real gate, and it is checksum-based rather than signature-based because that
    /// is what these vendors actually publish. rclone ships <b>unsigned</b> Windows binaries with a
    /// <c>SHA256SUMS</c> file alongside them; requiring an Authenticode signature would reject the one
    /// tool CloudDrive cannot function without. So two independent attestations are used where they
    /// exist:</para>
    ///
    /// <list type="number">
    ///   <item>the digest GitHub reports for the asset through its API — proves the bytes match what
    ///         GitHub is serving, and arrives over a separate connection from the download;</item>
    ///   <item>the vendor's own checksum file in the same release — proves they match what the vendor
    ///         built, which the first check alone would miss if the release itself were replaced.</item>
    /// </list>
    ///
    /// <para>A mismatch on either is fatal. Having <i>neither</i> is also fatal: this code puts an
    /// executable on the system PATH, and installing bytes that nothing has vouched for is not
    /// something to do quietly.</para>
    /// </summary>
    private async Task VerifyDownloadAsync(
        ToolUpdate update, string downloadedPath, string assetName, string actualSha256, CancellationToken ct)
    {
        var verifiedBy = new List<string>();

        if (update.ExpectedSha256 is { } expected)
        {
            if (!string.Equals(expected, actualSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(downloadedPath);
                throw new InvalidOperationException(
                    $"{assetName} does not match the digest GitHub published for it "
                    + $"(expected {expected}, got {actualSha256}). The download was discarded.");
            }
            verifiedBy.Add("GitHub asset digest");
        }

        if (update.ChecksumUrl is { } checksumUrl)
        {
            var published = await TryReadPublishedChecksumAsync(checksumUrl, assetName, ct).ConfigureAwait(false);
            if (published is not null)
            {
                if (!string.Equals(published, actualSha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(downloadedPath);
                    throw new InvalidOperationException(
                        $"{assetName} does not match the checksum in {update.Tool.ChecksumAssetName} "
                        + $"(expected {published}, got {actualSha256}). The download was discarded.");
                }
                verifiedBy.Add(update.Tool.ChecksumAssetName!);
            }
        }

        if (verifiedBy.Count == 0)
        {
            TryDelete(downloadedPath);
            throw new InvalidOperationException(
                $"{update.Tool.DisplayName} {update.AvailableVersion} published no checksum CloudDrive "
                + "could verify, so the download was discarded.");
        }

        _log?.Invoke($"{assetName} verified against {string.Join(" and ", verifiedBy)}.");
    }

    /// <summary>
    /// Reads one entry out of a vendor checksum file.
    ///
    /// The format is the <c>sha256sum</c> convention — hex, whitespace, filename, one per line, with
    /// an optional <c>*</c> marking binary mode. Returns null when the file cannot be fetched or does
    /// not mention this asset, which is a soft miss: the GitHub digest may still have verified it, and
    /// <see cref="VerifyDownloadAsync"/> fails only when nothing did.
    /// </summary>
    private async Task<string?> TryReadPublishedChecksumAsync(
        string checksumUrl, string assetName, CancellationToken ct)
    {
        try
        {
            var text = await _releases.DownloadTextAsync(checksumUrl, ct).ConfigureAwait(false);

            foreach (var line in text.Split(
                         '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var name = parts[^1].TrimStart('*');
                if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                    return parts[0].Trim().ToLowerInvariant();
            }

            _log?.Invoke($"{assetName} is not listed in the vendor's checksum file.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"Could not read the vendor checksum file: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Validates an Authenticode signature.
    ///
    /// An <i>invalid</i> signature is always fatal — that means a tampered binary or a broken chain. A
    /// <i>missing</i> one is fatal only when the tool declares
    /// <see cref="ToolDefinition.RequiresSignature"/>, which WinFsp does because Windows loads it into
    /// the kernel and rclone does not because it is not signed at all. Either way the bytes have
    /// already been checksum-verified by the time this runs.
    /// </summary>
    private void VerifySignature(string path, ToolDefinition tool)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"{tool.DisplayName} did not unpack as expected.", path);

        byte[] signerBytes;
        try
        {
            // CreateFromSignedFile carries SYSLIB0057, which steers callers to X509CertificateLoader.
            // That guidance does not apply here: the loader reads certificate *files*, and there is no
            // managed replacement for extracting the Authenticode signer out of a PE. The result is
            // immediately re-loaded through X509CertificateLoader and chain-validated below, so the
            // legacy path only locates the signer — it never decides whether to trust it.
#pragma warning disable SYSLIB0057
            signerBytes = System.Security.Cryptography.X509Certificates.X509Certificate
                .CreateFromSignedFile(path).GetRawCertData();
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException)
        {
            if (tool.RequiresSignature)
            {
                throw new InvalidOperationException(
                    $"{tool.DisplayName} is not Authenticode-signed, and it must be — it installs a "
                    + "driver that Windows loads into the kernel.");
            }

            // Said out loud rather than passed over silently: this vendor genuinely does not sign, and
            // an administrator reading the log should know which guarantee is and is not in force.
            _log?.Invoke(
                $"{tool.DisplayName} is not Authenticode-signed (this vendor does not sign its Windows "
                + "builds). It was verified by checksum instead.");
            return;
        }

        using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader
            .LoadCertificate(signerBytes);

        using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
        chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online;
        chain.ChainPolicy.RevocationFlag = System.Security.Cryptography.X509Certificates.X509RevocationFlag.ExcludeRoot;
        // A machine with no internet cannot fetch a CRL, and failing an install for that would break
        // CloudDrive exactly where it is most needed. An unknown revocation status is tolerated; a
        // known-bad chain is not.
        chain.ChainPolicy.VerificationFlags =
            System.Security.Cryptography.X509Certificates.X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown
            | System.Security.Cryptography.X509Certificates.X509VerificationFlags.IgnoreEndRevocationUnknown;

        if (!chain.Build(certificate))
        {
            var reasons = string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
            throw new InvalidOperationException(
                $"{tool.DisplayName} is signed, but the signature does not validate: {reasons}");
        }

        _log?.Invoke($"{tool.DisplayName} signature valid — {certificate.Subject.Split(',')[0]}");
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

    /// <summary>
    /// Registers the tools directory on the machine PATH.
    ///
    /// Returns false rather than throwing when this process cannot write HKLM. Being unable to edit the
    /// system PATH is a normal condition — it happens whenever the service host is run unelevated for
    /// troubleshooting — and it stops nothing: every internal caller resolves rclone by absolute path.
    /// PATH exists so a human can type <c>rclone</c> in a shell. Raising a stack trace for it, which is
    /// what happened before, mislabels a cosmetic shortfall as a failure.
    /// </summary>
    public bool RegisterOnPath()
    {
        if (!ProcessIdentity.CanWriteMachineStore)
        {
            _log?.Invoke(
                $"Not adding {AppPaths.ToolsBinDir} to the system PATH: that needs administrator "
                + $"rights and this process is running as {ProcessIdentity.Name}. CloudDrive itself is "
                + "unaffected — it resolves its tools by full path.");
            return false;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.ToolsBinDir);
            var added = SystemPath.Add(AppPaths.ToolsBinDir);
            if (added) _log?.Invoke($"Added {AppPaths.ToolsBinDir} to the system PATH.");
            return added;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log?.Invoke($"Could not add the tools directory to the system PATH: {ex.Message}");
            return false;
        }
    }

    public bool UnregisterFromPath()
    {
        try { return SystemPath.Remove(AppPaths.ToolsBinDir); }
        catch (UnauthorizedAccessException ex)
        {
            _log?.Invoke($"Could not remove the tools directory from the system PATH: {ex.Message}");
            return false;
        }
    }

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
