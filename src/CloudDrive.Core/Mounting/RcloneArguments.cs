using CloudDrive.Core.Models;

namespace CloudDrive.Core.Mounting;

/// <summary>
/// Translates a mapping into an <c>rclone mount</c> argument list.
///
/// Static and pure so the whole thing is unit-testable without launching a process — which matters,
/// because a wrong flag here is the difference between a drive that behaves like a local disk and
/// one that silently routes deletes into a Recycle Bin on paid storage.
/// </summary>
public static class RcloneArguments
{
    /// <summary>
    /// Builds the argument list for <paramref name="mapping"/> over <paramref name="protocol"/>.
    /// </summary>
    public static IReadOnlyList<string> BuildMount(
        Mapping mapping, StorageProtocol protocol, bool verbose = false)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (string.IsNullOrWhiteSpace(mapping.MountPoint))
            throw new InvalidOperationException("The mapping has no mount point configured.");

        var cache = mapping.Cache;

        var args = new List<string>
        {
            "mount",
            mapping.RemoteTargetFor(protocol),
            mapping.MountPoint,
            "--vfs-cache-mode", cache.CacheMode.ToString().ToLowerInvariant(),
            "--dir-cache-time", Duration(cache.DirCacheTime),
            "--buffer-size", $"{Math.Max(0, cache.BufferSizeMb)}Mi",
            "--volname", mapping.VolumeLabel,
            "--no-console",
            "--log-level", verbose ? "DEBUG" : "INFO",
        };

        AddPresentationArguments(args, mapping);

        if (mapping.ReadOnly) args.Add("--read-only");

        if (cache.CacheMode != VfsCacheMode.Off)
        {
            if (cache.VfsCacheMaxSizeMb > 0)
            {
                args.Add("--vfs-cache-max-size");
                args.Add($"{cache.VfsCacheMaxSizeMb}Mi");
            }
            args.Add("--vfs-cache-max-age");
            args.Add(Duration(cache.VfsCacheMaxAge));
        }

        AddThroughputArguments(args, cache, protocol);

        if (!string.IsNullOrWhiteSpace(cache.CacheDir))
        {
            args.Add("--cache-dir");
            args.Add(cache.CacheDir!.Trim());
        }

