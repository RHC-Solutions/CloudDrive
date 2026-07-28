using System.Runtime.Versioning;
using Microsoft.Win32;

namespace CloudDrive.Core.Platform;

/// <summary>
/// Registers the tray app to start at sign-in, via <c>HKCU\…\CurrentVersion\Run</c>.
///
/// <para><b>This has to be done by the app, not by the installer.</b> The key is per-user, and the
/// installer runs elevated — so an <c>HKCU</c> write from it lands in the hive of whichever
/// administrator approved the UAC prompt, which on a managed machine is frequently not the person who
/// will actually use CloudDrive. Inno Setup warns about exactly this. The tray app runs unelevated as
/// the real user, so it is the only thing positioned to get it right.</para>
///
/// <para>No elevation is needed, and none of this touches the Windows service: the service is
/// registered separately and starts at boot regardless of who signs in.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Value name under Run. Also what an administrator would look for to remove it by hand.</summary>
    public const string ValueName = "CloudDrive";

    /// <summary>The command currently registered to run at sign-in, or null when there is none.</summary>
    public static string? RegisteredCommand
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(ValueName) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(RegisteredCommand);

    /// <summary>
    /// Brings the Run key in line with <paramref name="enabled"/>, using <paramref name="exePath"/>.
    ///
    /// Idempotent, and it rewrites the value when the path has changed — otherwise an upgrade that
    /// moved the executable would leave a Run entry pointing at somewhere that no longer exists, and
    /// the app would silently stop starting at sign-in.
    /// </summary>
    /// <returns>True when the registry was changed.</returns>
    public static bool Apply(bool enabled, string? exePath = null)
    {
        var command = Quote(exePath ?? Environment.ProcessPath);

        try
        {
            if (!enabled)
            {
                if (!IsEnabled) return false;
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            if (command is null) return false;
            if (string.Equals(RegisteredCommand, command, StringComparison.OrdinalIgnoreCase)) return false;

            using var writable = Registry.CurrentUser.CreateSubKey(RunKey);
            writable?.SetValue(ValueName, command, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // A locked-down profile can deny this. Not worth failing anything over — the app still
            // works, it just will not launch itself.
            return false;
        }
    }

    /// <summary>
    /// Quotes the path, because <c>Run</c> values are parsed as command lines and an unquoted path
    /// containing a space is read as a command plus arguments.
    /// </summary>
    private static string? Quote(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : $"\"{path}\"";
}
