using System.Runtime.Versioning;
using System.Security.Principal;

namespace CloudDrive.Core.Platform;

/// <summary>
/// What this process is running as.
///
/// The distinction that matters throughout CloudDrive is not "is the user an administrator" but "does
/// this <i>token</i> carry administrator rights right now". On a machine with UAC, an account in the
/// Administrators group runs with a filtered token that does <b>not</b> satisfy an Administrators ACE.
/// Confusing the two produces exactly the bug this type exists to prevent: code that hardens a
/// directory to SYSTEM + Administrators, believes it still has access because the user is an admin,
/// and then cannot read its own files.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessIdentity
{
    private static readonly Lazy<(bool Elevated, bool System, string Name)> Current = new(Probe);

    /// <summary>
    /// True when this token actually carries administrator rights — not merely when the user could
    /// elevate. This is the question a file ACL answers with.
    /// </summary>
    public static bool IsElevated => Current.Value.Elevated;

    /// <summary>True when running as LocalSystem, which is how the Windows service runs.</summary>
    public static bool IsLocalSystem => Current.Value.System;

    /// <summary><c>DOMAIN\user</c>, for log lines and error messages.</summary>
    public static string Name => Current.Value.Name;

    /// <summary>
    /// True when this process may write to a directory ACL'd to SYSTEM and Administrators — which is
    /// the precondition for touching the machine store at all.
    /// </summary>
    public static bool CanWriteMachineStore => IsElevated || IsLocalSystem;

    private static (bool, bool, string) Probe()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);

            var isSystem = identity.IsSystem
                           || string.Equals(identity.User?.Value, "S-1-5-18", StringComparison.Ordinal);

            return (principal.IsInRole(WindowsBuiltInRole.Administrator), isSystem, identity.Name);
        }
        catch
        {
            return (false, false, "unknown");
        }
    }
}