        return args;
    }

    /// <summary>
    /// How the mount presents itself to Windows: a fixed disk or a network drive.
    ///
    /// <para><b>Fixed disk is the default, and that is the whole point.</b> rclone mounts a drive
    /// letter through WinFsp as a disk device unless <c>--network-mode</c> is passed, and a disk
    /// device is what appears under "Devices and drives" in Explorer with a real volume label and a
    /// custom icon — which is how Google Drive presents itself. Both source projects passed
    /// <c>--network-mode</c> unconditionally, which is why their drives landed under "Network
    /// locations" instead.</para>
    ///
    /// <para>The cost of the switch is a Recycle Bin. Windows gives fixed disks one and never gives
    /// network drives one, so a delete on a disk-mode mount becomes a server-side copy into a hidden
    /// <c>$RECYCLE.BIN</c> that goes on consuming paid storage forever. That is not handled here —
    /// see <c>DriveAppearance.ConfigureVolume</c>, which sets the volume's <c>NukeOnDelete</c> policy
    /// so deletes go straight through.</para>
    ///
    /// <para>Network mode stays available per mapping because rclone's own guidance is that some
    /// applications misbehave against fixed-disk FUSE mounts. It cannot apply to a directory
    /// mountpoint: Windows will not point a junction at a network device, and rclone responds by
    /// logging an error and mounting as a disk anyway — so passing it there would only put a red
    /// line in the activity log for a flag that was never going to take effect.</para>
    /// </summary>
    private static void AddPresentationArguments(List<string> args, Mapping mapping)
    {
        if (mapping.PresentAsNetworkDrive && mapping.MountTarget == MountTarget.DriveLetter)
        {
            args.Add("--network-mode");
            return;
        }

        // Disk mode. Naming the filesystem NTFS is not cosmetic: Explorer and a good deal of
        // third-party software gate features — the Security tab, alternate data streams, long-path
        // handling — on the filesystem name, and an unrecognised one makes a drive that looks local
        // behave in ways a local drive never would.
        //
        // This goes through -o, not a flag of its own. FileSystemName is a WinFsp mount option, and
        // rclone forwards -o/--option through to WinFsp verbatim. There is no --file-system-name flag:
        // passing one made rclone exit immediately with "unknown flag", so every drive-letter mount
        // failed with "rclone exited unexpectedly" and no other explanation.
        args.Add("-o");
        args.Add("FileSystemName=NTFS");
    }

    /// <summary>
    /// Adds the throughput flags for <paramref name="protocol"/>.
    ///
    /// One flag set would be wrong for most of them. S3 rewards many concurrent HTTP range requests;
    /// SFTP multiplexes inside a single SSH connection and is bounded by a server-side session cap;
    /// SMB pipelines in its own session and is actively hurt by rclone slicing reads apart; FTP has
    /// no multiplexing at all.
    /// </summary>
    public static void AddThroughputArguments(List<string> args, CacheSettings cache, StorageProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(cache);

        var (transfers, checkers) = ConcurrencyFor(cache, protocol);
        if (transfers > 0) { args.Add("--transfers"); args.Add(transfers.ToString()); }
        if (checkers > 0) { args.Add("--checkers"); args.Add(checkers.ToString()); }

        // Sequential read-ahead only does anything when whole files land in the cache.
        if (cache.ReadAheadMb > 0 && cache.CacheMode == VfsCacheMode.Full)
        {
            args.Add("--vfs-read-ahead");
            args.Add($"{cache.ReadAheadMb}Mi");
        }

        switch (protocol)
        {
            case StorageProtocol.S3:
                AddRangeReadArguments(args, cache, maxStreams: cache.ReadChunkStreams);
                if (cache.UploadConcurrency > 0)
                {
                    args.Add("--s3-upload-concurrency");
                    args.Add(cache.UploadConcurrency.ToString());
                }
                if (cache.UploadChunkSizeMb > 0)
                {
                    args.Add("--s3-chunk-size");
                    args.Add($"{cache.UploadChunkSizeMb}Mi");
                }
                // Reading an object's real modtime costs a HEAD per file; on a listing-heavy mount
                // that dominates everything else.
                if (cache.UseServerModTime) args.Add("--use-server-modtime");
                break;

            case StorageProtocol.Sftp:
                // Pipelined requests inside the single SSH connection rclone already holds. This is
                // SFTP's equivalent of upload concurrency and costs no extra sessions, which matters
                // because the server caps them. Parallel chunk streams would each open another
                // session, so the budget is better spent on concurrent whole-file transfers.
                if (cache.SftpConcurrency > 0)
                {
                    args.Add("--sftp-concurrency");
                    args.Add(cache.SftpConcurrency.ToString());
                }
                break;

            case StorageProtocol.WebDav:
                // Serves HTTP range requests, so parallel chunks help as they do on S3 — but with no
                // multipart upload, writes stay one stream per file. Capped lower than S3 because a
                // DAV server is usually one origin rather than a fleet.
                AddRangeReadArguments(args, cache, maxStreams: Math.Min(cache.ReadChunkStreams, 8));
                break;

            case StorageProtocol.Graph:
            case StorageProtocol.GoogleDrive:
                AddRangeReadArguments(args, cache, maxStreams: Math.Min(cache.ReadChunkStreams, 8));
                if (cache.UploadChunkSizeMb > 0)
                {
                    // Both APIs do resumable uploads in fixed-size chunks and reject a session whose
                    // chunk size is not a multiple of 320 KiB. Rounding here rather than letting the
                    // server reject the upload halfway through a large file.
                    var chunkMb = RoundToGraphChunk(cache.UploadChunkSizeMb);
                    args.Add(protocol == StorageProtocol.Graph ? "--onedrive-chunk-size" : "--drive-chunk-size");
                    args.Add($"{chunkMb}Mi");
                }
                if (protocol == StorageProtocol.GoogleDrive)
                {
                    // Drive's per-file metadata calls dominate a listing; asking for whole pages at a
                    // time is the single biggest win on a large tree.
                    args.Add("--drive-pacer-min-sleep");
                    args.Add("10ms");
                }
                break;

            case StorageProtocol.Smb:
                // Throughput comes purely from concurrent files. Slicing reads into separate streams
                // makes SMB slower, not faster.
                break;

            case StorageProtocol.Ftp:
                // No multiplexing: one command channel carries one transfer. Concurrency is a
                // connection-pool setting on the remote itself, already in RcloneConfig.
                break;
        }

        // Detecting changes by size and modtime rather than hashing avoids a round trip whenever the
        // VFS revalidates a cached file, on every back end.
        if (cache.FastFingerprint) args.Add("--vfs-fast-fingerprint");
    }

    private static void AddRangeReadArguments(List<string> args, CacheSettings cache, int maxStreams)
    {
        if (maxStreams > 0)
        {
            args.Add("--vfs-read-chunk-streams");
            args.Add(maxStreams.ToString());
        }
        if (cache.ReadChunkSizeMb > 0)
        {
            args.Add("--vfs-read-chunk-size");
            args.Add($"{cache.ReadChunkSizeMb}Mi");
        }
    }

    /// <summary>
    /// Rounds an upload chunk size up to a whole number of MiB that is also a multiple of 320 KiB,
    /// which is what the Graph and Drive resumable-upload endpoints require.
    /// </summary>
    private static int RoundToGraphChunk(int requestedMb)
    {
        // 320 KiB × 32 = 10 MiB, so any multiple of 10 MiB satisfies both constraints.
        const int step = 10;
        var rounded = (int)Math.Round(requestedMb / (double)step, MidpointRounding.AwayFromZero) * step;
        return Math.Max(step, rounded);
    }

    /// <summary>
    /// Resolves <c>--transfers</c> and <c>--checkers</c>.
    ///
    /// SFTP is the case that needs clamping. Every rclone transfer and checker on an SSH backend
    /// takes one of the server's concurrent sessions, and a Hetzner Storage Box allows about ten.
    /// Going over does not degrade gracefully — the server refuses the extra sessions and rclone
    /// surfaces them as checksum and I/O errors partway through a large copy — so the budget is
    /// split with one session left spare for the control connection.
    /// </summary>
    public static (int Transfers, int Checkers) ConcurrencyFor(CacheSettings cache, StorageProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(cache);

        var transfers = Math.Max(1, cache.Transfers);
        var checkers = Math.Max(1, cache.Checkers);

        if (protocol != StorageProtocol.Sftp) return (transfers, checkers);

        const int budget = StorageBox.MaxSshConnections - 1; // keep one in reserve
        var half = Math.Max(1, budget / 2);
        return (Math.Min(transfers, half), Math.Min(checkers, budget - half));
    }

    /// <summary>rclone accepts durations like "3600s"; whole seconds are unambiguous.</summary>
    private static string Duration(TimeSpan span) => $"{Math.Max(0, (long)span.TotalSeconds)}s";
}
