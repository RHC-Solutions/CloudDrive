using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CloudDrive.Core.Stores;

/// <summary>
/// Where CloudDrive keeps its state.
///
/// There are two roots and the split is not arbitrary. The **machine store** under
/// <c>%ProgramData%\CloudDrive</c> is the single source of truth: accounts, mappings, settings and
/// secrets all live there, owned by the service. The **user store** under
/// <c>%LOCALAPPDATA%\CloudDrive</c> holds only what is genuinely per-user and per-session — the
/// sync state of Files On-Demand roots, which run in the user's session because a Cloud Files sync
/// root has no session-0 equivalent, plus window geometry.
///
/// Both source projects put everything in the user store and copied a subset to ProgramData for the
/// service. That meant the configuration existed twice and a second Windows account got a second,
/// unrelated set of mappings. Inverting it is what "must not depend on a Windows user" actually
/// requires.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppPaths
{
    public const string ProductName = "CloudDrive";

    // ---------------------------------------------------------------- Machine store -----------

    /// <summary>The service-owned root. ACL'd to SYSTEM and Administrators.</summary>
    public static string MachineDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName);

    public static string AccountsFile => Path.Combine(MachineDir, "accounts.json");

    public static string MappingsFile => Path.Combine(MachineDir, "mappings.json");

    public static string SettingsFile => Path.Combine(MachineDir, "settings.json");

    /// <summary>Every secret: account credentials and notification tokens alike.</summary>
    public static string CredentialsFile => Path.Combine(MachineDir, "credentials.dat");

    public static string MachineLogsDir => Path.Combine(MachineDir, "logs");

    /// <summary>Undelivered alerts, so one survives a service restart or a network outage.</summary>
    public static string SpoolDir => Path.Combine(MachineDir, "spool");

    /// <summary>Managed third-party tools. See <c>ToolManager</c>.</summary>
    public static string ToolsDir => Path.Combine(MachineDir, "tools");

    /// <summary>
    /// The one directory added to <c>PATH</c>. It holds shims pointing at whichever version of each
    /// tool is current, so the <c>PATH</c> entry never has to change when a tool updates.
    /// </summary>
    public static string ToolsBinDir => Path.Combine(ToolsDir, "bin");

    /// <summary>Staging for downloaded updates, cleared once applied.</summary>
    public static string UpdateStagingDir => Path.Combine(MachineDir, "updates");

    // ---------------------------------------------------------------- User store --------------

    public static string UserDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    public static string UserLogsDir => Path.Combine(UserDir, "logs");

    /// <summary>Per-mapping sync state for on-demand folders, which are inherently per-user.</summary>
    public static string SyncStateDir => Path.Combine(UserDir, "sync");

    /// <summary>Window geometry and other UI preferences. Losing this file costs nothing.</summary>
    public static string UiStateFile => Path.Combine(UserDir, "ui.json");

    /// <summary>Default location for a Files On-Demand folder: visible in the user's profile.</summary>
    public static string DefaultOnDemandRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ProductName);

    // ---------------------------------------------------------------- Creation ----------------

    /// <summary>
    /// Creates the machine store and locks it down. Needs elevation, so it is called by the
    /// installer and by the service, never by the unelevated tray app.
    /// </summary>
    public static void EnsureMachineStore()
    {
        var existed = Directory.Exists(MachineDir);
        Directory.CreateDirectory(MachineDir);
        Directory.CreateDirectory(MachineLogsDir);
        Directory.CreateDirectory(SpoolDir);
        Directory.CreateDirectory(ToolsDir);
        Directory.CreateDirectory(ToolsBinDir);

        // Only on first creation. Re-applying on every start would stamp on a deliberate ACL change
        // an administrator made, and would cost a security-descriptor write on every service boot.
        if (!existed) TryRestrictToAdministrators(MachineDir);
    }

    public static void EnsureUserStore()
    {
        Directory.CreateDirectory(UserDir);
        Directory.CreateDirectory(UserLogsDir);
        Directory.CreateDirectory(SyncStateDir);
    }

    /// <summary>
    /// True when <paramref name="path"/> really is inside the machine store.
    ///
    /// The credential store's ACL hardening keys off this. Tests and unusual deployments point the
    /// stores elsewhere, and locking an arbitrary directory to SYSTEM + Administrators would be both
    /// surprising and — for a non-elevated caller — a way to lock itself out of its own temp folder.
    /// </summary>
    public static bool IsInMachineStore(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(MachineDir).TrimEnd(Path.DirectorySeparatorChar);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Strips inherited permissions and grants only SYSTEM and Administrators.
    ///
    /// <c>%ProgramData%</c> grants Users write access by default, which would let any account edit
    /// what the service mounts — and, since a mapping names a mount point, redirect a mount onto a
    /// path of the attacker's choosing. Best-effort: it needs ownership or WRITE_DAC, which the
    /// service and an elevated installer both have and a standard user does not. A standard user
    /// also cannot create this directory in the first place, so failing quietly is correct.
    /// </summary>
    public static void TryRestrictToAdministrators(string path)
    {
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            new DirectoryInfo(path).SetAccessControl(security);
        }
        catch
        {
            // Not fatal: the credential file sets its own ACL regardless.
        }
    }
}
