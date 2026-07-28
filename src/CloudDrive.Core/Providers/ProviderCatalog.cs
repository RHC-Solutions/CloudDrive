using CloudDrive.Core.Models;

namespace CloudDrive.Core.Providers;

/// <summary>
/// The descriptor for every supported brand, and the region tables they draw on.
///
/// This file is the whole "add a provider" surface. Everything downstream — the account dialog, the
/// rclone config writer, the storage-client factory, the Explorer menu — reads it rather than
/// branching on <see cref="ProviderId"/> itself.
/// </summary>
public static class ProviderCatalog
{
    // Capability sets shared by several brands. Named rather than repeated so a change to what
    // "an S3 dialect can do" happens once.
    private const ProviderCapabilities S3Caps =
        ProviderCapabilities.ShareLinks
        | ProviderCapabilities.ServerSideMove
        | ProviderCapabilities.NeedsDirectoryMarkers
        | ProviderCapabilities.NativeChangeToken
        | ProviderCapabilities.Hashes
        | ProviderCapabilities.Container
        | ProviderCapabilities.Regions;

    /// <summary>Real directories, cheap server-side rename, no ETag, no presigned URLs.</summary>
    private const ProviderCapabilities FileCaps =
        ProviderCapabilities.ServerSideMove | ProviderCapabilities.CustomEndpoint;

