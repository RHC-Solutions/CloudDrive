using CloudDrive.Core.Providers;

namespace CloudDrive.Core.Models;

/// <summary>Live mount state for a mapping.</summary>
public enum MountState
{
    Unmounted,
    Mounting,
    Mounted,
    Unmounting,
    Error,
}

/// <summary>How storage is surfaced to the user.</summary>
public enum MappingMode
{
    /// <summary>
    /// A normal folder backed by the Windows Cloud Files API, the way OneDrive works: files appear
    /// in Explorer as placeholders and download only when opened.
    ///
    /// Requires Windows 10 1709 or Server 2019 — <c>cldapi.dll</c> does not exist on Server 2016.
    /// Check <c>OsCapabilities.SupportsFilesOnDemand</c> before offering it.
    /// </summary>
    OnDemandFolder,

    /// <summary>A drive backed by rclone and WinFsp, at a letter or a directory.</summary>
    DriveLetter,
}

/// <summary>Where a <see cref="MappingMode.DriveLetter"/> mapping attaches.</summary>
public enum MountTarget
{
    /// <summary>A drive letter such as <c>H:</c>.</summary>
    DriveLetter,

    /// <summary>
    /// A directory such as <c>C:\CloudDrive\Backups</c>. WinFsp creates it on mount and removes it
    /// on unmount, so it must not already exist.
    /// </summary>
    Directory,
}

/// <summary>Which process owns the mount.</summary>
public enum MountHost
{
    /// <summary>
    /// The Windows service. The mount exists before anyone signs in, survives logoff, and is visible
    /// from every session — a DOS device created by LocalSystem lands in the global namespace.
    /// This is the default, and the whole point of the product.
    /// </summary>
    Service,

    /// <summary>
    /// The tray app, in the signed-in user's session. Forced for on-demand folders, because a Cloud
    /// Files sync root lives inside a user profile and calls back into that user's session; there is
    /// no session-0 equivalent. Also selectable for a drive letter someone wants kept private to
    /// their own session.
    /// </summary>
    UserSession,
}

