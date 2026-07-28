using System.Runtime.Versioning;
using CloudDrive.Core.Models;
using CloudDrive.Core.Platform;
using CloudDrive.Core.Stores;
using CloudDrive.Core.Tooling;
using CloudDrive.Ipc;
using CloudDrive.Notifications;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Service;

/// <summary>
/// Finds new CloudDrive releases and applies them when the machine is quiet.
///
/// The sequence is deliberate: <b>find, download, wait for idle, announce, apply</b>. Downloading
/// early means the bytes are ready when the idle window opens — waiting for quiet and only then
/// starting a 90 MB download would often see the window close first. Announcing before applying
/// gives a watching administrator a chance to intervene, and is why the alert says what is about to
/// happen rather than reporting it afterwards.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateCoordinator(
    ConfigStore config,
    MountReconciler reconciler,
    AlertDispatcher alerts,
    ToolManager tools,
    UpdateService updates,
    ILogger logger)
{
    private AvailableUpdate? _pending;
    private string? _downloadedInstaller;
    private DateTime _lastCheckUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raised as an update moves through its stages, so the UI can show progress.</summary>
    public event Func<UpdateEvent, Task>? Progress;

    /// <summary>Whether it is time to poll the feed again.</summary>
    public bool IsCheckDue(UpdateSettings settings) =>
        DateTime.UtcNow - _lastCheckUtc >= UpdateService.JitteredInterval(settings.CheckIntervalHours);

    /// <summary>
    /// The periodic tick: poll if due, download anything found, and apply it if the machine is idle.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var settings = config.LoadSettings();
        if (!settings.Updates.CheckForUpdates) return;

        if (IsCheckDue(settings.Updates))
        {
            try { await CheckNowAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The update check failed.");
            }
        }

        if (_pending is null || !settings.Updates.AutoInstallWhenIdle) return;

        var verdict = EvaluateIdle(settings.Updates);
        if (!verdict.IsIdle)
        {
            logger.LogDebug("Update {Version} is waiting: {Reason}", _pending.Version, verdict.Reason);
            return;
        }

        await ApplyAsync(_pending, ct).ConfigureAwait(false);
    }

    /// <summary>Polls the release feed now and downloads anything newer.</summary>
    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = config.LoadSettings();
            _lastCheckUtc = DateTime.UtcNow;

            var found = await updates.CheckAsync(settings.Updates, ct).ConfigureAwait(false);
            if (found is null)
            {
                return new UpdateCheckResult
                {
                    UpdateAvailable = false,
                    CurrentVersion = UpdateService.CurrentVersion,
                };
            }

            var isNew = _pending?.Version != found.Version;
            _pending = found;

            if (isNew)
            {
                logger.LogInformation("CloudDrive {Version} is available.", found.Version);

                if (settings.Updates.NotifyOnAvailable)
                {
                    await alerts.RaiseAsync(Alert.Info(AlertKind.UpdateAvailable,
                        $"CloudDrive {found.Version} is available",
                        settings.Updates.AutoInstallWhenIdle
                            ? $"Running {UpdateService.CurrentVersion}. It will be installed automatically "
                              + "once this machine is idle."
                            : $"Running {UpdateService.CurrentVersion}. Automatic installation is off, so "
                              + "install it from CloudDrive when convenient."), ct).ConfigureAwait(false);
                }

                await RaiseProgressAsync("available", found.Version, null).ConfigureAwait(false);
            }

            // Download eagerly, whether or not it will be applied automatically: it makes a manual
            // install instant, and it means a network outage later does not block the update.
            try
            {
                _downloadedInstaller = await updates.DownloadAsync(found, progress: null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Downloading CloudDrive {Version} failed; it will be retried.", found.Version);
            }

            var verdict = EvaluateIdle(settings.Updates);

            return new UpdateCheckResult
            {
                UpdateAvailable = true,
                CurrentVersion = UpdateService.CurrentVersion,
                AvailableVersion = found.Version,
                ReleaseNotes = found.ReleaseNotes,
                ReleaseUrl = found.ReleaseUrl,
                SizeBytes = found.SizeBytes,
                DeferredReason = verdict.IsIdle ? null : verdict.Reason,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies the pending update immediately, bypassing the idle gate. From the UI's "Install now".</summary>
    public async Task InstallNowAsync(CancellationToken ct)
    {
        var pending = _pending ?? throw new InvalidOperationException("There is no update to install.");
        await ApplyAsync(pending, ct).ConfigureAwait(false);
    }

    private IdleVerdict EvaluateIdle(UpdateSettings settings) =>
        IdleDetector.Evaluate(
            settings,
            reconciler.LiveMountPoints(),
            reconciler.ProtectedMountedMappings(),
            DateTime.Now);

    private async Task ApplyAsync(AvailableUpdate update, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = config.LoadSettings();

            var installer = _downloadedInstaller;
            if (installer is null || !File.Exists(installer))
                installer = await updates.DownloadAsync(update, progress: null, ct).ConfigureAwait(false);

            logger.LogInformation("Applying CloudDrive {Version}.", update.Version);

            if (settings.Updates.NotifyBeforeInstall)
            {
                await alerts.RaiseAsync(Alert.Warning(AlertKind.UpdateInstalling,
                    $"Installing CloudDrive {update.Version}",
                    $"{Environment.MachineName} is idle, so the update is being applied now. Mounts will be "
                    + "briefly unavailable and will come back automatically."), ct).ConfigureAwait(false);

                // Give the alert a moment to actually leave the machine. The installer is about to
                // stop this process, and an alert still sitting in an HTTP request would be lost —
                // spooled, but not delivered until after the restart, by which time it is stale.
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }

            await RaiseProgressAsync("installing", update.Version, null).ConfigureAwait(false);

            // Drop the mounts ourselves rather than letting the installer's service stop do it. This
            // way rclone exits cleanly, WinFsp releases the mount points, and any tool binary the
            // update wants to replace stops being held open.
            await reconciler.UnmountAllAsync(ct).ConfigureAwait(false);
            tools.ApplyPendingSwaps();

            // Fire and forget: the installer stops this service and replaces the binary this code is
            // running from, so there is nothing sensible to wait for. Mounts return by themselves —
            // the restarted service converges on the configuration in %ProgramData%, which the
            // upgrade leaves untouched.
            updates.LaunchInstaller(installer, restartService: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Applying CloudDrive {Version} failed.", update.Version);

            await alerts.RaiseAsync(Alert.Error(AlertKind.UpdateFailed,
                $"Updating to CloudDrive {update.Version} failed",
                $"{ex.Message} The current version is still installed and mounts are being restored."),
                ct).ConfigureAwait(false);

            await RaiseProgressAsync("failed", update.Version, ex.Message).ConfigureAwait(false);

            // Whatever went wrong, the machine must not be left with everything unmounted.
            try { await reconciler.ReconcileAsync(ct).ConfigureAwait(false); }
            catch (Exception restore) { logger.LogError(restore, "Restoring mounts after a failed update failed."); }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reports the outcome of an update that has just been applied. Called at startup, when the
    /// running version differs from the one last recorded — which is the only moment the process
    /// that performed the update can no longer speak for itself.
    /// </summary>
    public async Task ReportCompletedUpdateAsync(string previousVersion, CancellationToken ct)
    {
        var settings = config.LoadSettings();
        if (!settings.Updates.NotifyAfterInstall) return;

        await alerts.RaiseAsync(Alert.Info(AlertKind.UpdateInstalled,
            $"CloudDrive updated to {UpdateService.CurrentVersion}",
            $"Upgraded from {previousVersion}. Mounts are being restored."), ct).ConfigureAwait(false);

        UpdateService.CleanStaging(UpdateService.CurrentVersion);
    }

    /// <summary>Checks the managed tools for vendor updates and installs them if allowed and idle.</summary>
    public async Task TickToolsAsync(CancellationToken ct)
    {
        var settings = config.LoadSettings();
        if (!settings.Tools.CheckForToolUpdates) return;

        var state = tools.State;
        var due = state.LastCheckedUtc is null
                  || DateTime.UtcNow - state.LastCheckedUtc.Value
                  >= TimeSpan.FromHours(Math.Max(1, settings.Tools.CheckIntervalHours));
        if (!due) return;

        var available = await tools.CheckForUpdatesAsync(ct).ConfigureAwait(false);
        if (available.Count == 0) return;

        logger.LogInformation("Tool updates available: {Tools}",
            string.Join(", ", available.Select(u => $"{u.Tool.DisplayName} {u.AvailableVersion}")));

        if (!settings.Tools.AutoInstallWhenIdle) return;

        var verdict = EvaluateIdle(settings.Updates);
        if (!verdict.IsIdle)
        {
            logger.LogDebug("Tool updates are waiting: {Reason}", verdict.Reason);
            return;
        }

        foreach (var update in available)
        {
            ct.ThrowIfCancellationRequested();

            // An MSI needs a full installer run and, for WinFsp, potentially a reboot. Downloading
            // one silently in the background and running it under a user is not something to do
            // without being asked, so these are surfaced rather than applied.
            if (update.Tool.PackageKind == ToolPackageKind.Installer)
            {
                logger.LogInformation(
                    "{Tool} {Version} is available but is an installer package; install it from Settings → Tools.",
                    update.Tool.DisplayName, update.AvailableVersion);
                continue;
            }

            try
            {
                var installed = await tools.InstallAsync(update, progress: null, ct).ConfigureAwait(false);
                await alerts.RaiseAsync(Alert.Info(AlertKind.ToolUpdated,
                    $"{update.Tool.DisplayName} updated to {installed.Version}",
                    $"Previously {update.InstalledVersion ?? "not installed"}. "
                    + "It will be in use from the next mount."), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Installing {Tool} {Version} failed.",
                    update.Tool.DisplayName, update.AvailableVersion);
            }
        }

        tools.PruneOldVersions(settings.Tools.KeepPreviousVersions);
    }

    private Task RaiseProgressAsync(string stage, string? version, string? message) =>
        Progress?.Invoke(new UpdateEvent { Stage = stage, Version = version, Message = message })
        ?? Task.CompletedTask;
}
