using CloudDrive.Core.Models;
using CloudDrive.Core.Mounting;

namespace CloudDrive.Tests;

/// <summary>
/// The mount argument list decides how the drive behaves in Explorer, so the flags that matter are
/// asserted rather than eyeballed. A wrong one here is the difference between a drive that acts like
/// a local disk and one that quietly routes deletes into a Recycle Bin on paid storage.
/// </summary>
public class RcloneArgumentTests
{
    private static Mapping DriveMapping() => new()
    {
        Name = "Backups",
        Container = "my-bucket",
        Mode = MappingMode.DriveLetter,
        MountTarget = MountTarget.DriveLetter,
        DriveLetter = "H",
    };

    [Fact]
    public void Drive_letter_mounts_as_a_fixed_disk_by_default()
    {
        var args = RcloneArguments.BuildMount(DriveMapping(), StorageProtocol.S3);

        // This is the whole "like Google Drive does it" change: --network-mode is what puts a mount
        // under "Network locations", and not passing it is what makes it a disk.
        Assert.DoesNotContain("--network-mode", args);

        // The filesystem name goes through -o, because FileSystemName is a WinFsp option and rclone has
        // no flag of its own for it. This assertion previously expected "--file-system-name", which is
        // what the builder emitted and what rclone rejects — the test and the code shared one wrong
        // assumption, so it passed while every drive mount failed. RcloneFlagValidityTests now checks
        // the emitted flags against rclone's own flag list, which is the check that catches this class
        // of mistake rather than restating it.
        Assert.Contains("-o", args);
        Assert.Contains("FileSystemName=NTFS", args);
        Assert.DoesNotContain("--file-system-name", args);
    }

    [Fact]
    public void Network_mode_is_available_as_an_opt_in()
    {
        var mapping = DriveMapping();
        mapping.PresentAsNetworkDrive = true;

        var args = RcloneArguments.BuildMount(mapping, StorageProtocol.S3);

        Assert.Contains("--network-mode", args);
        Assert.DoesNotContain("--file-system-name", args);
    }

    [Fact]
    public void Network_mode_is_ignored_for_a_directory_mountpoint()
    {
        // Windows will not point a junction at a network device, so rclone logs an error and mounts
        // as a disk anyway. Passing the flag would only put a red line in the activity log.
        var mapping = DriveMapping();
        mapping.PresentAsNetworkDrive = true;
        mapping.MountTarget = MountTarget.Directory;
        mapping.MountDirectory = @"C:\CloudDrive\Backups";

        var args = RcloneArguments.BuildMount(mapping, StorageProtocol.S3);

        Assert.DoesNotContain("--network-mode", args);
    }

    [Fact]
    public void Mount_point_and_remote_target_are_positional()
    {
        var mapping = DriveMapping();
        mapping.SubPath = "projects/2026";

        var args = RcloneArguments.BuildMount(mapping, StorageProtocol.S3);

        Assert.Equal("mount", args[0]);
        Assert.Equal($"{mapping.RemoteName}:my-bucket/projects/2026", args[1]);
        Assert.Equal("H:", args[2]);
    }

    [Fact]
    public void Read_only_is_passed_through()
    {
        var mapping = DriveMapping();
        mapping.ReadOnly = true;

        Assert.Contains("--read-only", RcloneArguments.BuildMount(mapping, StorageProtocol.S3));
    }

    [Fact]
    public void Volume_label_comes_from_the_mapping_name()
    {
        var args = RcloneArguments.BuildMount(DriveMapping(), StorageProtocol.S3);
        var index = args.ToList().IndexOf("--volname");

        Assert.True(index >= 0);
        Assert.Equal("Backups", args[index + 1]);
    }

    [Fact]
    public void A_mapping_with_no_mount_point_is_rejected()
    {
        var mapping = DriveMapping();
        mapping.MountTarget = MountTarget.Directory;
        mapping.MountDirectory = null;

        Assert.Throws<InvalidOperationException>(
            () => RcloneArguments.BuildMount(mapping, StorageProtocol.S3));
    }

