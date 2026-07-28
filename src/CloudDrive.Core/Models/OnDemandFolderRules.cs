namespace CloudDrive.Core.Models;

/// <summary>
/// Safety rules for the location of an on-demand mapping's folder.
///
/// A Cloud Files sync root takes over the folder it is registered at: every file beneath it becomes
/// a placeholder owned by the provider, and remote deletions propagate down. Pointing one at the
/// wrong folder is therefore destructive rather than merely wrong, and Windows refuses some
/// locations outright. These checks run before the folder is created, so a bad choice is rejected in
/// the dialog instead of failing (or eating files) at mount time.
/// </summary>
public static class OnDemandFolderRules
{
    /// <summary>
    /// Returns an error message if <paramref name="path"/> is an unsafe sync-root location, or null
    /// if it is acceptable. A blank path means "use the default under the user profile", which is
    /// always allowed. <paramref name="otherFolders"/> are the folders other mappings already use.
    /// </summary>
    public static string? Validate(string? path, IEnumerable<string>? otherFolders = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var trimmed = path.Trim();

        // Check the raw input: GetFullPath would resolve a relative path against the current
        // directory, which is never what the user meant here.
        if (!Path.IsPathFullyQualified(trimmed))
            return @"Enter a full path, for example D:\Hetzner\Backups.";

        string full;
        try { full = Path.GetFullPath(trimmed); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "That folder path isn't valid.";
        }

        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) && PathsEqual(full, root))
            return "Choose a folder inside the drive, not the drive root — the folder you pick becomes "
                 + "the sync root and everything in it is taken over.";

        foreach (var reserved in ReservedFolders())
        {
            if (PathsEqual(full, reserved))
                return $"'{full}' is a Windows system folder and can't be used. Choose another location.";
        }

        if (FindCloudRoot(full) is { } cloud)
            return $"That location is inside {cloud}. Windows can't nest one on-demand folder inside "
                 + "another — pick somewhere outside it.";

        if (otherFolders is not null)
        {
            foreach (var other in otherFolders)
            {
                if (string.IsNullOrWhiteSpace(other)) continue;
                string otherFull;
                try { otherFull = Path.GetFullPath(other.Trim()); } catch { continue; }
                if (PathsOverlap(full, otherFull))
                    return $"That location overlaps another mapping's folder ('{otherFull}'). "
                         + "Each mapping needs its own folder.";
            }
        }

        return null;
    }

    /// <summary>
    /// True when the folder already exists and holds something. Not an error — the caller should
    /// confirm with the user, because registering a sync root over existing files converts them
    /// into placeholders and lets remote deletions remove them.
    /// </summary>
    public static bool HasExistingContent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path.Trim());
            return Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any();
        }
        catch { return false; }
    }

    /// <summary>
    /// Combines a parent directory with a mapping name to produce a sync-root path, mirroring how
    /// OneDrive creates a named folder inside the location you choose rather than taking the
    /// location itself.
    /// </summary>
    public static string CombineForMapping(string parentDirectory, string mappingName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        var leaf = Sanitize(mappingName);
        if (leaf.Length == 0) leaf = "CloudDrive";
        return Path.Combine(parentDirectory.Trim(), leaf);
    }

    /// <summary>Strips characters Windows won't accept in a folder name.</summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Where(c => !invalid.Contains(c)).ToArray());
        return cleaned.TrimEnd('.', ' ');
    }

    private static IEnumerable<string> ReservedFolders()
    {
        var ids = new[]
        {
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData,
        };
        foreach (var id in ids)
        {
            string p;
            try { p = Environment.GetFolderPath(id); } catch { continue; }
            if (!string.IsNullOrWhiteSpace(p)) yield return p;
        }
    }

    /// <summary>
    /// Names the cloud-storage root containing <paramref name="full"/>, if any. Detected from the
    /// environment variables the OneDrive client sets, which is enough to catch the common mistake
    /// of putting a Hetzner folder inside OneDrive (the two would fight over the same files).
    /// </summary>
    private static string? FindCloudRoot(string full)
    {
        var candidates = new (string Var, string Label)[]
        {
            ("OneDriveCommercial", "OneDrive for Business"),
            ("OneDriveConsumer", "OneDrive"),
            ("OneDrive", "OneDrive"),
        };
        foreach (var (variable, label) in candidates)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value)) continue;
            string resolved;
            try { resolved = Path.GetFullPath(value); } catch { continue; }
            if (PathsEqual(full, resolved) || IsUnder(full, resolved)) return label;
        }
        return null;
    }

    private static bool PathsOverlap(string a, string b) =>
        PathsEqual(a, b) || IsUnder(a, b) || IsUnder(b, a);

    private static bool IsUnder(string child, string parent)
    {
        var p = Normalize(parent);
        var c = Normalize(child);
        return c.Length > p.Length
            && c.StartsWith(p, StringComparison.OrdinalIgnoreCase)
            && (c[p.Length] == Path.DirectorySeparatorChar || c[p.Length] == Path.AltDirectorySeparatorChar);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Trailing separators are insignificant, so they are stripped — except on a drive root, where
    /// "D:" and "D:\" must normalize to the same thing or a root would never compare equal to itself.
    /// </summary>
    private static string Normalize(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0) return path;
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + Path.DirectorySeparatorChar;
        return trimmed;
    }
}
