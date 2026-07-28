namespace CloudDrive.Core.Models;

/// <summary>rclone's VFS cache modes, in increasing order of how much it will keep on disk.</summary>
public enum VfsCacheMode
{
    /// <summary>No caching. Lowest disk use, but many applications cannot write at all.</summary>
    Off,

    /// <summary>Cache only file metadata.</summary>
    Minimal,

    /// <summary>Cache files that are opened for writing.</summary>
    Writes,

    /// <summary>Cache whole files on read and write. The default: the best application compatibility.</summary>
    Full,
}

/// <summary>
/// Per-mapping transfer and cache tuning. Defaults suit a fast link to an S3 endpoint; the
/// protocol-specific clamping happens in <c>RcloneArguments</c>, not here, because the same numbers
/// mean different things to different back ends.
/// </summary>
public sealed class CacheSettings
{
    public VfsCacheMode CacheMode { get; set; } = VfsCacheMode.Full;

    /// <summary>Cache ceiling in MiB; 0 means unlimited.</summary>
    public int VfsCacheMaxSizeMb { get; set; } = 10_240;

    /// <summary>How long an untouched cached file survives, in seconds.</summary>
    public int VfsCacheMaxAgeSeconds { get; set; } = 3600;

    /// <summary>How long a directory listing is trusted, in seconds.</summary>
    public int DirCacheTimeSeconds { get; set; } = 300;

    /// <summary>In-memory read-ahead buffer per open file, in MiB.</summary>
    public int BufferSizeMb { get; set; } = 32;

    /// <summary>Sequential read-ahead in MiB. Only does anything when whole files land in the cache.</summary>
    public int ReadAheadMb { get; set; } = 128;

    /// <summary>Concurrent file transfers.</summary>
    public int Transfers { get; set; } = 8;

    /// <summary>Concurrent metadata checks.</summary>
    public int Checkers { get; set; } = 16;

    /// <summary>Parallel range reads per open file. Throughput on S3 scales nearly linearly with this.</summary>
    public int ReadChunkStreams { get; set; } = 16;

    /// <summary>Size of each range read, in MiB.</summary>
    public int ReadChunkSizeMb { get; set; } = 32;

    /// <summary>Concurrent parts per multipart upload.</summary>
    public int UploadConcurrency { get; set; } = 8;

    /// <summary>Multipart upload part size, in MiB.</summary>
    public int UploadChunkSizeMb { get; set; } = 16;

    /// <summary>
    /// Pipelined SFTP requests inside the one SSH connection rclone already holds. This is SFTP's
    /// equivalent of upload concurrency and costs no extra sessions, which matters on a server that
    /// caps them.
    /// </summary>
    public int SftpConcurrency { get; set; } = 64;

    /// <summary>
    /// Trust the server's stored modification time rather than issuing a HEAD per object to read the
    /// real one. Worth a great deal on a listing-heavy S3 mount.
    /// </summary>
    public bool UseServerModTime { get; set; } = true;

    /// <summary>
    /// Detect changes from size and modification time instead of hashing. Saves a round trip
    /// whenever the VFS revalidates a cached file, on every back end.
    /// </summary>
    public bool FastFingerprint { get; set; } = true;

    /// <summary>Where rclone keeps its cache. Null uses rclone's default.</summary>
    public string? CacheDir { get; set; }

    public TimeSpan VfsCacheMaxAge => TimeSpan.FromSeconds(Math.Max(0, VfsCacheMaxAgeSeconds));

    public TimeSpan DirCacheTime => TimeSpan.FromSeconds(Math.Max(0, DirCacheTimeSeconds));

    public static CacheSettings Default() => new();

    public CacheSettings Clone() => (CacheSettings)MemberwiseClone();
}
