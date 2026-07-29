using System.Diagnostics;
using CloudDrive.Core.Models;
using CloudDrive.Core.Mounting;

namespace CloudDrive.Tests;

/// <summary>
/// Checks every flag CloudDrive passes to <c>rclone mount</c> against the flags rclone actually accepts.
///
/// <para>This exists because of a bug the whole existing test suite was structurally unable to catch.
/// CloudDrive emitted <c>--file-system-name NTFS</c> to make a disk-mode drive report itself as NTFS.
/// That flag does not exist: <c>FileSystemName</c> is a <i>WinFsp</i> option and has to be forwarded
/// through <c>-o</c>. rclone rejected the command line before doing anything, so **every drive-letter
/// mount failed**, and all the user saw was "rclone exited unexpectedly (code 2)".</para>
///
/// <para>The existing argument tests asserted that the builder emitted the flags the builder intended to
/// emit — they agreed with the code because they encoded the same assumption. Nothing compared the
/// output against the tool that has to consume it. This does, by asking rclone for its own flag list.</para>
///
/// <para>Skipped when <c>third_party\rclone\rclone.exe</c> is absent, so a fresh clone still goes green;
/// run <c>scripts\fetch-tools.ps1</c> to get the real coverage.</para>
/// </summary>
public class RcloneFlagValidityTests
{
    private static string? FindRclone()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "third_party", "rclone", "rclone.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Every flag rclone accepts for a mount, from both places rclone documents them.
    ///
    /// <para>Neither source alone is complete, and each omission produces a test that lies in a
    /// different direction. <c>mount --help</c> lists mount and VFS flags but few global or backend
    /// ones, so <c>--checkers</c> and <c>--s3-chunk-size</c> look invalid. <c>help flags</c> lists
    /// global and backend flags but not the VFS group, so <c>--vfs-cache-mode</c> looks invalid. Both
    /// were tried, and both failed on correct code before this used the union.</para>
    ///
    /// <para>The union still excludes the flag that started this — <c>--file-system-name</c> appears in
    /// neither, which is verified below rather than assumed.</para>
    /// </summary>
    private static HashSet<string> KnownFlags(string rclone)
    {
        var flags = ReadFlags(rclone, "mount", "--help");
        flags.UnionWith(ReadFlags(rclone, "help", "flags"));
        return flags;
    }

    private static HashSet<string> ReadFlags(string rclone, params string[] arguments)
    {
        var psi = new ProcessStartInfo(rclone)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)!;
        var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(20_000);

        // Listed as "      --flag-name Type   description", sometimes "  -o, --option Type ...".
        var flags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var match in System.Text.RegularExpressions.Regex.Matches(text, @"(?<!\S)(--[a-z0-9][a-z0-9-]*)")
                     .Cast<System.Text.RegularExpressions.Match>())
        {
            flags.Add(match.Groups[1].Value);
        }

        // Short forms are not in the long-flag sweep above.
        foreach (var shortFlag in System.Text.RegularExpressions.Regex.Matches(text, @"(?<!\S)(-[a-zA-Z]),")
                     .Cast<System.Text.RegularExpressions.Match>())
        {
            flags.Add(shortFlag.Groups[1].Value);
        }

        return flags;
    }

    private static Mapping DriveMapping(bool networkMode = false, bool readOnly = false) => new()
    {
        Name = "Drive",
        Container = "bucket",
        Mode = MappingMode.DriveLetter,
        MountTarget = MountTarget.DriveLetter,
        DriveLetter = "X",
        PresentAsNetworkDrive = networkMode,
        ReadOnly = readOnly,
    };

    public static TheoryData<string, StorageProtocol, bool, bool> Cases() => new()
    {
        { "s3 disk mode",        StorageProtocol.S3,      false, false },
        { "s3 network mode",     StorageProtocol.S3,      true,  false },
        { "s3 read-only",        StorageProtocol.S3,      false, true  },
        { "sftp",                StorageProtocol.Sftp,    false, false },
        { "smb",                 StorageProtocol.Smb,     false, false },
        { "webdav",              StorageProtocol.WebDav,  false, false },
        { "ftp",                 StorageProtocol.Ftp,     false, false },
    };

    /// <summary>
    /// The assertion that matters: rclone recognises every flag we pass, for every protocol and every
    /// presentation mode.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_flag_is_one_rclone_accepts(
        string label, StorageProtocol protocol, bool networkMode, bool readOnly)
    {
        var rclone = FindRclone();
        if (rclone is null) return; // see the class remarks

        var known = KnownFlags(rclone);
        // Sanity-check the oracle itself: a parse that silently yields nothing would make this test
        // pass for any input at all.
        // Sanity-check the oracle before trusting it. A parse that silently yields nothing, or that
        // misses a whole flag group, would make this test pass for any input at all.
        Assert.True(known.Count > 500, $"rclone's flag list did not parse (found {known.Count}).");
        Assert.Contains("--vfs-cache-mode", known);   // the VFS group, from mount --help
        Assert.Contains("--checkers", known);         // the global group, from help flags
        Assert.DoesNotContain("--file-system-name", known);

        var args = RcloneArguments.BuildMount(DriveMapping(networkMode, readOnly), protocol);

        var unknown = args
            .Where(a => a.StartsWith('-') && a.Length > 1)
            // A negative number would be a value, not a flag.
            .Where(a => !double.TryParse(a, out _))
            .Where(a => !known.Contains(a))
            .ToList();

        Assert.True(unknown.Count == 0,
            $"{label}: rclone does not accept {string.Join(", ", unknown)}. "
            + "A flag that rclone rejects makes it exit before mounting anything.");
    }

    /// <summary>
    /// Disk mode forwards FileSystemName through <c>-o</c>, which is a WinFsp option rather than an
    /// rclone flag. Pinned explicitly because getting this wrong broke every drive mount and the
    /// symptom — "rclone exited unexpectedly" — named nothing useful.
    /// </summary>
    [Fact]
    public void Disk_mode_passes_the_filesystem_name_as_a_winfsp_option()
    {
        var args = RcloneArguments.BuildMount(DriveMapping(), StorageProtocol.S3).ToList();

        var index = args.IndexOf("-o");
        Assert.True(index >= 0, "disk mode should forward a WinFsp option");
        Assert.Equal("FileSystemName=NTFS", args[index + 1]);

        Assert.DoesNotContain("--file-system-name", args);
    }

    [Fact]
    public void Network_mode_asks_for_network_mode_and_sets_no_filesystem_name()
    {
        // A network drive is presented by Windows, so naming the filesystem is neither needed nor
        // meaningful there.
        var args = RcloneArguments.BuildMount(DriveMapping(networkMode: true), StorageProtocol.S3);

        Assert.Contains("--network-mode", args);
        Assert.DoesNotContain("FileSystemName=NTFS", args);
    }
}
