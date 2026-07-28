namespace CloudDrive.Core.Models;

/// <summary>
/// A storage brand the user picks from the "Add account" list.
///
/// Values are persisted by name in <c>accounts.json</c>, so they may be added but never renamed or
/// renumbered. New brands go at the end.
/// </summary>
public enum ProviderId
{
    // --- S3 dialects -------------------------------------------------------------------------

    /// <summary>Wasabi Hot Cloud Storage.</summary>
    Wasabi,

    /// <summary>Amazon S3.</summary>
    AwsS3,

    /// <summary>
    /// Backblaze B2, reached over its S3-compatible endpoint rather than the native B2 API.
    /// One endpoint style means B2 reuses the entire S3 path — client, retry policy, presigned
    /// share links, multipart upload — instead of adding a back end that would duplicate all of it
    /// for no user-visible difference.
    /// </summary>
    BackblazeB2,

    /// <summary>Hetzner Object Storage. A different product from a Storage Box; see <see cref="HetznerStorageBox"/>.</summary>
    HetznerObjectStorage,

    /// <summary>Any other S3-compatible service: MinIO, Ceph, Cloudflare R2, Storj, an appliance.</summary>
    GenericS3,

    // --- File protocols ----------------------------------------------------------------------

    /// <summary>
    /// A Hetzner Storage Box: one account reachable over SFTP, SMB and WebDAV, with no S3 endpoint
    /// at all. Kept distinct from <see cref="HetznerObjectStorage"/> because conflating the two is
    /// the most common Hetzner setup mistake, and from the plain protocol providers because its
    /// hostnames, SMB share names and SSH port are all derived rather than typed.
    /// </summary>
    HetznerStorageBox,

    /// <summary>
    /// A plain SFTP server.
    ///
    /// This one entry is also what "SSH" and "SSHFS" mean. SFTP is a subsystem of SSH — the same
    /// connection, the same port, the same credentials — and SSHFS is the name of the *Linux FUSE
    /// client* that speaks it, not a protocol of its own. Offering three menu items that produce
    /// byte-identical connections would be a lie in the UI, so there is one provider and the
    /// description says what it covers.
    /// </summary>
    Sftp,

    /// <summary>An SMB/CIFS file share, on a NAS, a Windows server or Samba.</summary>
    Smb,

    /// <summary>An FTP server, optionally over TLS (FTPS).</summary>
    Ftp,

    /// <summary>A WebDAV server: Nextcloud, ownCloud, a plain DAV mount.</summary>
    WebDav,

    // --- OAuth back ends ---------------------------------------------------------------------

    /// <summary>OneDrive personal or business, and SharePoint document libraries, over Microsoft Graph.</summary>
    OneDrive,

    /// <summary>Google Drive, including shared drives, over the Drive v3 API.</summary>
    GoogleDrive,
}