    // ---------------------------------------------------------------- Per-protocol tuning -----

    [Fact]
    public void S3_gets_multipart_and_range_reads()
    {
        var args = RcloneArguments.BuildMount(DriveMapping(), StorageProtocol.S3);

        Assert.Contains("--s3-upload-concurrency", args);
        Assert.Contains("--s3-chunk-size", args);
        Assert.Contains("--vfs-read-chunk-streams", args);
        Assert.Contains("--use-server-modtime", args);
    }

    [Fact]
    public void Sftp_gets_pipelining_but_not_parallel_streams()
    {
        var args = RcloneArguments.BuildMount(DriveMapping(), StorageProtocol.Sftp);

        Assert.Contains("--sftp-concurrency", args);
        // Each parallel chunk stream would cost another SSH session, and the session budget is
        // better spent on concurrent whole files.
        Assert.DoesNotContain("--vfs-read-chunk-streams", args);
        Assert.DoesNotContain("--s3-chunk-size", args);
    }

    [Fact]
    public void Smb_relies_on_concurrent_files_alone()
    {
        var args = RcloneArguments.BuildMount(DriveMapping(), StorageProtocol.Smb);

        // Slicing reads into separate streams makes SMB slower, not faster.
        Assert.DoesNotContain("--vfs-read-chunk-streams", args);
        Assert.Contains("--transfers", args);
    }

    /// <summary>
    /// Exceeding a Storage Box's session cap does not degrade gracefully: the server refuses the
    /// extra sessions and rclone reports them as checksum errors partway through a large copy.
    /// </summary>
    [Fact]
    public void Sftp_concurrency_is_clamped_to_the_session_budget()
    {
        var cache = new CacheSettings { Transfers = 64, Checkers = 64 };

        var (transfers, checkers) = RcloneArguments.ConcurrencyFor(cache, StorageProtocol.Sftp);

        Assert.True(transfers + checkers < StorageBox.MaxSshConnections,
            $"{transfers} transfers + {checkers} checkers would exceed the ~{StorageBox.MaxSshConnections} session cap.");
    }

    [Fact]
    public void Other_protocols_are_not_clamped()
    {
        var cache = new CacheSettings { Transfers = 32, Checkers = 48 };

        var (transfers, checkers) = RcloneArguments.ConcurrencyFor(cache, StorageProtocol.S3);

        Assert.Equal(32, transfers);
        Assert.Equal(48, checkers);
    }

    [Fact]
    public void Graph_upload_chunks_are_rounded_to_a_multiple_the_api_accepts()
    {
        var mapping = DriveMapping();
        mapping.Cache.UploadChunkSizeMb = 17; // not a multiple of 320 KiB

        var args = RcloneArguments.BuildMount(mapping, StorageProtocol.Graph).ToList();
        var index = args.IndexOf("--onedrive-chunk-size");

        Assert.True(index >= 0);
        // Graph and Drive reject a resumable session whose chunk size is not a multiple of 320 KiB;
        // any whole multiple of 10 MiB satisfies that.
        var value = int.Parse(args[index + 1].Replace("Mi", string.Empty));
        Assert.Equal(0, value % 10);
    }

    [Fact]
    public void Read_ahead_only_applies_when_whole_files_are_cached()
    {
        var mapping = DriveMapping();
        mapping.Cache.CacheMode = VfsCacheMode.Writes;

        Assert.DoesNotContain("--vfs-read-ahead", RcloneArguments.BuildMount(mapping, StorageProtocol.S3));

        mapping.Cache.CacheMode = VfsCacheMode.Full;
        Assert.Contains("--vfs-read-ahead", RcloneArguments.BuildMount(mapping, StorageProtocol.S3));
    }
}
