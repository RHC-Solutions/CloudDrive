using System.Runtime.Versioning;

namespace CloudDrive.CloudFiles;

/// <summary>
/// Brands an on-demand folder in Explorer by writing a <c>desktop.ini</c> that sets the folder's
/// icon (to the RHC Solutions icon embedded in the app exe) and a tooltip. This is the safe,
/// per-folder form of branding; a pinned left-sidebar entry would require additional shell
/// namespace registration.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FolderBranding
{
    /// <summary>
    /// Writes desktop.ini pointing the folder icon at <paramref name="iconResource"/>
    /// (e.g. "C:\...\CloudDrive.exe,0"). Safe to call repeatedly.
    /// </summary>
    public static void Apply(string folderPath, string iconResource, string? tooltip = null)
    {
        if (!Directory.Exists(folderPath)) return;

        var iniPath = Path.Combine(folderPath, "desktop.ini");
        var lines = new List<string>
        {
            "[.ShellClassInfo]",
            $"IconResource={iconResource}",
            "ConfirmFileOp=0",
        };
        if (!string.IsNullOrWhiteSpace(tooltip))
            lines.Add($"InfoTip={tooltip}");

        try
        {
            File.WriteAllLines(iniPath, lines);
            // desktop.ini must be Hidden+System, and the folder needs the System (or ReadOnly)
            // attribute for Explorer to honour it.
            File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System);
            var dir = new DirectoryInfo(folderPath);
            dir.Attributes |= FileAttributes.System;
        }
        catch
        {
            // Branding is cosmetic; never fail the mount over it.
        }
    }
}
