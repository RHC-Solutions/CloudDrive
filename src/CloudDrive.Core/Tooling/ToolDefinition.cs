namespace CloudDrive.Core.Tooling;

/// <summary>How a tool's payload arrives from the vendor.</summary>
public enum ToolPackageKind
{
    /// <summary>A zip holding the executable, possibly inside a versioned directory.</summary>
    Zip,

    /// <summary>A bare .exe downloaded directly.</summary>
    Executable,

    /// <summary>An MSI that has to be run to install. Not placed on PATH.</summary>
    Installer,
}

/// <summary>
/// A third-party binary CloudDrive manages: where it comes from, how to verify it, and what to do
/// with it once downloaded.
///
/// Sources are the **vendor's** own release feed, never a CloudDrive mirror. A security fix in
/// rclone should reach users when rclone ships it, not when CloudDrive next cuts a release.
/// </summary>
public sealed record ToolDefinition
{
    /// <summary>Stable identifier, also the directory name under <c>tools\</c>.</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Why CloudDrive needs it, shown in the tools list.</summary>
    public required string Purpose { get; init; }

    /// <summary>GitHub <c>owner/repo</c> whose releases are the source of truth for versions.</summary>
    public required string GitHubRepo { get; init; }

    /// <summary>
    /// Substrings that identify the right x64 Windows asset in a release. All must appear in the
    /// asset name. Matching on a pattern rather than an exact name survives the version number
    /// changing on every release.
    /// </summary>
    public required IReadOnlyList<string> AssetNameContains { get; init; }

    /// <summary>Asset name substrings that disqualify a match, checked after <see cref="AssetNameContains"/>.</summary>
    public IReadOnlyList<string> AssetNameExcludes { get; init; } = [];

    public required ToolPackageKind PackageKind { get; init; }

    /// <summary>
    /// The executable to expose on PATH, relative to the unpacked payload. Null for an installer,
    /// which is run rather than linked.
    /// </summary>
    public string? ExecutableName { get; init; }

    /// <summary>
    /// Whether this tool is required for CloudDrive to mount anything at all. A missing required
    /// tool is an error surfaced in the UI; a missing optional one is a note.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Argument that makes the executable print its version, used to confirm the binary actually
    /// runs after an update rather than trusting that the download succeeded.
    /// </summary>
    public string? VersionArgument { get; init; }

    /// <summary>
    /// The vendor's own checksum file in the same release, if it publishes one — for rclone,
    /// <c>SHA256SUMS</c>.
    ///
    /// This is a second, independent attestation alongside the digest GitHub reports through its API.
    /// The API digest proves the bytes match what GitHub is serving; the vendor's file proves they
    /// match what the vendor built. Both are checked when both exist.
    /// </summary>
    public string? ChecksumAssetName { get; init; }

    /// <summary>
    /// Refuse this tool unless it carries a valid Authenticode signature.
    ///
    /// <para><b>Not every vendor signs, and pretending otherwise breaks the updater.</b> rclone ships
    /// unsigned Windows binaries and publishes SHA-256 sums instead; requiring a signature would
    /// reject the one tool CloudDrive cannot work without. WinFsp does sign, and it installs a
    /// kernel-mode driver, so it is held to the higher bar.</para>
    ///
    /// <para>When this is false a signature is still validated <i>if present</i> — an invalid
    /// signature is always fatal. What changes is whether its absence is.</para>
    /// </summary>
    public bool RequiresSignature { get; init; }
}

/// <summary>The tools CloudDrive manages.</summary>
public static class ToolCatalog
{
    public const string RcloneId = "rclone";
    public const string WinFspId = "winfsp";
    public const string SshfsId = "sshfs-win";

    public static readonly IReadOnlyList<ToolDefinition> All =
    [
        new ToolDefinition
        {
            Id = RcloneId,
            DisplayName = "rclone",
            Purpose = "Mounts drive letters and directory mountpoints.",
            GitHubRepo = "rclone/rclone",
            AssetNameContains = ["windows", "amd64", ".zip"],
            // rclone publishes an "-osarch" bundle of every platform in one archive; matching it
            // would download hundreds of megabytes to extract one exe.
            AssetNameExcludes = ["osarch"],
            PackageKind = ToolPackageKind.Zip,
            ExecutableName = "rclone.exe",
            Required = true,
            VersionArgument = "version",
            ChecksumAssetName = "SHA256SUMS",
            // rclone does not Authenticode-sign its Windows builds; it publishes SHA256SUMS. The
            // digest check is the real guarantee here.
            RequiresSignature = false,
        },
        new ToolDefinition
        {
            Id = WinFspId,
            DisplayName = "WinFsp",
            Purpose = "The filesystem driver rclone mounts through. Needed for drive letters.",
            GitHubRepo = "winfsp/winfsp",
            AssetNameContains = ["winfsp", ".msi"],
            PackageKind = ToolPackageKind.Installer,
            // A kernel driver, so it is installed rather than dropped in a folder, and it never
            // goes on PATH. A driver that Windows will load into the kernel gets the strict bar.
            Required = true,
            RequiresSignature = true,
        },
        new ToolDefinition
        {
            Id = SshfsId,
            DisplayName = "SSHFS-Win",
            Purpose = "Optional: mount SFTP directly through WinFsp instead of through rclone.",
            GitHubRepo = "winfsp/sshfs-win",
            AssetNameContains = ["sshfs-win", "x64", ".msi"],
            PackageKind = ToolPackageKind.Installer,
            Required = false,
            RequiresSignature = true,
        },
    ];

    public static ToolDefinition Get(string id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentOutOfRangeException(nameof(id), id, "No such managed tool.");
}

/// <summary>What is installed, recorded in <c>tools\tools.json</c>.</summary>
public sealed class ToolState
{
    public Dictionary<string, InstalledTool> Installed { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime? LastCheckedUtc { get; set; }
}

/// <summary>One installed version of one tool.</summary>
public sealed class InstalledTool
{
    public string Version { get; set; } = string.Empty;

    /// <summary>Directory holding this version, under <c>tools\&lt;id&gt;\&lt;version&gt;</c>.</summary>
    public string InstallPath { get; set; } = string.Empty;

    /// <summary>SHA-256 of the downloaded asset, so a re-download can be skipped and tampering seen.</summary>
    public string? Sha256 { get; set; }

    public DateTime InstalledUtc { get; set; }

    /// <summary>Where it came from, kept for auditability.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>Versions kept alongside this one, newest first, for rollback.</summary>
    public List<string> PreviousVersions { get; set; } = [];
}

/// <summary>A newer version found at the vendor.</summary>
/// <param name="ExpectedSha256">
/// The digest GitHub reports for this asset, lower-case hex with no prefix, or null when the API did
/// not supply one. Checked against the downloaded bytes before anything is installed.
/// </param>
/// <param name="ChecksumUrl">
/// The vendor's own checksum file in the same release, when it publishes one.
/// </param>
public sealed record ToolUpdate(
    ToolDefinition Tool,
    string AvailableVersion,
    string? InstalledVersion,
    string DownloadUrl,
    long SizeBytes,
    string? ReleaseNotesUrl,
    string? ExpectedSha256 = null,
    string? ChecksumUrl = null);
