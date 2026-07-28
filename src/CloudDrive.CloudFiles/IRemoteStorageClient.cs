namespace CloudDrive.CloudFiles;

/// <summary>One file listed under a remote prefix.</summary>
/// <param name="Key">
/// Full remote key: for S3 the object key, for the file protocols the path relative to the mapping's
/// root. Always forward slashes, whatever the protocol uses on the wire.
/// </param>
/// <param name="Size">Size in bytes.</param>
/// <param name="LastModifiedUtc">Last-modified timestamp, in UTC.</param>
/// <param name="ETag">
/// A token that changes whenever the remote content does. S3 supplies a real ETag; the file
/// protocols have no equivalent, so <see cref="RemoteEntry.SyntheticTag"/> derives one. Either way
/// the reconciler only ever compares it for equality.
/// </param>
public sealed record RemoteEntry(string Key, long Size, DateTime LastModifiedUtc, string? ETag)
{
    /// <summary>
    /// A change token for back ends without ETags. Size alone misses a same-length edit and mtime
    /// alone misses a timestamp-preserving write, so both go in.
    /// </summary>
    public static string SyntheticTag(long size, DateTime lastModifiedUtc) =>
        $"{size:x}-{lastModifiedUtc.ToUniversalTime().Ticks:x}";
}

/// <summary>The outcome of a batched delete: which keys went, and which the server refused.</summary>
public sealed record DeleteResult(IReadOnlyList<string> Deleted, IReadOnlyList<string> Failed);

/// <summary>
/// The storage operations the Files On-Demand engine needs, independent of protocol.
///
/// The back ends differ enough that the on-demand layer cannot be written against any one of them:
/// S3 has no directories but has server-side copy and presigned URLs; SFTP and SMB have real
/// directories and cheap renames; WebDAV has directories and a MOVE verb; FTP has directories and
/// almost nothing else. This interface is the narrow set they can all honour, with the awkward
/// differences declared as capabilities rather than assumed.
/// </summary>
public interface IRemoteStorageClient : IDisposable
{
    /// <summary>Human-facing protocol name, for log lines and the UI.</summary>
    string ProtocolName { get; }

    /// <summary>
    /// True when the back end can produce a time-limited public URL for one object. In practice the
    /// S3 dialects and Google Drive; a Storage Box or an SMB share has no equivalent.
    /// </summary>
    bool SupportsShareLinks { get; }

    /// <summary>Enumerates every file at or under <paramref name="prefix"/>, recursively.</summary>
    IAsyncEnumerable<RemoteEntry> ListAsync(string? prefix = null, CancellationToken ct = default);

    /// <summary>
    /// Opens a read stream over a byte range. A null <paramref name="length"/> reads to the end.
    /// This is what feeds hydration, so it is called with whatever range Windows asks for.
    /// </summary>
    Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct = default);

    /// <summary>
    /// Uploads a local file to <paramref name="key"/>, overwriting and creating parent directories
    /// as needed. Returns the new change token.
    /// </summary>
    Task<string?> PutAsync(string key, string localPath, CancellationToken ct = default);

    /// <summary>Deletes one file. A missing file is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Deletes many files, batching where the protocol supports it. Partial failures are reported
    /// rather than thrown, so the keys that did go can be forgotten while the rest stay tracked.
    /// </summary>
    Task<DeleteResult> DeleteManyAsync(IEnumerable<string> keys, CancellationToken ct = default);

    /// <summary>Renames or moves a file on the server.</summary>
    Task MoveAsync(string sourceKey, string destKey, CancellationToken ct = default);

    /// <summary>A time-limited share URL, or null when <see cref="SupportsShareLinks"/> is false.</summary>
    string? CreateShareLink(string key, TimeSpan expiresIn);
}
