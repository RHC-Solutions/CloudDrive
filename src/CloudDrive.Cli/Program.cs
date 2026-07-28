using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using CloudDrive.Core.Models;
using CloudDrive.Core.Platform;
using CloudDrive.Core.Tooling;
using CloudDrive.Ipc;

[assembly: SupportedOSPlatform("windows")]

namespace CloudDrive.Cli;

/// <summary>
/// The command-line surface.
///
/// On Server Core there is no desktop shell, so WPF cannot run and this is the *only* way to
/// configure CloudDrive. It is therefore held to the same standard as the UI rather than treated as
/// a debugging aid: everything the window can do, this can do, and it talks to the service over the
/// same pipe with the same authorisation rules.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "status" => await StatusAsync().ConfigureAwait(false),
                "list" => await ListAsync(args).ConfigureAwait(false),
                "mount" => await MountAsync(args, mount: true).ConfigureAwait(false),
                "unmount" => await MountAsync(args, mount: false).ConfigureAwait(false),
                "remount-all" => await SimpleAsync(IpcOperation.RemountAll, "Remounting everything.").ConfigureAwait(false),
                "service" => Service(args),
                "tools" => await ToolsAsync(args).ConfigureAwait(false),
                "update" => await UpdateAsync(args).ConfigureAwait(false),
                "log" => await LogAsync(args).ConfigureAwait(false),
                "info" => Info(),
                _ => Unknown(args[0]),
            };
        }
        catch (ServiceUnavailableException ex)
        {
            Error(ex.Message);
            return 3;
        }
        catch (IpcException ex)
        {
            Error(ex.Message);
            return 4;
        }
        catch (Exception ex)
        {
            Error(ex.Message);
            return 1;
        }
    }

    private static bool IsHelp(string arg) =>
        arg is "-h" or "--help" or "/?" or "help";

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            CloudDrive {UpdateService.CurrentVersion}
            Mount cloud and server storage as native Windows drives.

              cdrive status                  What is mounted right now
              cdrive list [accounts]         Mappings, or accounts
              cdrive mount <name|id>         Mount one mapping
              cdrive unmount <name|id>       Unmount one mapping
              cdrive remount-all             Unmount everything and reconcile

              cdrive service <verb>          install | uninstall | start | stop | restart | status
              cdrive tools <verb>            list | check | install <id> | rollback <id> | path
              cdrive update [check|install]  Look for a new release, or apply the pending one
              cdrive log [lines]             Recent service log (default 100)
              cdrive info                    What this machine supports

            Configuration changes require an elevated prompt, because they control what the
            machine-wide service mounts. Accounts and mappings are created in the CloudDrive window;
            on Server Core, edit %ProgramData%\CloudDrive\mappings.json and the service will
            converge on it.
            """);
    }

    private static int Unknown(string verb)
    {
        Error($"Unknown command '{verb}'. Run 'cdrive --help'.");
        return 2;
    }

    // ---------------------------------------------------------------- Connection --------------

    private static async Task<IpcClient> ConnectAsync()
    {
        var client = new IpcClient();
        await client.ConnectAsync().ConfigureAwait(false);
        return client;
    }

    // ---------------------------------------------------------------- Commands ----------------

    private static async Task<int> StatusAsync()
    {
        await using var client = await ConnectAsync().ConfigureAwait(false);
        var state = await client.CallAsync<ServiceSnapshot>(IpcOperation.GetState).ConfigureAwait(false);
        if (state is null) { Error("The service returned nothing."); return 1; }

        Console.WriteLine($"CloudDrive service {state.ServiceVersion} on {Environment.MachineName}");
        Console.WriteLine();

        if (state.Mappings.Count == 0)
        {
            Console.WriteLine("No mappings are configured.");
        }
        else
        {
            var accounts = state.Accounts.ToDictionary(a => a.Id);
            var mounts = state.Mounts.ToDictionary(m => m.MappingId);

            Console.WriteLine($"{"NAME",-22} {"ACCOUNT",-18} {"WHERE",-14} {"STATUS",-12} DETAIL");
            foreach (var mapping in state.Mappings.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var account = accounts.GetValueOrDefault(mapping.AccountId);
                var mount = mounts.GetValueOrDefault(mapping.Id);
                var status = mount?.State.ToString() ?? "Unmounted";
                var detail = mount?.Message ?? string.Empty;

                Console.WriteLine(
                    $"{Trim(mapping.Name, 22),-22} {Trim(account?.Name ?? "(missing)", 18),-18} "
                    + $"{Trim(mapping.Mode == MappingMode.OnDemandFolder ? "on-demand" : mapping.MountPoint, 14),-14} "
                    + $"{status,-12} {Trim(detail, 40)}");
            }
        }

        if (state.Warnings.Count > 0)
        {
            Console.WriteLine();
            foreach (var warning in state.Warnings) Warn(warning);
        }

        return state.Mounts.Any(m => m.State == MountState.Error) ? 5 : 0;
    }

    private static async Task<int> ListAsync(string[] args)
    {
        await using var client = await ConnectAsync().ConfigureAwait(false);
        var state = await client.CallAsync<ServiceSnapshot>(IpcOperation.GetState).ConfigureAwait(false);
        if (state is null) return 1;

        var wantAccounts = args.Length > 1 && args[1].StartsWith("acc", StringComparison.OrdinalIgnoreCase);

        if (wantAccounts)
        {
            Console.WriteLine($"{"NAME",-24} {"PROVIDER",-24} {"ID",-34} DETAILS");
            foreach (var a in state.Accounts.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Console.WriteLine(
                    $"{Trim(a.Name, 24),-24} {Trim(a.Descriptor.DisplayName, 24),-24} "
                    + $"{a.Id,-34} {Trim(a.Summary, 40)}");
            }
            return 0;
        }

        Console.WriteLine($"{"NAME",-24} {"MODE",-12} {"HOST",-10} {"ID",-34} REMOTE");
        foreach (var m in state.Mappings.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Console.WriteLine(
                $"{Trim(m.Name, 24),-24} {(m.Mode == MappingMode.OnDemandFolder ? "on-demand" : "drive"),-12} "
                + $"{(m.Host == MountHost.Service ? "service" : "session"),-10} {m.Id,-34} {Trim(m.RemoteDescription, 30)}");
        }
        return 0;
    }

    private static async Task<int> MountAsync(string[] args, bool mount)
    {
        if (args.Length < 2)
        {
            Error($"Which mapping? Usage: clouddrive {(mount ? "mount" : "unmount")} <name|id>");
            return 2;
        }

        await using var client = await ConnectAsync().ConfigureAwait(false);
        var state = await client.CallAsync<ServiceSnapshot>(IpcOperation.GetState).ConfigureAwait(false);
        if (state is null) return 1;

        var mapping = Resolve(state, args[1]);
        if (mapping is null) return 6;

        if (mapping.Mode == MappingMode.OnDemandFolder)
        {
            Error($"'{mapping.Name}' is a Files On-Demand folder. Those run in a desktop session, "
                  + "so they are mounted from the CloudDrive window rather than from here.");
            return 7;
        }

        Console.WriteLine($"{(mount ? "Mounting" : "Unmounting")} '{mapping.Name}'…");
        await client.CallAsync(
            mount ? IpcOperation.Mount : IpcOperation.Unmount,
            new MountRequest { MappingId = mapping.Id }).ConfigureAwait(false);

        Console.WriteLine($"'{mapping.Name}' {(mount ? "mounted at " + mapping.MountPoint : "unmounted")}.");
        return 0;
    }

    /// <summary>
    /// Finds a mapping by exact id, exact name, or unambiguous prefix.
    ///
    /// An ambiguous prefix is an error rather than a guess: picking the first match would eventually
    /// unmount the wrong drive, and doing so silently is worse than refusing.
    /// </summary>
    private static Mapping? Resolve(ServiceSnapshot state, string token)
    {
        if (Guid.TryParse(token, out var id))
        {
            var byId = state.Mappings.FirstOrDefault(m => m.Id == id);
            if (byId is not null) return byId;
            Error($"No mapping has the id {id}.");
            return null;
        }

        var exact = state.Mappings
            .Where(m => string.Equals(m.Name, token, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        if (exact.Count == 1) return exact[0];

        var partial = state.Mappings
            .Where(m => m.Name.Contains(token, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        switch (partial.Count)
        {
            case 1:
                return partial[0];
            case 0:
                Error($"No mapping matches '{token}'. Run 'cdrive list' to see them.");
                return null;
            default:
                Error($"'{token}' matches {partial.Count} mappings: {string.Join(", ", partial.Select(m => m.Name))}. "
                      + "Be more specific, or use the id.");
                return null;
        }
    }

    private static async Task<int> SimpleAsync(IpcOperation operation, string message)
    {
        await using var client = await ConnectAsync().ConfigureAwait(false);
        Console.WriteLine(message);
        await client.CallAsync(operation).ConfigureAwait(false);
        Console.WriteLine("Done.");
        return 0;
    }

    // ---------------------------------------------------------------- Service -----------------

    private static int Service(string[] args)
    {
        var verb = args.Length > 1 ? args[1].ToLowerInvariant() : "status";

        if (verb is "install" or "uninstall" or "start" or "stop" or "restart" && !IsElevated())
        {
            Error($"'cdrive service {verb}' needs an elevated prompt.");
            return 8;
        }

        var timeout = TimeSpan.FromSeconds(45);

        switch (verb)
        {
            case "install":
                var exe = ServiceControl.ResolveServiceExe();
                if (exe is null)
                {
                    Error("CloudDrive.Service.exe was not found next to this executable.");
                    return 9;
                }
                ServiceControl.Install(exe);
                ServiceControl.Start(timeout);
                Console.WriteLine($"Installed and started the CloudDrive service from {exe}.");
                return 0;

            case "uninstall":
                ServiceControl.Uninstall();
                Console.WriteLine("Removed the CloudDrive service. Configuration in %ProgramData% was left alone.");
                return 0;

            case "start": ServiceControl.Start(timeout); Console.WriteLine("Started."); return 0;
            case "stop": ServiceControl.Stop(timeout); Console.WriteLine("Stopped."); return 0;
            case "restart": ServiceControl.Restart(timeout); Console.WriteLine("Restarted."); return 0;

            case "status":
                Console.WriteLine($"CloudDrive service: {ServiceControl.GetState()}");
                return 0;

            default:
                Error($"Unknown service verb '{verb}'. Use install, uninstall, start, stop, restart or status.");
                return 2;
        }
    }

    // ---------------------------------------------------------------- Tools -------------------

    private static async Task<int> ToolsAsync(string[] args)
    {
        var verb = args.Length > 1 ? args[1].ToLowerInvariant() : "list";

        if (verb == "path")
        {
            var binDir = Core.Stores.AppPaths.ToolsBinDir;
            var action = args.Length > 2 ? args[2].ToLowerInvariant() : "show";

            // --register and --unregister are what the installer calls. Editing the machine PATH
            // safely means reading the raw REG_EXPAND_SZ value without expanding it, which Inno
            // Setup's [Registry] section cannot do — writing back an expanded copy would bake
            // %SystemRoot% into the machine PATH permanently.
            if (action is "--register" or "--unregister")
            {
                if (!IsElevated())
                {
                    Error("Editing the system PATH needs an elevated prompt.");
                    return 8;
                }

                if (action == "--register")
                {
                    Directory.CreateDirectory(binDir);
                    Console.WriteLine(SystemPath.Add(binDir)
                        ? $"Added {binDir} to the system PATH."
                        : $"{binDir} was already on the system PATH.");
                }
                else
                {
                    Console.WriteLine(SystemPath.Remove(binDir)
                        ? $"Removed {binDir} from the system PATH."
                        : $"{binDir} was not on the system PATH.");
                }
                return 0;
            }

            Console.WriteLine(binDir);
            Console.WriteLine(SystemPath.Contains(binDir)
                ? "This directory is on the system PATH."
                : "This directory is NOT on the system PATH. Add it with: cdrive tools path --register");
            return 0;
        }

        await using var client = await ConnectAsync().ConfigureAwait(false);

        switch (verb)
        {
            case "list":
            {
                var state = await client.CallAsync<ToolStateResult>(IpcOperation.GetToolState).ConfigureAwait(false);
                if (state is null) return 1;

                Console.WriteLine($"Tools directory: {state.ToolsDirectory}"
                                  + (state.OnSystemPath ? " (on PATH)" : " (not on PATH)"));
                Console.WriteLine();
                Console.WriteLine($"{"TOOL",-14} {"INSTALLED",-14} {"REQUIRED",-10} PURPOSE");
                foreach (var t in state.Tools)
                {
                    Console.WriteLine(
                        $"{t.DisplayName,-14} {t.InstalledVersion ?? "-",-14} "
                        + $"{(t.Required ? "yes" : "no"),-10} {Trim(t.Purpose, 60)}");
                }
                return 0;
            }

            case "check":
            {
                Console.WriteLine("Checking each vendor…");
                var updates = await client.CallAsync<List<ToolInfo>>(IpcOperation.CheckToolUpdates)
                    .ConfigureAwait(false) ?? [];
                if (updates.Count == 0) { Console.WriteLine("Every tool is up to date."); return 0; }

                foreach (var u in updates)
                    Console.WriteLine($"{u.DisplayName}: {u.InstalledVersion ?? "not installed"} → {u.AvailableVersion}");
                return 0;
            }

            case "install":
            {
                if (args.Length < 3) { Error("Which tool? e.g. cdrive tools install rclone"); return 2; }
                Console.WriteLine($"Installing {args[2]}…");
                var version = await client.CallAsync<string>(IpcOperation.InstallTool, args[2]).ConfigureAwait(false);
                Console.WriteLine($"Installed {args[2]} {version}.");
                return 0;
            }

            case "rollback":
            {
                if (args.Length < 3) { Error("Which tool? e.g. cdrive tools rollback rclone"); return 2; }
                await client.CallAsync(IpcOperation.RollbackTool, args[2]).ConfigureAwait(false);
                Console.WriteLine($"Rolled {args[2]} back to the previous version.");
                return 0;
            }

            default:
                Error($"Unknown tools verb '{verb}'. Use list, check, install, rollback or path.");
                return 2;
        }
    }

    // ---------------------------------------------------------------- Update ------------------

    private static async Task<int> UpdateAsync(string[] args)
    {
        var verb = args.Length > 1 ? args[1].ToLowerInvariant() : "check";
        await using var client = await ConnectAsync().ConfigureAwait(false);

        if (verb == "install")
        {
            Console.WriteLine("Applying the pending update. Mounts will drop and come back.");
            await client.CallAsync(IpcOperation.InstallUpdate).ConfigureAwait(false);
            return 0;
        }

        Console.WriteLine("Checking for updates…");
        var result = await client.CallAsync<UpdateCheckResult>(IpcOperation.CheckForUpdate).ConfigureAwait(false);
        if (result is null) return 1;

        if (!result.UpdateAvailable)
        {
            Console.WriteLine($"Up to date: version {result.CurrentVersion}.");
            return 0;
        }

        Console.WriteLine($"CloudDrive {result.AvailableVersion} is available (running {result.CurrentVersion}).");
        if (result.ReleaseUrl is not null) Console.WriteLine(result.ReleaseUrl);
        Console.WriteLine(result.DeferredReason is null
            ? "It will install automatically once this machine is idle."
            : $"Waiting: {result.DeferredReason}");
        return 0;
    }

    private static async Task<int> LogAsync(string[] args)
    {
        var lines = args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var n) ? n : 100;

        await using var client = await ConnectAsync().ConfigureAwait(false);
        var tail = await client.CallAsync<LogTailResult>(
            IpcOperation.GetLogTail, new LogTailRequest { Lines = lines }).ConfigureAwait(false);

        foreach (var line in tail?.Lines ?? []) Console.WriteLine(line);
        return 0;
    }

    private static int Info()
    {
        Console.WriteLine($"CloudDrive       {UpdateService.CurrentVersion}");
        Console.WriteLine($"Windows          {OsCapabilities.EditionName} (build {OsCapabilities.BuildNumber})");
        Console.WriteLine($"Server Core      {(OsCapabilities.IsServerCore ? "yes" : "no")}");
        Console.WriteLine($"WinFsp           {(WinFsp.IsInstalled ? WinFsp.Version ?? "installed" : "not installed")}");
        Console.WriteLine($"Service          {ServiceControl.GetState()}");
        Console.WriteLine($"Elevated         {(IsElevated() ? "yes" : "no")}");
        Console.WriteLine();
        Console.WriteLine(OsCapabilities.SupportsFilesOnDemand
            ? "Files On-Demand  available"
            : $"Files On-Demand  unavailable\n                 {OsCapabilities.FilesOnDemandUnavailableReason}");
        return 0;
    }

    // ---------------------------------------------------------------- Helpers -----------------

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static string Trim(string? value, int max)
    {
        value ??= string.Empty;
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    /// <summary>Writes to stderr, so a caller can separate diagnostics from output it is parsing.</summary>
    private static void Error(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(message);
        Console.ForegroundColor = previous;
    }

    private static void Warn(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("! " + message);
        Console.ForegroundColor = previous;
    }
}