    public static readonly IReadOnlyList<ProviderDescriptor> All =
    [
        // ---------------------------------------------------------------- S3 dialects ---------
        new ProviderDescriptor
        {
            Id = ProviderId.Wasabi,
            DisplayName = "Wasabi",
            Description = "Wasabi Hot Cloud Storage buckets.",
            Protocols = [StorageProtocol.S3],
            Auth = AuthKind.KeyPair,
            Capabilities = S3Caps,
            ContainerLabel = "Bucket",
            Regions = Regions.Wasabi,
            DefaultRegion = "us-east-1",
            RcloneS3Provider = "Wasabi",
            ConsoleUrl = "https://console.wasabisys.com/",
            AccentColor = "#93C842",
        },
        new ProviderDescriptor
        {
            Id = ProviderId.AwsS3,
            DisplayName = "Amazon S3",
            Description = "AWS S3 buckets, including S3-compatible AWS storage classes.",
            Protocols = [StorageProtocol.S3],
            Auth = AuthKind.KeyPair,
            Capabilities = S3Caps,
            ContainerLabel = "Bucket",
            Regions = Regions.Aws,
            DefaultRegion = "us-east-1",
            RcloneS3Provider = "AWS",
            ConsoleUrl = "https://s3.console.aws.amazon.com/s3/buckets",
            AccentColor = "#EC6327",
        },
        new ProviderDescriptor
        {
            Id = ProviderId.BackblazeB2,
            DisplayName = "Backblaze B2",
            // Said out loud in the picker, because a user with a native-API key id will otherwise
            // wonder why the fields are labelled the way they are.
            Description = "Backblaze B2 buckets over the S3-compatible endpoint. "
                          + "Use an application key: its keyID is the access key, its "
                          + "applicationKey is the secret.",
            Protocols = [StorageProtocol.S3],
            Auth = AuthKind.KeyPair,
            Capabilities = S3Caps,
            ContainerLabel = "Bucket",
            Regions = Regions.Backblaze,
            DefaultRegion = "us-west-004",
            // B2's S3 gateway is not one of rclone's named dialects; "Other" plus an explicit
            // endpoint is what rclone's own B2-over-S3 documentation prescribes.
            RcloneS3Provider = "Other",
            ConsoleUrl = "https://secure.backblaze.com/b2_buckets.htm",
            AccentColor = "#EC6327",
        },
        new ProviderDescriptor
        {
            Id = ProviderId.HetznerObjectStorage,
            DisplayName = "Hetzner Object Storage",
            Description = "Hetzner S3 buckets. Not the same product as a Storage Box.",
            Protocols = [StorageProtocol.S3],
            Auth = AuthKind.KeyPair,
            Capabilities = S3Caps,
            ContainerLabel = "Bucket",
            Regions = Regions.HetznerObject,
            DefaultRegion = "fsn1",
            RcloneS3Provider = "Hetzner",
            ConsoleUrl = "https://console.hetzner.cloud/",
            AccentColor = "#5A4DA1",
        },
        new ProviderDescriptor
        {
            Id = ProviderId.GenericS3,
            DisplayName = "S3-compatible",
            Description = "Any other S3 API: MinIO, Ceph, Cloudflare R2, Storj, an appliance.",
            Protocols = [StorageProtocol.S3],
            Auth = AuthKind.KeyPair,
            // The only S3 entry with a typed endpoint and no region list.
            Capabilities = (S3Caps & ~ProviderCapabilities.Regions) | ProviderCapabilities.CustomEndpoint,
            ContainerLabel = "Bucket",
            RcloneS3Provider = "Other",
            AccentColor = "#47C2BE",
        },

        // ---------------------------------------------------------------- File protocols ------
        new ProviderDescriptor
        {
            Id = ProviderId.HetznerStorageBox,
            DisplayName = "Hetzner Storage Box",
            Description = "A Hetzner Storage Box over SFTP, SMB or WebDAV. Has no S3 endpoint.",
            // Order is the fallback preference: SFTP is the one service that is always switched on,
            // needs no Robot toggle, and carries real timestamps.
            Protocols = [StorageProtocol.Sftp, StorageProtocol.Smb, StorageProtocol.WebDav],
            Auth = AuthKind.PasswordOrKey,
            Capabilities = FileCaps | ProviderCapabilities.Hashes,
            ContainerLabel = "Folder",
            // Port 23 rather than 22: Hetzner's port 22 accepts file transfers but refuses
            // interactive commands, which silently disables rclone's checksum support.
            DefaultPort = 23,
            ConsoleUrl = "https://robot.hetzner.com/storage",
            AccentColor = "#5A4DA1",
        },
        new ProviderDescriptor
        {
            Id = ProviderId.Sftp,
            DisplayName = "SFTP / SSH",
            Description = "Any SSH server. This is also what SSHFS connects to — same protocol.",
            Protocols = [StorageProtocol.Sftp],
            Auth = AuthKind.PasswordOrKey,
            Capabilities = FileCaps | ProviderCapabilities.Hashes,
            ContainerLabel = "Remote path",
            DefaultPort = 22,
            AccentColor = "#47C2BE",
        },
        new ProviderDescriptor
        {
            Id = ProviderId.Smb,
            DisplayName = "SMB / CIFS",
            Description = "A Windows, Samba or NAS file share.",
            Protocols = [StorageProtocol.Smb],
            Auth = AuthKind.Password,
            Capabilities = FileCaps | ProviderCapabilities.Container,
            ContainerLabel = "Share",
            DefaultPort = 445,
            AccentColor = "#EC6327",
        },
        new ProviderDescriptor
        {
            Id = ProviderId.Ftp,
            DisplayName = "FTP / FTPS",
            Description = "An FTP server, with optional TLS.",
            Protocols = [StorageProtocol.Ftp],
            Auth = AuthKind.Password,
            // No hashes: FTP has no standard checksum command, so rclone verifies by size and time.
            Capabilities = FileCaps,
            ContainerLabel = "Remote path",
            DefaultPort = 21,
            AccentColor = "#93C842",
        },
        new ProviderDescriptor
        {
            Id = ProviderId.WebDav,
            DisplayName = "WebDAV",
            Description = "A WebDAV server: Nextcloud, ownCloud, or a plain DAV mount.",
            Protocols = [StorageProtocol.WebDav],
            Auth = AuthKind.Password,
            Capabilities = FileCaps,
            ContainerLabel = "Remote path",
            DefaultPort = 443,
            AccentColor = "#47C2BE",
        },

        // ---------------------------------------------------------------- OAuth back ends -----
        new ProviderDescriptor
        {
            Id = ProviderId.OneDrive,
            DisplayName = "OneDrive",
            Description = "OneDrive personal or business, and SharePoint document libraries.",
            Protocols = [StorageProtocol.Graph],
            Auth = AuthKind.OAuth,
            Capabilities = ProviderCapabilities.ServerSideMove
                           | ProviderCapabilities.NativeChangeToken
                           | ProviderCapabilities.Hashes
                           | ProviderCapabilities.Container,
            ContainerLabel = "Drive",
            ConsoleUrl = "https://onedrive.live.com/",
            AccentColor = "#5A4DA1",
        },
        new ProviderDescriptor
        {
            Id = ProviderId.GoogleDrive,
            DisplayName = "Google Drive",
            Description = "Google Drive, including shared drives.",
            Protocols = [StorageProtocol.GoogleDrive],
            Auth = AuthKind.OAuth,
            Capabilities = ProviderCapabilities.ShareLinks
                           | ProviderCapabilities.ServerSideMove
                           | ProviderCapabilities.NativeChangeToken
                           | ProviderCapabilities.Hashes
                           | ProviderCapabilities.Container,
            ContainerLabel = "Drive",
            ConsoleUrl = "https://drive.google.com/",
            AccentColor = "#93C842",
        },
    ];

