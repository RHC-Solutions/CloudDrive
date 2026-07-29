using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace CloudDrive.Core.Platform;

/// <summary>
/// Makes a mounted drive look and behave like a local Windows disk rather than like a FUSE mount
/// wearing a drive letter.
///
/// Dropping <c>--network-mode</c> (see <c>RcloneArguments</c>) is what puts the drive under "Devices
/// and drives"; this class deals with the two consequences of that choice which Windows does not
/// handle for us — the Recycle Bin a fixed disk gets, and the generic drive icon it gets.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriveAppearance
{
    private const string DriveIconsKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons";

    private const string BitBucketVolumeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume";

    /// <summary>
    /// Gives drive <paramref name="driveLetter"/> CloudDrive's icon and label in Explorer.
    ///
    /// This is the same mechanism Google Drive uses to brand its letter. It writes to HKLM, so it
    /// needs elevation and applies to every user on the machine — which is what a service-hosted
    /// mount wants, since the drive is visible from every session anyway.
    /// </summary>
    /// <param name="driveLetter">A single letter, with or without a colon.</param>
    /// <param name="iconPath">Path to an .ico or an "exe,index" reference.</param>
    /// <param name="label">Volume label, or null to leave Explorer showing rclone's.</param>
    public static void SetDriveIcon(string driveLetter, string iconPath, string? label = null)
    {
        var letter = NormalizeLetter(driveLetter);
        if (letter is null || string.IsNullOrWhiteSpace(iconPath)) return;

        // HKLM when this process can write it, HKCU otherwise, because Explorer honours both.
        //
        // This used to write HKLM unconditionally, which a standard user cannot do -- so every mount in
        // a user's own session logged "Drive presentation could not be applied: Access to the registry
        // key ... is denied" and silently got no icon. HKCU is in fact the more correct target for a
        // session mount: the drive belongs to that user, so its branding should too. A serviced mount
        // runs as LocalSystem, where HKCU is SYSTEM's own hive and would brand the drive for nobody, so
        // that case still needs HKLM and has the rights for it.
        var hive = ProcessIdentity.CanWriteMachineStore ? Registry.LocalMachine : Registry.CurrentUser;

        using var icons = hive.CreateSubKey($@"{DriveIconsKey}\{letter}\DefaultIcon");
        icons?.SetValue(null, iconPath, RegistryValueKind.String);

        if (!string.IsNullOrWhiteSpace(label))
        {
            using var labelKey = hive.CreateSubKey($@"{DriveIconsKey}\{letter}\DefaultLabel");
            labelKey?.SetValue(null, label, RegistryValueKind.String);
        }
    }

    /// <summary>
    /// Removes the icon and label for a letter, so a drive that is no longer CloudDrive's does not
    /// keep its branding. Called on unmount and on uninstall.
    /// </summary>
    public static void ClearDriveIcon(string driveLetter)
    {
        var letter = NormalizeLetter(driveLetter);
        if (letter is null) return;

        // Both hives, because which one SetDriveIcon used depends on the privileges it had at the time,
        // and a leftover entry would brand whatever drive next takes the letter.
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var icons = hive.OpenSubKey(DriveIconsKey, writable: true);
                icons?.DeleteSubKeyTree(letter, throwOnMissingSubKey: false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                // No rights to this hive. Cosmetic, so not worth failing an unmount over.
            }
        }
    }

    /// <summary>
    /// Stops Windows routing deletes on this volume through a Recycle Bin.
    ///
    /// <para>A fixed disk gets a <c>$RECYCLE.BIN</c>; a network drive never does. That is the one
    /// genuine downside of presenting the mount as a local disk, and it is not cosmetic: deleting a
    /// file becomes a server-side <i>copy</i> into a hidden folder on the remote, so the bytes are
    /// still there and still being billed, indefinitely, and the user believes they freed the
    /// space.</para>
    ///
    /// <para><b>Scope caveat, because it is real.</b> The Recycle Bin policy is per volume <i>and per
    /// user</i>, under HKCU. A service running as LocalSystem can only write its own hive, which
    /// does nothing for the interactive users who will actually delete files. So this is called from
    /// the tray app for the signed-in user, and <see cref="PurgeRecycleBin"/> is the backstop that
    /// works no matter who deleted what.</para>
    /// </summary>
    /// <returns>True when the policy was written.</returns>
    public static bool SetNukeOnDelete(string mountPoint)
    {
        var volumeGuid = TryGetVolumeGuid(mountPoint);
        if (volumeGuid is null) return false;

        // The BitBucket key names the volume by its GUID with the braces kept and the "\\?\Volume"
        // prefix and trailing slash removed.
        var keyName = volumeGuid
            .Replace(@"\\?\Volume", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('\\');

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{BitBucketVolumeKey}\{keyName}");
            if (key is null) return false;
            key.SetValue("NukeOnDelete", 1, RegistryValueKind.DWord);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes any <c>$RECYCLE.BIN</c> sitting at the root of the mount.
    ///
    /// The backstop for everything <see cref="SetNukeOnDelete"/> cannot reach: a second user on the
    /// machine, a delete that happened before the policy was applied, or a Windows build that
    /// recreates the folder anyway. Cheap to call — the folder almost never exists — and it runs
    /// against the remote, so failures are expected and swallowed.
    /// </summary>
    /// <returns>Bytes reclaimed, or 0 if there was nothing to do.</returns>
    public static long PurgeRecycleBin(string mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint)) return 0;

        var root = mountPoint.EndsWith(':') ? mountPoint + Path.DirectorySeparatorChar : mountPoint;
        var bin = Path.Combine(root, "$RECYCLE.BIN");

        try
        {
            if (!Directory.Exists(bin)) return 0;

            long freed = 0;
            foreach (var file in Directory.EnumerateFiles(bin, "*", SearchOption.AllDirectories))
            {
                try { freed += new FileInfo(file).Length; }
                catch { /* raced with something else; the size is only for reporting */ }
            }

            Directory.Delete(bin, recursive: true);
            return freed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The remote may refuse, or the folder may be in use. The next sweep retries.
            return 0;
        }
    }

    /// <summary>
    /// The volume GUID path for a mount point, e.g. <c>\\?\Volume{…}\</c>. Null when the path is not
    /// a mounted volume — which is the normal answer while a mount is still coming up.
    /// </summary>
    public static string? TryGetVolumeGuid(string mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint)) return null;

        // The API insists on a trailing backslash and rejects the path without one.
        var path = mountPoint.EndsWith(Path.DirectorySeparatorChar)
            ? mountPoint
            : mountPoint + Path.DirectorySeparatorChar;

        var buffer = new StringBuilder(64);
        return GetVolumeNameForVolumeMountPointW(path, buffer, (uint)buffer.Capacity)
            ? buffer.ToString()
            : null;
    }

    /// <summary>A bare uppercase letter with no colon, or null when the input is not one letter.</summary>
    private static string? NormalizeLetter(string? driveLetter)
    {
        var trimmed = (driveLetter ?? string.Empty).Trim().TrimEnd(':', '\\', '/');
        return trimmed.Length == 1 && char.IsAsciiLetter(trimmed[0])
            ? trimmed.ToUpperInvariant()
            : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string lpszVolumeMountPoint, StringBuilder lpszVolumeName, uint cchBufferLength);
}
