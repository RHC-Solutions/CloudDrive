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

    /// <summary>True when <paramref name="directory"/> is already on the machine PATH.</summary>
    public static bool Contains(string directory)
    {
        var current = ReadRaw();
        return Split(current).Any(p => SamePath(p, directory));
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
        using var key = Registry.LocalMachine.OpenSubKey(EnvironmentKey, writable: false);
        return key?.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string ?? string.Empty;
    }

    private static void WriteRaw(string value)
    {
        using var key = Registry.LocalMachine.OpenSubKey(EnvironmentKey, writable: true)
            ?? throw new UnauthorizedAccessException(
                "Editing the system PATH needs administrator rights.");

        // REG_EXPAND_SZ, not REG_SZ. Writing the wrong type here would stop Windows expanding the
        // %SystemRoot% entries that were already in the value, breaking PATH for the whole machine.
        key.SetValue("Path", value, RegistryValueKind.ExpandString);
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
