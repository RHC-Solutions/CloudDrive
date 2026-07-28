using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace CloudDrive.Core.Tooling;

/// <summary>
/// Adds and removes CloudDrive's managed tools directory on the machine <c>PATH</c>.
///
/// Machine scope rather than user scope, for the same reason the configuration lives in
/// <c>%ProgramData%</c>: the service has no user hive, and a tool that only exists on one user's
/// PATH is not much use to a process running as LocalSystem.
///
/// <para><b>Editing the system PATH is a footgun.</b> It is a single string shared by everything on
/// the machine, and the classic way to destroy it is to read it with
/// <see cref="Environment.GetEnvironmentVariable(string, EnvironmentVariableTarget)"/>, which
/// <i>expands</i> embedded <c>%SystemRoot%</c>-style references, and then write the expanded result
/// back — silently baking in values that were meant to stay dynamic. This class reads the raw
/// unexpanded string straight out of the registry instead, and only ever appends or removes one
/// entry.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemPath
{
    private const string EnvironmentKey =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>
    /// True when <paramref name="directory"/> is already on the machine PATH.
    ///
    /// Returns false rather than throwing when the machine environment key cannot be read. This is
    /// informational — it drives a "(on PATH)" label — and a process that cannot read HKLM should get
    /// a tools listing without that detail rather than an error instead of the listing, which is what
    /// happened before: the whole <c>tools list</c> command failed with "Requested registry access is
    /// not allowed".
    /// </summary>
    public static bool Contains(string directory)
    {
        try
        {
            return Split(ReadRaw()).Any(p => SamePath(p, directory));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Appends <paramref name="directory"/> if it is not there already, and tells running processes.
    /// Requires elevation.
    /// </summary>
    /// <returns>True when the PATH was changed.</returns>
    public static bool Add(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var current = ReadRaw();
        var entries = Split(current).ToList();
        if (entries.Any(p => SamePath(p, directory))) return false;

        entries.Add(directory.TrimEnd(Path.DirectorySeparatorChar));
        WriteRaw(string.Join(';', entries));
        Broadcast();
        return true;
    }

    /// <summary>Removes <paramref name="directory"/> from the machine PATH. Used by the uninstaller.</summary>
    /// <returns>True when the PATH was changed.</returns>
    public static bool Remove(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var entries = Split(ReadRaw()).ToList();
        var kept = entries.Where(p => !SamePath(p, directory)).ToList();
        if (kept.Count == entries.Count) return false;

        WriteRaw(string.Join(';', kept));
        Broadcast();
        return true;
    }

    /// <summary>
    /// Reads PATH as stored, without expanding <c>%SystemRoot%</c> and friends.
    ///
    /// <see cref="RegistryValueOptions.DoNotExpandEnvironmentNames"/> is the whole point of this
    /// method: without it, writing back what we read would replace every dynamic reference with
    /// whatever it happened to expand to at that moment, permanently.
    /// </summary>
    private static string ReadRaw()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(EnvironmentKey, writable: false);
            return key?.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)
                as string ?? string.Empty;
        }
        catch (System.Security.SecurityException ex)
        {
            // Normalised so every caller has one exception type to reason about; Contains swallows it,
            // Add and Remove let it surface as an actionable message.
            throw new UnauthorizedAccessException(
                "Reading the system PATH was denied. This needs administrator rights.", ex);
        }
    }

    private static void WriteRaw(string value)
    {
        RegistryKey? key;
        try
        {
            key = Registry.LocalMachine.OpenSubKey(EnvironmentKey, writable: true);
        }
        catch (System.Security.SecurityException ex)
        {
            // Opening HKLM for write throws SecurityException, not UnauthorizedAccessException, when
            // the token lacks the rights. Normalising it means callers have one exception type to
            // handle rather than discovering the second one in production.
            throw new UnauthorizedAccessException(
                "Editing the system PATH needs administrator rights.", ex);
        }

        if (key is null)
            throw new UnauthorizedAccessException("Editing the system PATH needs administrator rights.");

        using (key)
        {
            // REG_EXPAND_SZ, not REG_SZ. Writing the wrong type would stop Windows expanding the
            // %SystemRoot% entries already in the value, breaking PATH for the whole machine.
            key.SetValue("Path", value, RegistryValueKind.ExpandString);
        }
    }

    private static IEnumerable<string> Split(string path) =>
        path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // An unexpanded %VAR% entry is not a valid path; fall back to a literal comparison
            // rather than throwing and aborting the whole PATH edit.
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Tells already-running processes that the environment changed, so a new shell picks up the
    /// PATH without a reboot. Explorer rebroadcasts it to anything it launches afterwards.
    /// Best-effort and time-limited: a hung window must not stall the installer.
    /// </summary>
    private static void Broadcast()
    {
        try
        {
            SendMessageTimeout(
                HwndBroadcast, WmSettingChange, UIntPtr.Zero, "Environment",
                SmtoAbortIfHung, 2000, out _);
        }
        catch
        {
            // Purely a convenience; the PATH is already written.
        }
    }

    private static readonly IntPtr HwndBroadcast = new(0xffff);
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);
}
