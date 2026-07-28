using System.Runtime.Versioning;
using Microsoft.Win32;

namespace CloudDrive.Core.Platform;

/// <summary>
/// Detects WinFsp, the filesystem driver rclone mounts drive letters through.
///
/// Without it, <c>rclone mount</c> fails with a message about a missing DLL that means nothing to a
/// user. Detecting it up front turns that into a banner offering to install it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WinFsp
{
    private static readonly Lazy<string?> InstallPath = new(Locate);

    /// <summary>Where WinFsp is installed, or null when it is not.</summary>
    public static string? Path => InstallPath.Value;

    public static bool IsInstalled => InstallPath.Value is not null;

    /// <summary>
    /// The installed version, read from the registry. Null when WinFsp is absent or the value is
    /// missing, which some older installers leave out.
    /// </summary>
    public static string? Version
    {
        get
        {
            try
            {
                using var key = OpenKey();
                return key?.GetValue("Version") as string;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// WinFsp records itself under the 32-bit view of the registry even on x64, because the
    /// installer is 32-bit. Reading the native view finds nothing on a 64-bit machine, which looks
    /// exactly like "not installed" — so the 32-bit view is checked first and explicitly.
    /// </summary>
    private static RegistryKey? OpenKey()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            var key = hive.OpenSubKey(@"SOFTWARE\WinFsp");
            if (key is not null) return key;
        }
        return null;
    }

    private static string? Locate()
    {
        try
        {
            using var key = OpenKey();
            if (key?.GetValue("InstallDir") is string dir && Directory.Exists(dir)) return dir;
        }
        catch
        {
            // Fall through to the filesystem check.
        }

        // A registry entry can survive an uninstall, and a repair install can leave the files
        // without rewriting the key. The DLL rclone actually loads is the thing that matters.
        foreach (var candidate in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                 })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            var dir = System.IO.Path.Combine(candidate, "WinFsp");
            if (File.Exists(System.IO.Path.Combine(dir, "bin", "winfsp-x64.dll"))) return dir;
        }

        return null;
    }
}