    private static readonly Dictionary<ProviderId, ProviderDescriptor> ById =
        All.ToDictionary(p => p.Id);

    /// <summary>The descriptor for <paramref name="id"/>. Throws for an id with no entry.</summary>
    public static ProviderDescriptor Get(ProviderId id) =>
        ById.TryGetValue(id, out var d)
            ? d
            : throw new ArgumentOutOfRangeException(nameof(id), id, "No descriptor is registered for this provider.");

    public static ProviderDescriptor? Find(ProviderId id) => ById.GetValueOrDefault(id);

    /// <summary>Resolves a region by code, or null when the provider has no matching entry.</summary>
    public static ProviderRegion? FindRegion(ProviderId id, string? code) =>
        code is null
            ? null
            : Get(id).Regions.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------- Region tables --------
    //
    // These live in a nested class rather than as fields of ProviderCatalog, and that is load-order
    // correctness rather than tidiness. Static field initialisers run in textual order, so plain
    // fields declared below `All` would still be null while `All` was being built -- every provider
    // would silently get an empty region list and every region dropdown would ship blank. A nested
    // type initialises on first access instead, so the tables are ready whenever `All` reaches them
    // and they can stay down here where they do not bury the descriptors.

    private static class Regions
    {
        /// <summary>https://docs.wasabi.com/docs/what-are-the-service-urls-for-wasabis-different-storage-regions</summary>
        public static readonly IReadOnlyList<ProviderRegion> Wasabi =
        [
            new("us-east-1", "US East 1 (N. Virginia)", "s3.us-east-1.wasabisys.com"),
            new("us-east-2", "US East 2 (N. Virginia)", "s3.us-east-2.wasabisys.com"),
            new("us-central-1", "US Central 1 (Texas)", "s3.us-central-1.wasabisys.com"),
            new("us-west-1", "US West 1 (Oregon)", "s3.us-west-1.wasabisys.com"),
            new("ca-central-1", "Canada Central 1 (Toronto)", "s3.ca-central-1.wasabisys.com"),
            new("eu-central-1", "EU Central 1 (Amsterdam)", "s3.eu-central-1.wasabisys.com"),
            new("eu-central-2", "EU Central 2 (Frankfurt)", "s3.eu-central-2.wasabisys.com"),
            new("eu-west-1", "EU West 1 (London)", "s3.eu-west-1.wasabisys.com"),
            new("eu-west-2", "EU West 2 (Paris)", "s3.eu-west-2.wasabisys.com"),
            new("eu-south-1", "EU South 1 (Milan)", "s3.eu-south-1.wasabisys.com"),
            new("ap-northeast-1", "AP Northeast 1 (Tokyo)", "s3.ap-northeast-1.wasabisys.com"),
            new("ap-northeast-2", "AP Northeast 2 (Osaka)", "s3.ap-northeast-2.wasabisys.com"),
            new("ap-southeast-1", "AP Southeast 1 (Singapore)", "s3.ap-southeast-1.wasabisys.com"),
            new("ap-southeast-2", "AP Southeast 2 (Sydney)", "s3.ap-southeast-2.wasabisys.com"),
        ];

