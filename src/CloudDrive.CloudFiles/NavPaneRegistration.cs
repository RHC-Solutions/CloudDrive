using System.Runtime.Versioning;
using Microsoft.Win32;

namespace CloudDrive.CloudFiles;

/// <summary>
/// Adds (and removes) a pinned entry for an on-demand folder in Explorer's navigation pane — the
/// OneDrive-style sidebar item with a custom icon. Uses the documented per-user shell "namespace
/// extension delegating to a filesystem folder" recipe, written entirely under HKCU so it is fully
/// reversible. No packaging/admin required.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NavPaneRegistration
{
    // The shell "file folder" delegate CLSID that makes our namespace node behave like a folder.
    private const string FileFolderDelegate = "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}";

    private static string Clsid(Guid id) => "{" + id.ToString("D").ToUpperInvariant() + "}";

    /// <summary>Registers the pinned nav-pane entry. Safe to call repeatedly (idempotent).</summary>
    public static void Register(Guid id, string displayName, string targetFolderPath, string iconResource)
    {
        var clsid = Clsid(id);
        try
        {
            using (var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsid}"))
            {
                root!.SetValue(null, displayName);
                root.SetValue("System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);
                root.SetValue("SortOrderIndex", 0x42, RegistryValueKind.DWord);
            }
            using (var icon = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsid}\DefaultIcon"))
                icon!.SetValue(null, iconResource, RegistryValueKind.ExpandString);
            using (var inproc = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsid}\InProcServer32"))
                inproc!.SetValue(null, @"%SystemRoot%\system32\shell32.dll", RegistryValueKind.ExpandString);
            using (var instance = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsid}\Instance"))
                instance!.SetValue("CLSID", FileFolderDelegate);
            using (var bag = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsid}\Instance\InitPropertyBag"))
            {
                bag!.SetValue("Attributes", 0x11, RegistryValueKind.DWord);
                bag.SetValue("TargetFolderPath", targetFolderPath, RegistryValueKind.ExpandString);
            }
            using (var sf = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsid}\ShellFolder"))
            {
                sf!.SetValue("FolderValueFlags", 0x28, RegistryValueKind.DWord);
                sf.SetValue("Attributes", unchecked((int)0xF080004D), RegistryValueKind.DWord);
            }
            // Show it in the navigation pane tree.
            using (var ns = Registry.CurrentUser.CreateSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{clsid}"))
                ns!.SetValue(null, displayName);
            // Keep it off the desktop.
            using (var hide = Registry.CurrentUser.CreateSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel"))
                hide!.SetValue(clsid, 1, RegistryValueKind.DWord);
        }
        catch
        {
            // Sidebar branding is cosmetic; never fail the mount over it.
        }
    }

    /// <summary>Removes the nav-pane entry. Safe to call when it was never registered.</summary>
    public static void Unregister(Guid id)
    {
        var clsid = Clsid(id);
        Delete($@"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{clsid}");
        Delete($@"Software\Classes\CLSID\{clsid}");
        try
        {
            using var hide = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", writable: true);
            hide?.DeleteValue(clsid, throwOnMissingValue: false);
        }
        catch { /* ignore */ }
    }

    private static void Delete(string subKey)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); }
        catch { /* ignore */ }
    }
}
