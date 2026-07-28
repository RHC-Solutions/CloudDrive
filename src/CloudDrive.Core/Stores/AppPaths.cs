using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using CloudDrive.Core.Platform;

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

    /// <summary>
    /// Environment variable that relocates the machine store.
    ///
    /// Exists so the service host can be run from a console, and tested, without elevation. The real
    /// store is ACL'd to SYSTEM and Administrators, which is correct for production and makes an
    /// unelevated end-to-end run impossible otherwise.
    /// </summary>
    public const string DataDirVariable = "CLOUDDRIVE_DATA_DIR";

    /// <summary>The service-owned root. ACL'd to SYSTEM and Administrators.</summary>
    public static string MachineDir { get; } = ResolveMachineDir();

    /// <summary>True when the store has been redirected away from <c>%ProgramData%</c> for a test.</summary>
    public static bool MachineDirIsRedirected { get; private set; }

    private static string ResolveMachineDir()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            try
            {
                var full = Path.GetFullPath(overridden.Trim());
                MachineDirIsRedirected = true;
                return full;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A malformed override falls back to the real location rather than failing to start:
                // a typo in an environment variable should not stop the service mounting anything.
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName);
    }

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
    /// Creates the machine store, hardening its ACL when this process is entitled to.
    ///
    /// <para><b>The hardening is conditional, and that is a bug fix rather than a relaxation.</b>
    /// Restricting the directory to SYSTEM and Administrators locks out any token that does not
    /// satisfy that ACL — including a UAC-filtered token belonging to a user who <i>is</i> in the
    /// Administrators group. Doing it unconditionally meant an unelevated process could create the
    /// store, harden it, and then be unable to read what it had just written, leaving behind a
    /// directory it could neither use nor delete. So the ACL is applied only when running as SYSTEM
    /// or genuinely elevated; otherwise the store inherits <c>%ProgramData%</c>'s permissions and the
    /// first elevated run tightens it.</para>
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">
    /// The store exists but this process cannot use it. Thrown with an explanation rather than
    /// letting a bare access-denied surface from somewhere deeper.
    /// </exception>
    public static void EnsureMachineStore()
    {
        var existed = Directory.Exists(MachineDir);

        try
        {
            Directory.CreateDirectory(MachineDir);
            Directory.CreateDirectory(MachineLogsDir);
            Directory.CreateDirectory(SpoolDir);
            Directory.CreateDirectory(ToolsDir);
            Directory.CreateDirectory(ToolsBinDir);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(DescribeAccessFailure(), ex);
        }

        // Only on first creation, and only when this process is entitled. Re-applying on every start
        // would also stamp on a deliberate ACL change an administrator had made.
        if (!existed && ProcessIdentity.CanWriteMachineStore && !MachineDirIsRedirected)
            TryRestrictToAdministrators(MachineDir);
    }

    /// <summary>
    /// Whether the machine store is usable by this process, and why not when it is not. Used as a
    /// preflight so a failure is reported once, clearly, instead of as an access-denied from
    /// whichever store happened to be touched first.
    /// </summary>
    public static string? DescribeMachineStoreProblem()
    {
        try
        {
            if (!Directory.Exists(MachineDir)) return null; // it will be created

            // Existence is not access. A probe write is the only reliable check, because the ACL may
            // grant traversal but not creation.
            var probe = Path.Combine(MachineDir, ".access-probe");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return DescribeAccessFailure();
        }
    }

    private static string DescribeAccessFailure() =>
        $"""
         CloudDrive cannot write to its configuration directory:
           {MachineDir}

         That directory is restricted to SYSTEM and Administrators, and this process is running as
         {ProcessIdentity.Name}{(ProcessIdentity.IsElevated ? " (elevated)" : " (not elevated)")}.

         The Windows service runs as LocalSystem and is unaffected. To run the service host directly
         for troubleshooting, either start an elevated prompt, or point it at a scratch directory:

           $env:{DataDirVariable} = "$env:LOCALAPPDATA\CloudDrive-test"
         """;

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
