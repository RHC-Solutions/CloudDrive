using System.Runtime.Versioning;
using CloudDrive.Core;
using CloudDrive.Core.Models;
using CloudDrive.Core.Platform;
using CloudDrive.Core.Stores;
using CloudDrive.Core.Tooling;
using CloudDrive.Ipc;
using CloudDrive.Notifications;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Service;

/// <summary>
/// Handles every IPC operation. This is where authorisation lives.
///
/// The rule is simple and applied at the top of each handler: reading is open to any authenticated
/// user, writing requires administrator rights, and credentials are released only to the user who
/// owns the mapping asking for them. It is deliberately not spread across the layer below — an
/// authorisation check that lives next to the operation is one a reviewer can actually verify.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IpcDispatcher(
    ConfigStore config,
    CredentialStore credentials,
    MountReconciler reconciler,
    AlertDispatcher alerts,
    ToolManager tools,
    UpdateCoordinator updates,
    FileLogger fileLog,
    ILogger logger)
{
    /// <summary>Raised after a change that clients should refetch for.</summary>
    public event Func<string, Task>? ConfigChanged;

    public async Task<object?> DispatchAsync(IpcRequest request, CancellationToken ct) =>
        request.Operation switch
        {
            IpcOperation.Ping => new { Version = UpdateService.CurrentVersion, Ok = true },

            IpcOperation.GetState => GetState(request),
            IpcOperation.GetStatus => reconciler.Snapshot(),
            IpcOperation.GetCapabilities => GetCapabilities(),
            IpcOperation.GetLogTail => GetLogTail(request),
            IpcOperation.GetToolState => await GetToolStateAsync(ct).ConfigureAwait(false),

            IpcOperation.Mount => await MountAsync(request, ct).ConfigureAwait(false),
            IpcOperation.Unmount => await UnmountAsync(request, ct).ConfigureAwait(false),
            IpcOperation.RemountAll => await RemountAllAsync(request, ct).ConfigureAwait(false),

            IpcOperation.SaveAccount => await SaveAccountAsync(request).ConfigureAwait(false),
            IpcOperation.DeleteAccount => await DeleteAccountAsync(request, ct).ConfigureAwait(false),
            IpcOperation.SaveMapping => await SaveMappingAsync(request).ConfigureAwait(false),
            IpcOperation.DeleteMapping => await DeleteMappingAsync(request, ct).ConfigureAwait(false),
            IpcOperation.SaveSettings => await SaveSettingsAsync(request).ConfigureAwait(false),

            IpcOperation.GetSessionCredentials => GetOnDemandCredentials(request),

            IpcOperation.SaveNotificationTarget => await SaveNotificationTargetAsync(request).ConfigureAwait(false),
            IpcOperation.DeleteNotificationTarget => await DeleteNotificationTargetAsync(request).ConfigureAwait(false),
            IpcOperation.SendTestAlert => await SendTestAlertAsync(request, ct).ConfigureAwait(false),

            IpcOperation.CheckForUpdate => await updates.CheckNowAsync(ct).ConfigureAwait(false),
            IpcOperation.InstallUpdate => await InstallUpdateAsync(request, ct).ConfigureAwait(false),
            IpcOperation.SkipUpdate => await SkipUpdateAsync(request).ConfigureAwait(false),
            IpcOperation.CheckToolUpdates => await CheckToolUpdatesAsync(request, ct).ConfigureAwait(false),
            IpcOperation.InstallTool => await InstallToolAsync(request, ct).ConfigureAwait(false),
            IpcOperation.RollbackTool => RollbackTool(request),

            _ => throw new InvalidOperationException($"Unsupported operation '{request.Operation}'."),
        };

    // ---------------------------------------------------------------- Reading -----------------

    private ServiceSnapshot GetState(IpcRequest request)
    {
        var document = config.Load();
        var settings = config.LoadSettings();
        var warnings = new List<string>();
        if (reconciler.UnavailableReason is { } reason) warnings.Add(reason);
        if (!WinFsp.IsInstalled)
            warnings.Add("WinFsp is not installed, so drive-letter mappings cannot mount. "
                         + "Install it from Settings → Tools.");
        foreach (var orphan in document.OrphanedMappings())
            warnings.Add($"Mapping '{orphan.Name}' points at an account that no longer exists.");
        foreach (var account in document.Accounts.Where(a => a.NeedsReauth))
            warnings.Add($"Account '{account.Name}' needs to be signed in again: {account.ReauthRequiredReason}");

        var spooled = alerts.SpoolDepth();
        if (spooled > 0) warnings.Add($"{spooled} alert(s) could not be delivered and are queued for retry.");

        return new ServiceSnapshot
        {
            ServiceVersion = UpdateService.CurrentVersion,
            Accounts = document.Accounts,
            Mappings = document.Mappings,
            Settings = settings,
            Mounts = [.. reconciler.Snapshot()],
            Warnings = warnings,
            CallerIsAdministrator = request.Caller.IsAdministrator,
        };
    }

    private CapabilityReport GetCapabilities()
    {
        var rclone = tools.ResolveRclone();
        return new CapabilityReport
        {
            SupportsFilesOnDemand = OsCapabilities.SupportsFilesOnDemand,
            FilesOnDemandUnavailableReason = OsCapabilities.FilesOnDemandUnavailableReason,
            IsServerCore = OsCapabilities.IsServerCore,
            EditionName = OsCapabilities.EditionName,
            BuildNumber = OsCapabilities.BuildNumber,
            WinFspInstalled = WinFsp.IsInstalled,
            RclonePath = rclone,
            RcloneVersion = tools.InstalledVersion(ToolCatalog.RcloneId),
        };
    }

    private LogTailResult GetLogTail(IpcRequest request)
    {
        var body = request.Body<LogTailRequest>() ?? new LogTailRequest();
        return new LogTailResult { Lines = [.. fileLog.Tail(Math.Clamp(body.Lines, 1, 5000))] };
    }

    private async Task<ToolStateResult> GetToolStateAsync(CancellationToken ct)
    {
        var state = tools.State;

        // Only report an available version when a check has already been done; this call is on the
        // UI's startup path and must not block on the network.
        var result = new ToolStateResult
        {
            LastCheckedUtc = state.LastCheckedUtc,
            ToolsDirectory = AppPaths.ToolsDir,
            OnSystemPath = SystemPath.Contains(AppPaths.ToolsBinDir),
        };

        foreach (var tool in ToolCatalog.All)
        {
            var installed = state.Installed.GetValueOrDefault(tool.Id);
            result.Tools.Add(new ToolInfo
            {
                Id = tool.Id,
                DisplayName = tool.DisplayName,
                Purpose = tool.Purpose,
                InstalledVersion = installed?.Version,
                Required = tool.Required,
                CanRollback = installed?.PreviousVersions.Count > 0,
            });
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return result;
    }

    // ---------------------------------------------------------------- Authorisation -----------

    // Every write used to call RequireAdministrator, which was too blunt and made CloudDrive unusable
    // for a standard user — a regression against both predecessors, which needed no privilege at all
    // because they only ever mounted into the caller's own session.
    //
    // The thing actually worth protecting is narrower: the Windows service runs as LocalSystem, so
    // anyone who can edit a *serviced* mapping can point a SYSTEM process at a path of their choosing.
    // Machine-wide settings, notification targets and tool installs are the same class of problem.
    //
    // Nothing about a mapping that mounts in your own session, from an account you created, escalates
    // anything. Those are authorised by ownership instead.

    /// <summary>
    /// Throws unless the caller may modify <paramref name="account"/>: administrators may change any,
    /// and a standard user may change one they own. An account with no owner is shared, so other users'
    /// mappings may depend on it and changing it needs elevation.
    /// </summary>
    private static void RequireAccountAccess(IpcRequest request, Account? account)
    {
        if (request.Caller.IsAdministrator) return;
        if (account is null) return; // a new account; ownership is stamped on save

        if (account.IsMachineWide)
        {
            throw new UnauthorizedAccessException(
                $"'{account.Name}' is a shared account, so changing it needs administrator rights. "
                + "Accounts you create yourself do not.");
        }

        if (!string.Equals(account.OwnerSid, request.Caller.Sid, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"'{account.Name}' belongs to another user.");
    }

    /// <summary>
    /// Throws unless the caller may modify <paramref name="mapping"/>. A mapping hosted by the service
    /// always needs elevation, because the service acts as LocalSystem. One hosted in a user's own
    /// session needs only that the caller owns it.
    /// </summary>
    private static void RequireMappingAccess(IpcRequest request, Mapping mapping)
    {
        if (request.Caller.IsAdministrator) return;

        if (mapping.Host == MountHost.Service)
        {
            throw new UnauthorizedAccessException(
                "A mapping hosted by the CloudDrive service needs administrator rights, because the "
                + "service runs as LocalSystem and mounts for the whole machine. Choose "
                + "'This sign-in session' to manage it without elevation.");
        }

        if (!string.IsNullOrEmpty(mapping.OwnerSid)
            && !string.Equals(mapping.OwnerSid, request.Caller.Sid, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"'{mapping.Name}' belongs to another user.");
        }
    }

    // ---------------------------------------------------------------- Mount control -----------

    private async Task<object?> MountAsync(IpcRequest request, CancellationToken ct)
    {
        var body = request.Body<MountRequest>() ?? throw new InvalidOperationException("No mapping specified.");
        RequireMappingAccess(request, RequireMapping(body.MappingId));
        await reconciler.MountAsync(body.MappingId, ct).ConfigureAwait(false);
        return null;
    }

    private async Task<object?> UnmountAsync(IpcRequest request, CancellationToken ct)
    {
        var body = request.Body<MountRequest>() ?? throw new InvalidOperationException("No mapping specified.");
        RequireMappingAccess(request, RequireMapping(body.MappingId));
        await reconciler.UnmountAsync(body.MappingId, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>The mapping, or a clear error naming what was asked for.</summary>
    private Mapping RequireMapping(Guid id) =>
        config.Load().FindMapping(id)
        ?? throw new InvalidOperationException($"No mapping with id {id:D} exists.");

    private async Task<object?> RemountAllAsync(IpcRequest request, CancellationToken ct)
    {
        request.RequireAdministrator();
        await reconciler.UnmountAllAsync(ct).ConfigureAwait(false);
        await reconciler.ReconcileAsync(ct).ConfigureAwait(false);
        return null;
    }

    // ---------------------------------------------------------------- Configuration -----------

    private async Task<object?> SaveAccountAsync(IpcRequest request)
    {
        var body = request.Body<SaveAccountRequest>()
                   ?? throw new InvalidOperationException("No account supplied.");

        var account = body.Account;
        if (string.IsNullOrWhiteSpace(account.Name))
            throw new InvalidOperationException("The account needs a name.");

        RequireAccountAccess(request, config.Load().FindAccount(account.Id));

        // A standard user's account is stamped as theirs; an administrator's is shared. Taken from the
        // caller's token rather than from the request, so it cannot be claimed on someone else's behalf.
        if (!request.Caller.IsAdministrator) account.OwnerSid = request.Caller.Sid;

        config.Mutate(document =>
        {
            var existing = document.FindAccount(account.Id);
            if (existing is null) document.Accounts.Add(account);
            else document.Accounts[document.Accounts.IndexOf(existing)] = account;
        });

        // Null credentials means "leave the stored secret alone", which is what makes renaming an
        // account possible without the user retyping a password they may not have to hand.
        if (body.Credentials is not null)
            credentials.SetAccount(account.Id, body.Credentials);

        await NotifyChangedAsync($"Account '{account.Name}' saved.").ConfigureAwait(false);
        return account;
    }

    private async Task<object?> DeleteAccountAsync(IpcRequest request, CancellationToken ct)
    {
        var body = request.Body<DeleteRequest>() ?? throw new InvalidOperationException("No account specified.");
        RequireAccountAccess(request, config.Load().FindAccount(body.Id));

        var removed = config.Mutate(document => document.RemoveAccountCascade(body.Id));

        // Unmount the cascade before forgetting the credentials, or the mounts would keep running
        // with no configuration behind them.
        foreach (var mapping in removed)
            await reconciler.UnmountAsync(mapping.Id, ct).ConfigureAwait(false);

        credentials.RemoveAccount(body.Id);

        await NotifyChangedAsync(
            removed.Count == 0
                ? "Account deleted."
                : $"Account deleted, along with {removed.Count} mapping(s) that used it.")
            .ConfigureAwait(false);
        return null;
    }

    private async Task<object?> SaveMappingAsync(IpcRequest request)
    {
        var body = request.Body<SaveMappingRequest>()
                   ?? throw new InvalidOperationException("No mapping supplied.");
        var mapping = body.Mapping;

        // Both the incoming mapping and whatever it replaces are checked. The first stops a standard
        // user creating a serviced mapping; the second stops them converting someone else's.
        RequireMappingAccess(request, mapping);
        if (config.Load().FindMapping(mapping.Id) is { } previous)
            RequireMappingAccess(request, previous);

        if (!request.Caller.IsAdministrator) mapping.OwnerSid = request.Caller.Sid;

        var document = config.Load();
        var account = document.FindAccount(mapping.AccountId)
            ?? throw new InvalidOperationException("That mapping's account does not exist.");

        var problems = mapping.Validate(account);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(" ", problems));

        if (mapping.Mode == MappingMode.OnDemandFolder && !OsCapabilities.SupportsFilesOnDemand)
            throw new InvalidOperationException(OsCapabilities.FilesOnDemandUnavailableReason!);

        if (document.FindMountPointConflict(mapping) is { } conflict)
            throw new InvalidOperationException(
                $"'{conflict.Name}' already uses {mapping.MountPoint}.");

        // A user-session mapping belongs to whoever created it. Recording the owner is what lets the
        // credential handler below refuse to hand one user's secrets to another.
        if (mapping.Host == MountHost.UserSession && string.IsNullOrWhiteSpace(mapping.OwnerSid))
            mapping.OwnerSid = request.Caller.Sid;

        config.Mutate(d =>
        {
            var existing = d.FindMapping(mapping.Id);
            if (existing is null) d.Mappings.Add(mapping);
            else d.Mappings[d.Mappings.IndexOf(existing)] = mapping;
        });

        await NotifyChangedAsync($"Mapping '{mapping.Name}' saved.").ConfigureAwait(false);
        return mapping;
    }

    private async Task<object?> DeleteMappingAsync(IpcRequest request, CancellationToken ct)
    {
        var body = request.Body<DeleteRequest>() ?? throw new InvalidOperationException("No mapping specified.");
        RequireMappingAccess(request, RequireMapping(body.Id));

        await reconciler.UnmountAsync(body.Id, ct).ConfigureAwait(false);
        config.Mutate(document => document.Mappings.RemoveAll(m => m.Id == body.Id));

        await NotifyChangedAsync("Mapping deleted.").ConfigureAwait(false);
        return null;
    }

    private async Task<object?> SaveSettingsAsync(IpcRequest request)
    {
        request.RequireAdministrator();
        var settings = request.Body<AppSettings>() ?? throw new InvalidOperationException("No settings supplied.");
        config.SaveSettings(settings);
        await NotifyChangedAsync("Settings saved.").ConfigureAwait(false);
        return null;
    }

    // ---------------------------------------------------------------- Credentials -------------

    /// <summary>
    /// Releases an account's credentials so the caller can run a Files On-Demand root in their own
    /// session.
    ///
    /// <para>This is the one operation that hands a secret back over the pipe, so it is fenced on
    /// every side: the mapping must be an on-demand, user-session mapping; the caller must be the
    /// recorded owner, or an administrator. Without the ownership check any standard user could read
    /// the machine-wide configuration, pick someone else's mapping id, and ask the LocalSystem
    /// service to decrypt that person's password for them.</para>
    /// </summary>
    private OnDemandCredentialsResult GetOnDemandCredentials(IpcRequest request)
    {
        var body = request.Body<MountRequest>() ?? throw new InvalidOperationException("No mapping specified.");

        var document = config.Load();
        var mapping = document.FindMapping(body.MappingId)
            ?? throw new InvalidOperationException("That mapping no longer exists.");

        // Any mapping hosted in a user's own session, whichever mode. A session-hosted drive letter is
        // mounted by the tray app rather than by the service, and it needs the same credentials an
        // on-demand root does. A serviced mapping is never released: the service already holds those and
        // handing them to a client would leak a machine-wide secret to whoever asked.
        if (mapping.Host != MountHost.UserSession)
            throw new UnauthorizedAccessException(
                "Credentials are only released for mappings that run in a user session. This one is "
                + "hosted by the service, which uses them itself.");

        var isOwner = string.Equals(mapping.OwnerSid, request.Caller.Sid, StringComparison.Ordinal);
        if (!isOwner && !request.Caller.IsAdministrator)
        {
            logger.LogWarning(
                "{Caller} asked for credentials for mapping '{Mapping}', which belongs to another user.",
                request.Caller.Name, mapping.Name);
            throw new UnauthorizedAccessException("That mapping belongs to another user.");
        }

        var account = document.FindAccount(mapping.AccountId)
            ?? throw new InvalidOperationException("That mapping's account no longer exists.");
        var secret = credentials.GetAccount(account.Id)
            ?? throw new InvalidOperationException($"No credentials are stored for '{account.Name}'.");

        return new OnDemandCredentialsResult { Account = account, Credentials = secret };
    }

    // ---------------------------------------------------------------- Notifications -----------

    private async Task<object?> SaveNotificationTargetAsync(IpcRequest request)
    {
        request.RequireAdministrator();
        var body = request.Body<NotificationTargetRequest>()
                   ?? throw new InvalidOperationException("No target supplied.");

        var secret = body.Secret ?? credentials.GetNotification(body.Target.Id);

        // Validate before saving, so a typo surfaces now rather than when the first real alert is
        // lost at three in the morning.
        var problem = alerts.Validate(body.Target, secret);
        if (problem is not null) throw new InvalidOperationException(problem);

        config.MutateSettings(settings =>
        {
            var targets = settings.Notifications.Targets;
            var existing = targets.FirstOrDefault(t => t.Id == body.Target.Id);
            if (existing is null) targets.Add(body.Target);
            else targets[targets.IndexOf(existing)] = body.Target;
            return true;
        });

        if (body.Secret is not null) credentials.SetNotification(body.Target.Id, body.Secret);

        await NotifyChangedAsync($"Notification target '{body.Target.Name}' saved.").ConfigureAwait(false);
        return null;
    }

    private async Task<object?> DeleteNotificationTargetAsync(IpcRequest request)
    {
        request.RequireAdministrator();
        var body = request.Body<DeleteRequest>() ?? throw new InvalidOperationException("No target specified.");

        config.MutateSettings(settings => settings.Notifications.Targets.RemoveAll(t => t.Id == body.Id));
        credentials.RemoveNotification(body.Id);

        await NotifyChangedAsync("Notification target deleted.").ConfigureAwait(false);
        return null;
    }

    private async Task<object?> SendTestAlertAsync(IpcRequest request, CancellationToken ct)
    {
        request.RequireAdministrator();
        var body = request.Body<NotificationTargetRequest>()
                   ?? throw new InvalidOperationException("No target supplied.");

        var secret = body.Secret ?? credentials.GetNotification(body.Target.Id);
        var alert = Alert.Info(AlertKind.TestMessage,
            "CloudDrive test message",
            $"If you are reading this, alerts from {Environment.MachineName} are working.");

        await alerts.SendDirectAsync(alert, body.Target, secret, ct).ConfigureAwait(false);
        return null;
    }

    // ---------------------------------------------------------------- Updates and tools -------

    private async Task<object?> InstallUpdateAsync(IpcRequest request, CancellationToken ct)
    {
        request.RequireAdministrator();
        await updates.InstallNowAsync(ct).ConfigureAwait(false);
        return null;
    }

    private async Task<object?> SkipUpdateAsync(IpcRequest request)
    {
        request.RequireAdministrator();
        var version = request.Body<string>();
        config.MutateSettings(settings => settings.Updates.SkippedVersion = version);
        await NotifyChangedAsync($"Release {version} will not be offered again.").ConfigureAwait(false);
        return null;
    }

    private async Task<object?> CheckToolUpdatesAsync(IpcRequest request, CancellationToken ct)
    {
        request.RequireAdministrator();
        var available = await tools.CheckForUpdatesAsync(ct).ConfigureAwait(false);
        return available.Select(u => new ToolInfo
        {
            Id = u.Tool.Id,
            DisplayName = u.Tool.DisplayName,
            Purpose = u.Tool.Purpose,
            InstalledVersion = u.InstalledVersion,
            AvailableVersion = u.AvailableVersion,
            Required = u.Tool.Required,
        }).ToList();
    }

    private async Task<object?> InstallToolAsync(IpcRequest request, CancellationToken ct)
    {
        request.RequireAdministrator();
        var toolId = request.Body<string>() ?? throw new InvalidOperationException("No tool specified.");

        var available = await tools.CheckForUpdatesAsync(ct).ConfigureAwait(false);
        var update = available.FirstOrDefault(u =>
                         string.Equals(u.Tool.Id, toolId, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException($"No update is available for '{toolId}'.");

        var installed = await tools.InstallAsync(update, progress: null, ct).ConfigureAwait(false);

        await alerts.RaiseAsync(Alert.Info(AlertKind.ToolUpdated,
            $"{update.Tool.DisplayName} updated to {installed.Version}",
            $"Previously {update.InstalledVersion ?? "not installed"}."), ct).ConfigureAwait(false);

        await NotifyChangedAsync($"{update.Tool.DisplayName} updated.").ConfigureAwait(false);
        return installed.Version;
    }

    private object? RollbackTool(IpcRequest request)
    {
        request.RequireAdministrator();
        var toolId = request.Body<string>() ?? throw new InvalidOperationException("No tool specified.");
        if (!tools.Rollback(toolId))
            throw new InvalidOperationException("There is no previous version to roll back to.");
        return null;
    }

    private Task NotifyChangedAsync(string reason) =>
        ConfigChanged?.Invoke(reason) ?? Task.CompletedTask;
}