        /// <summary>
        /// Commercial AWS regions. Dual-stack and FIPS endpoints are deliberately left out: they are a
        /// per-account compliance choice, and a user who needs one can select "S3-compatible" and type
        /// the endpoint.
        /// </summary>
        public static readonly IReadOnlyList<ProviderRegion> Aws =
        [
            new("us-east-1", "US East (N. Virginia)", "s3.us-east-1.amazonaws.com"),
            new("us-east-2", "US East (Ohio)", "s3.us-east-2.amazonaws.com"),
            new("us-west-1", "US West (N. California)", "s3.us-west-1.amazonaws.com"),
            new("us-west-2", "US West (Oregon)", "s3.us-west-2.amazonaws.com"),
            new("ca-central-1", "Canada (Central)", "s3.ca-central-1.amazonaws.com"),
            new("eu-west-1", "Europe (Ireland)", "s3.eu-west-1.amazonaws.com"),
            new("eu-west-2", "Europe (London)", "s3.eu-west-2.amazonaws.com"),
            new("eu-west-3", "Europe (Paris)", "s3.eu-west-3.amazonaws.com"),
            new("eu-central-1", "Europe (Frankfurt)", "s3.eu-central-1.amazonaws.com"),
            new("eu-central-2", "Europe (Zurich)", "s3.eu-central-2.amazonaws.com"),
            new("eu-north-1", "Europe (Stockholm)", "s3.eu-north-1.amazonaws.com"),
            new("eu-south-1", "Europe (Milan)", "s3.eu-south-1.amazonaws.com"),
            new("ap-south-1", "Asia Pacific (Mumbai)", "s3.ap-south-1.amazonaws.com"),
            new("ap-northeast-1", "Asia Pacific (Tokyo)", "s3.ap-northeast-1.amazonaws.com"),
            new("ap-northeast-2", "Asia Pacific (Seoul)", "s3.ap-northeast-2.amazonaws.com"),
            new("ap-northeast-3", "Asia Pacific (Osaka)", "s3.ap-northeast-3.amazonaws.com"),
            new("ap-southeast-1", "Asia Pacific (Singapore)", "s3.ap-southeast-1.amazonaws.com"),
            new("ap-southeast-2", "Asia Pacific (Sydney)", "s3.ap-southeast-2.amazonaws.com"),
            new("ap-east-1", "Asia Pacific (Hong Kong)", "s3.ap-east-1.amazonaws.com"),
            new("sa-east-1", "South America (São Paulo)", "s3.sa-east-1.amazonaws.com"),
            new("me-central-1", "Middle East (UAE)", "s3.me-central-1.amazonaws.com"),
            new("af-south-1", "Africa (Cape Town)", "s3.af-south-1.amazonaws.com"),
        ];

        /// <summary>
        /// Backblaze S3 endpoints. The numeric suffix is the storage cluster the *bucket* lives in and
        /// is shown in the B2 console next to the bucket's endpoint — it is not a guessable geography
        /// code, which is why the display names name the cluster too.
        /// </summary>
        public static readonly IReadOnlyList<ProviderRegion> Backblaze =
        [
            new("us-west-000", "US West (cluster 000)", "s3.us-west-000.backblazeb2.com"),
            new("us-west-001", "US West (cluster 001)", "s3.us-west-001.backblazeb2.com"),
            new("us-west-002", "US West (cluster 002)", "s3.us-west-002.backblazeb2.com"),
            new("us-west-004", "US West (cluster 004)", "s3.us-west-004.backblazeb2.com"),
            new("us-east-005", "US East (cluster 005)", "s3.us-east-005.backblazeb2.com"),
            new("eu-central-003", "EU Central (cluster 003)", "s3.eu-central-003.backblazeb2.com"),
        ];

        public static readonly IReadOnlyList<ProviderRegion> HetznerObject =
        [
            new("fsn1", "Falkenstein (Germany)", "fsn1.your-objectstorage.com"),
            new("nbg1", "Nuremberg (Germany)", "nbg1.your-objectstorage.com"),
            new("hel1", "Helsinki (Finland)", "hel1.your-objectstorage.com"),
        ];
    }
}
