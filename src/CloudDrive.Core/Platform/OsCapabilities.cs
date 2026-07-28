using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace CloudDrive.Core.Platform;

/// <summary>
/// What this particular Windows install can actually do.
///
/// CloudDrive supports Windows 10 1607 through Windows 11, and Windows Server 2016 through 2025 —
/// but not uniformly, and the differences are detected rather than assumed. Every probe here answers
/// a question that an OS version number answers badly or not at all.
/// </summary>
[SupportedOSPlatform("windows")]
public static class OsCapabilities
{
    private static readonly Lazy<bool> FilesOnDemand = new(ProbeCloudFilesApi);
    private static readonly Lazy<bool> ServerCore = new(ProbeServerCore);
    private static readonly Lazy<string> Edition = new(ReadEdition);

    /// <summary>The build number, e.g. 14393 for Server 2016, 26100 for Windows 11 24H2.</summary>
    public static int BuildNumber => Environment.OSVersion.Version.Build;

    /// <summary>
    /// Whether the Windows Cloud Files API is present, which is what Files On-Demand folders need.
    ///
    /// <para>This is a <i>probe</i>, deliberately, and not a version check. <c>cldapi.dll</c> shipped
    /// with Windows 10 1709 (build 16299); Windows Server 2016 is build 14393 and does not have it,
    /// so on that SKU the on-demand mapping mode simply cannot work. Microsoft's own
    /// <c>CfRegisterSyncRoot</c> page lists "Minimum supported server: Windows Server 2016", but that
    /// row is a documentation-template default rather than a claim about the DLL — trusting it would
    /// mean shipping a mode that fails at first use on exactly the OS the user was told it
    /// supported.</para>
    ///
    /// <para>Asking the loader whether the DLL exists and exports the entry point is cheap, is right
    /// on every SKU including ones that do not exist yet, and does not depend on anybody's
    /// documentation being accurate.</para>
    /// </summary>
    public static bool SupportsFilesOnDemand => FilesOnDemand.Value;

    /// <summary>
    /// True on Server Core, which has no Explorer and no WPF. The tray app cannot run there at all,
    /// so the CLI is the only management surface — which is why there is one.
    /// </summary>
    public static bool IsServerCore => ServerCore.Value;

    /// <summary>Product name from the registry, e.g. "Windows Server 2016 Standard".</summary>
    public static string EditionName => Edition.Value;

    /// <summary>
    /// Why Files On-Demand is unavailable, phrased for a user, or null when it is available. Shown
    /// instead of the mapping mode rather than letting the option fail when clicked.
    /// </summary>
    public static string? FilesOnDemandUnavailableReason =>
        SupportsFilesOnDemand
            ? null
            : $"Files On-Demand needs the Windows Cloud Files API, which arrived in Windows 10 1709 "
              + $"and Windows Server 2019. This machine is {EditionName} (build {BuildNumber}), so "
              + "mappings here have to use a drive letter or a folder mountpoint instead.";

    /// <summary>
    /// Loads <c>cldapi.dll</c> and looks for a real export.
    ///
    /// Both halves matter. A file-existence check would pass on a system where the DLL is present but
    /// the feature is disabled, and checking the export confirms the loader can actually bind to it.
    /// The handle is deliberately not freed: the library is loaded again moments later by the
    /// on-demand engine on any machine where this returns true, and keeping the reference avoids an
    /// unload/reload cycle.
    /// </summary>
    private static bool ProbeCloudFilesApi()
    {
        try
        {
            if (!NativeLibrary.TryLoad("cldapi.dll", out var handle)) return false;
            return NativeLibrary.TryGetExport(handle, "CfRegisterSyncRoot", out _);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Detects Server Core by the absence of the Server-Gui-Shell feature, which is recorded under
    /// the servicing key. Checking for explorer.exe would be less reliable: the file can be present
    /// on an install where the shell is not.
    /// </summary>
    private static bool ProbeServerCore()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Server\ServerLevels");
            if (key is null) return false; // Not a server SKU at all.

            // ServerCore is present on every server install; Server-Gui-Shell only on Desktop
            // Experience. The presence of the former alone is what identifies Core.
            var hasCore = key.GetValue("ServerCore") is not null;
            var hasShell = key.GetValue("Server-Gui-Shell") is not null;
            return hasCore && !hasShell;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadEdition()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string;
            var display = key?.GetValue("DisplayVersion") as string;
            if (string.IsNullOrWhiteSpace(product)) return Environment.OSVersion.VersionString;
            return string.IsNullOrWhiteSpace(display) ? product : $"{product} {display}";
        }
        catch
        {
            return Environment.OSVersion.VersionString;
        }
    }
}
