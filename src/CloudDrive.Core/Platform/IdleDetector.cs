using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CloudDrive.Core.Models;

namespace CloudDrive.Core.Platform;

/// <summary>Why the machine is not considered idle, or that it is.</summary>
/// <param name="IsIdle">True when every gate passed.</param>
/// <param name="Reason">A user-facing explanation when it is not.</param>
public sealed record IdleVerdict(bool IsIdle, string? Reason)
{
    public static readonly IdleVerdict Idle = new(true, null);

    public static IdleVerdict Busy(string reason) => new(false, reason);
}

/// <summary>
/// Decides whether it is safe to apply an update.
///
/// Applying one means unmounting every drive, so "idle" has to mean more than "the screensaver is
/// on". Each gate below rules out a way a user or a job could be mid-something:
///
/// <list type="bullet">
///   <item>a mapping that has moved bytes recently, which is a running copy or backup;</item>
///   <item>a file held open on a mount, which is a document someone is editing;</item>
///   <item>a mapping the user marked as never-interrupt;</item>
///   <item>an interactive user who has touched the keyboard or mouse — skipped entirely when nobody
///         is signed in, which is the normal state on a server and must not block updates forever;</item>
///   <item>the configured maintenance window, when one is set.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public static class IdleDetector
{
    /// <summary>
    /// How long the interactive user has been away from the keyboard and mouse.
    ///
    /// <see cref="TimeSpan.MaxValue"/> when there is no interactive session to ask about — a service
    /// in session 0 gets no input at all, and treating "no input ever" as "never idle" would mean an
    /// unattended server never updated, which is exactly backwards.
    /// </summary>
    public static TimeSpan UserIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.MaxValue;

        // Both values are milliseconds since boot from GetTickCount, which wraps every ~49.7 days.
        // Unsigned subtraction handles the wrap correctly; casting to a signed type first would give
        // a huge negative interval once a machine had been up that long.
        var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    /// <summary>True when a user is signed in to an interactive session on this machine.</summary>
    public static bool HasInteractiveSession => Environment.UserInteractive && ProcessSessionId() != 0;

    private static int ProcessSessionId()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().SessionId; }
        catch { return 0; }
    }

    /// <summary>
    /// Evaluates every gate and returns the first failure, so the caller can log or alert with a
    /// reason rather than an unexplained "not now".
    /// </summary>
    /// <param name="settings">The idle window and maintenance window to apply.</param>
    /// <param name="mountPoints">Live mount points to check for open handles and recent writes.</param>
    /// <param name="protectedMappings">Names of mounted mappings flagged never-interrupt.</param>
    /// <param name="now">Local time, injectable for tests.</param>
    public static IdleVerdict Evaluate(
        UpdateSettings settings,
        IReadOnlyCollection<string> mountPoints,
        IReadOnlyCollection<string> protectedMappings,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mountPoints);
        ArgumentNullException.ThrowIfNull(protectedMappings);

        if (protectedMappings.Count > 0)
        {
            return IdleVerdict.Busy(
                $"'{string.Join("', '", protectedMappings)}' {(protectedMappings.Count == 1 ? "is" : "are")} "
                + "mounted and marked as never to be interrupted by an update.");
        }

        if (!InMaintenanceWindow(settings, now, out var windowReason))
            return IdleVerdict.Busy(windowReason!);

        var window = TimeSpan.FromMinutes(Math.Max(1, settings.IdleMinutesBeforeInstall));

        foreach (var mountPoint in mountPoints)
        {
            if (RecentlyWritten(mountPoint, window, now))
                return IdleVerdict.Busy($"{mountPoint} was written to within the last {window.TotalMinutes:0} minutes.");
        }

        // Only gate on user input when there is a user. On a server with nobody signed in this is
        // skipped, which is the whole point of an unattended update.
        if (HasInteractiveSession)
        {
            var idle = UserIdleTime();
            if (idle < window)
                return IdleVerdict.Busy(
                    $"Someone is using this machine; it has been idle for {idle.TotalMinutes:0} of the "
                    + $"required {window.TotalMinutes:0} minutes.");
        }

        return IdleVerdict.Idle;
    }

    /// <summary>
    /// Whether anything under the mount root changed recently.
    ///
    /// Only the root's own timestamp and its immediate children are checked. Walking the whole tree
    /// would mean listing every object on the remote — an expensive, billable operation to run every
    /// few minutes, and one that would itself count as activity.
    /// </summary>
    private static bool RecentlyWritten(string mountPoint, TimeSpan window, DateTime now)
    {
        try
        {
            var root = mountPoint.EndsWith(':') ? mountPoint + Path.DirectorySeparatorChar : mountPoint;
            if (!Directory.Exists(root)) return false;

            var cutoff = now.ToUniversalTime() - window;
            if (Directory.GetLastWriteTimeUtc(root) > cutoff) return true;

            return new DirectoryInfo(root)
                .EnumerateFileSystemInfos()
                .Any(entry => entry.LastWriteTimeUtc > cutoff);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot tell, so assume busy. Skipping an update cycle is recoverable; unmounting a
            // drive underneath a running job is not.
            return true;
        }
    }

    /// <summary>
    /// Whether <paramref name="now"/> falls inside the configured maintenance window. No window
    /// configured means any time is fine. A window that crosses midnight (22:00–04:00) is handled.
    /// </summary>
    internal static bool InMaintenanceWindow(UpdateSettings settings, DateTime now, out string? reason)
    {
        reason = null;

        if (!TryParseTime(settings.MaintenanceWindowStart, out var start)
            || !TryParseTime(settings.MaintenanceWindowEnd, out var end))
        {
            return true;
        }

        var current = now.TimeOfDay;
        var inside = start <= end
            ? current >= start && current < end
            : current >= start || current < end; // wraps past midnight

        if (!inside)
        {
            reason = $"Outside the maintenance window ({settings.MaintenanceWindowStart}–"
                     + $"{settings.MaintenanceWindowEnd}).";
        }
        return inside;
    }

    private static bool TryParseTime(string? value, out TimeSpan time)
    {
        time = default;
        return !string.IsNullOrWhiteSpace(value)
               && TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out time);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
