namespace CloudDrive.Core.Models;

/// <summary>
/// The wire protocol that actually moves bytes.
///
/// This is deliberately separate from <see cref="ProviderId"/>. A provider is a brand — Wasabi, AWS,
/// Hetzner — and several brands speak the same protocol, while one brand (a Hetzner Storage Box) can
/// speak three. Code that moves data switches on the protocol; code that shows a logo, validates a
/// bucket name or builds a console URL switches on the provider.
/// </summary>
public enum StorageProtocol
{
    /// <summary>
    /// Measure the reachable protocols against the real server and use whichever is fastest from
    /// here. Only meaningful for a provider that offers more than one; see
    /// <see cref="ProviderDescriptor.SupportsProtocolBenchmark"/>.
    /// </summary>
    Auto,

    /// <summary>SFTP over SSH. Also what "SSH" and "SSHFS" mean — see the remarks on <see cref="ProviderId.Sftp"/>.</summary>
    Sftp,

    /// <summary>SMB/CIFS, the Windows file-sharing protocol.</summary>
    Smb,

    /// <summary>WebDAV over HTTP(S).</summary>
    WebDav,

    /// <summary>FTP, optionally wrapped in TLS (FTPS).</summary>
    Ftp,

    /// <summary>The S3 HTTP API, in any vendor's dialect.</summary>
    S3,

    /// <summary>Microsoft Graph, for OneDrive and SharePoint document libraries.</summary>
    Graph,

    /// <summary>The Google Drive v3 REST API.</summary>
    GoogleDrive,
}

/// <summary>
/// How a provider proves who you are. This drives which fields the credential form shows, and it is
/// the reason <see cref="Credentials"/> can stay one type instead of one per brand.
/// </summary>
public enum AuthKind
{
    /// <summary>An access key id and a secret key. Every S3 dialect, and Backblaze's key id/app key pair.</summary>
    KeyPair,

    /// <summary>A username and password, with no key alternative. SMB, FTP and WebDAV.</summary>
    Password,

    /// <summary>A username plus either a password or a private key. SSH-based backends.</summary>
    PasswordOrKey,

    /// <summary>OAuth 2 with a refresh token obtained interactively once. OneDrive and Google Drive.</summary>
    OAuth,
}

/// <summary>
/// Capabilities that differ between back ends and that callers must branch on. Declared as flags
/// rather than discovered by trying an operation and catching the failure, so the UI can grey out
/// what will not work instead of offering it and then apologising.
/// </summary>
[Flags]
public enum ProviderCapabilities
{
    None = 0,

    /// <summary>Can mint a time-limited public URL for one object. In practice, S3 dialects only.</summary>
    ShareLinks = 1 << 0,

    /// <summary>Rename/move happens on the server rather than as a download-and-re-upload.</summary>
    ServerSideMove = 1 << 1,

    /// <summary>
    /// Has no real directories, so an empty folder must be represented by a zero-byte marker object.
    /// Without one, a folder the user creates exists only in the client's memory: invisible to every
    /// other tool and gone on remount.
    /// </summary>
    NeedsDirectoryMarkers = 1 << 2,

    /// <summary>Carries a per-object change token (an S3 ETag) rather than needing a synthetic one.</summary>
    NativeChangeToken = 1 << 3,

    /// <summary>Content hashes can be read cheaply, so rclone can verify transfers.</summary>
    Hashes = 1 << 4,

    /// <summary>Objects are addressed inside a named container: an S3 bucket or an SMB share.</summary>
    Container = 1 << 5,

    /// <summary>The endpoint is chosen from a region list rather than typed in.</summary>
    Regions = 1 << 6,

    /// <summary>The user may type their own endpoint host. True for self-hosted and generic back ends.</summary>
    CustomEndpoint = 1 << 7,
}