/// <summary>
/// One mount: an account, a path inside it, and how it should appear on this machine. Carries no
/// secrets and no connection details — those belong to the <see cref="Account"/> it names, so
/// rotating a password fixes every mapping at once.
/// </summary>
public sealed class Mapping
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The account this mounts. Several mappings may share one.</summary>
    public Guid AccountId { get; set; }

    /// <summary>Friendly name, e.g. "Backups". Becomes the volume label.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The container inside the account: an S3 bucket, an SMB share, a Graph drive id. Empty for
    /// providers whose account already lands in one place, such as SFTP or a Storage Box.
    /// </summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>Optional path within the container to surface as the root.</summary>
    public string? SubPath { get; set; }

    // --- Presentation ----------------------------------------------------------------------------

    public MappingMode Mode { get; set; } = MappingMode.DriveLetter;

    public MountHost Host { get; set; } = MountHost.Service;

    /// <summary>
    /// SID of the user a <see cref="MountHost.UserSession"/> mapping belongs to.
    ///
    /// The configuration is machine-wide and readable by every account on the box, so without an
    /// owner recorded there would be nothing stopping one standard user asking the service for
    /// another user's stored credentials in order to mount their private on-demand folder. The IPC
    /// layer refuses to release credentials for a mapping whose owner is not the caller.
    /// Null for a serviced mapping, which belongs to the machine rather than to a person.
    /// </summary>
    public string? OwnerSid { get; set; }

    public MountTarget MountTarget { get; set; } = MountTarget.DriveLetter;

    /// <summary>Drive letter without the colon, e.g. "H".</summary>
    public string DriveLetter { get; set; } = "H";

    /// <summary>Directory mountpoint when <see cref="MountTarget"/> is <see cref="MountTarget.Directory"/>.</summary>
    public string? MountDirectory { get; set; }

    /// <summary>
    /// Local folder registered as a Cloud Files sync root, for an on-demand mapping. Null uses a
    /// default under the user profile.
    /// </summary>
    public string? LocalFolderPath { get; set; }

    /// <summary>
    /// Present the drive as a network drive instead of a fixed disk.
    ///
    /// Off by default, and that default is the point: passing rclone's <c>--network-mode</c> is what
    /// makes a mount show up under "Network locations", and not passing it is what makes it a fixed
    /// disk under "Devices and drives" — which is how Google Drive presents itself. It stays
    /// available because rclone's own guidance is that a few applications misbehave against
    /// fixed-disk FUSE mounts, and because a network drive never gets a Recycle Bin.
    /// See <c>DriveAppearance</c> for how the fixed-disk Recycle Bin is dealt with instead.
    /// </summary>
    public bool PresentAsNetworkDrive { get; set; }

    /// <summary>Give the drive CloudDrive's icon in Explorer, the way Google Drive brands its letter.</summary>
    public bool UseCustomDriveIcon { get; set; } = true;

    // --- Behaviour --------------------------------------------------------------------------------

    /// <summary>Mount automatically: at boot for a serviced mapping, at logon for a session one.</summary>
    public bool AutoMount { get; set; } = true;

    /// <summary>Mount read-only. Cheap insurance for a mapping that exists to be restored from.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Never let an automatic update unmount this. For the mapping a nightly backup job writes to,
    /// where a ten-minute idle window is not proof that nothing is happening.
    /// </summary>
    public bool BlockAutoUpdateWhileMounted { get; set; }

    public CacheSettings Cache { get; set; } = CacheSettings.Default();

    // --- Derived ------------------------------------------------------------------------------

    /// <summary>rclone remote name. Unique per mapping, so two mappings never share config.</summary>
    public string RemoteName => "cd_" + Id.ToString("N");

    public string DriveTarget => (DriveLetter ?? string.Empty).TrimEnd(':') + ":";

    /// <summary>What rclone is told to mount onto, and what the readiness check watches for.</summary>
    public string MountPoint => MountTarget == MountTarget.Directory
        ? (MountDirectory ?? string.Empty).TrimEnd('\\', '/')
        : DriveTarget;

    /// <summary>Sub-path with separators and edges normalised: "" or "a/b".</summary>
    public string NormalizedSubPath =>
        string.IsNullOrWhiteSpace(SubPath) ? string.Empty : SubPath.Trim().Replace('\\', '/').Trim('/');

    /// <summary>
    /// Prefix that on-demand keys carry, so a key round-trips to the same remote path a drive mount
    /// would use. The container is not part of it: the storage client is already scoped to one.
    /// </summary>
    public string KeyPrefix => NormalizedSubPath.Length == 0 ? string.Empty : NormalizedSubPath + "/";

    /// <summary>Volume label shown in Explorer.</summary>
    public string VolumeLabel =>
        !string.IsNullOrWhiteSpace(Name) ? Name
        : !string.IsNullOrWhiteSpace(Container) ? Container
        : "CloudDrive";

    /// <summary>
    /// The rclone remote path: <c>remote:container/subpath</c>. Which segments apply depends on how
    /// the back end roots itself — S3 needs the bucket and SMB needs the share as the first path
    /// segment, while SFTP, FTP and WebDAV already land in the account's own directory.
    /// </summary>
    public string RemoteTargetFor(StorageProtocol protocol)
    {
        var segments = new List<string>();

        if (protocol is StorageProtocol.S3 or StorageProtocol.Smb && !string.IsNullOrWhiteSpace(Container))
            segments.Add(Container.Trim('/'));

        var sub = NormalizedSubPath;
        if (sub.Length > 0) segments.Add(sub);

        return RemoteName + ":" + string.Join('/', segments.Where(s => s.Length > 0));
    }

    /// <summary>
    /// Default directory mountpoint for a serviced mapping. Deliberately outside any user profile:
    /// the service account cannot reach one, and the mount is supposed to exist before a user signs in.
    /// </summary>
    public string DefaultMountDirectory
    {
        get
        {
            var leaf = !string.IsNullOrWhiteSpace(Name) ? Name
                : !string.IsNullOrWhiteSpace(Container) ? Container
                : "Storage";
            foreach (var c in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(c, '_');
            var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? @"C:\";
            return Path.Combine(systemRoot, "CloudDrive", leaf);
        }
    }

    /// <summary>
    /// True when this configuration is one the Windows service can honour. An on-demand folder never
    /// is, whatever <see cref="Host"/> says, so the check is on the mode rather than on the flag.
    /// </summary>
    public bool IsServiceable => Mode == MappingMode.DriveLetter && Host == MountHost.Service;

    /// <summary>Short description of where this points, for lists and log lines.</summary>
    public string RemoteDescription
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Container)) parts.Add(Container);
            if (NormalizedSubPath.Length > 0) parts.Add(NormalizedSubPath);
            return parts.Count > 0 ? string.Join('/', parts) : "/";
        }
    }

    public Mapping Clone()
    {
        var copy = (Mapping)MemberwiseClone();
        copy.Cache = Cache.Clone();
        return copy;
    }

    /// <summary>
    /// Everything that would require a remount if it changed. The service compares this string to
    /// decide whether a configuration edit means "remount" or "leave it alone".
    /// </summary>
    public string MountFingerprint(Account account) => string.Join('|',
        AccountId, Container, NormalizedSubPath, MountPoint, MountTarget,
        account.EffectiveProtocol, account.Host, account.EffectivePort, account.Username,
        PresentAsNetworkDrive, ReadOnly,
        Cache.CacheMode, Cache.VfsCacheMaxSizeMb, Cache.VfsCacheMaxAgeSeconds,
        Cache.DirCacheTimeSeconds, Cache.BufferSizeMb, Cache.CacheDir);

    /// <summary>Validates the mapping against its account, returning the problems found.</summary>
    public IReadOnlyList<string> Validate(Account account)
    {
        var problems = new List<string>();
        var descriptor = ProviderCatalog.Get(account.Provider);

        if (string.IsNullOrWhiteSpace(Name))
            problems.Add("The mapping needs a name.");

        if (descriptor.Has(ProviderCapabilities.Container) && string.IsNullOrWhiteSpace(Container))
            problems.Add($"A {descriptor.ContainerLabel.ToLowerInvariant()} is required.");

        if (Mode == MappingMode.DriveLetter)
        {
            if (MountTarget == MountTarget.DriveLetter)
            {
                var letter = (DriveLetter ?? string.Empty).TrimEnd(':');
                if (letter.Length != 1 || !char.IsAsciiLetter(letter[0]))
                    problems.Add("Pick a single drive letter.");
            }
            else if (string.IsNullOrWhiteSpace(MountDirectory))
            {
                problems.Add("A directory mountpoint is required.");
            }
        }
        else if (Host == MountHost.Service)
        {
            problems.Add(
                "Files On-Demand folders cannot be hosted by the service: a sync root lives inside a "
                + "user profile and has no session-0 equivalent.");
        }

        return problems;
    }
}
