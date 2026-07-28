using System.Runtime.Versioning;
using CloudDrive.Core;
using CloudDrive.Core.Models;
using CloudDrive.Core.Platform;
using CloudDrive.Core.Stores;
using CloudDrive.Core.Tooling;
using CloudDrive.Ipc;
using CloudDrive.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Service;

/// <summary>
/// The service's main loop. Owns every long-lived object and coordinates the periodic work:
/// reconciling mounts, flushing the alert spool, and checking for updates.
///
/// One loop rather than several timers, so the periodic jobs cannot overlap each other or run while
/// the service is shutting down. Each job is individually guarded, because a failure in the update
/// checker must not stop mounts being reconciled.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceWorker : BackgroundService
{
    /// <summary>How often the loop wakes. Everything else is a multiple of this.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    /// <summary>Re-mount anything that has fallen over, even with no configuration change.</summary>
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan SpoolFlushInterval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan UpdateTickInterval = TimeSpan.FromMinutes(15);

    private readonly ILogger<ServiceWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private ConfigStore _config = null!;
    private CredentialStore _credentials = null!;
    private FileLogger _fileLog = null!;
    private AlertDispatcher _alerts = null!;
    private ToolManager _tools = null!;
    private MountReconciler _reconciler = null!;
    private UpdateCoordinator _updates = null!;
    private IpcServer _ipc = null!;
    private FileSystemWatcher? _configWatcher;

    private DateTime _lastReconcile = DateTime.MinValue;
    private DateTime _lastSpoolFlush = DateTime.MinValue;
    private DateTime _lastUpdateTick = DateTime.MinValue;
    private volatile bool _reconcileRequested = true;

    public ServiceWorker(ILogger<ServiceWorker> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Compose();
        }
        catch (Exception ex)
        {
            // Nothing works without composition, and a service that silently does nothing is worse
            // than one that stops with a reason in the Event Log.
            _logger.LogCritical(ex, "CloudDrive could not start.");
            throw;
        }

        await OnStartedAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;

            if (_reconcileRequested || now - _lastReconcile >= ReconcileInterval)
            {
                _reconcileRequested = false;
                _lastReconcile = now;
                await SafelyAsync("reconciling mounts",
                    () => _reconciler.ReconcileAsync(stoppingToken)).ConfigureAwait(false);
            }

            if (now - _lastSpoolFlush >= SpoolFlushInterval)
            {
                _lastSpoolFlush = now;
                await SafelyAsync("flushing the alert spool",
                    () => _alerts.FlushSpoolAsync(stoppingToken)).ConfigureAwait(false);
            }

            if (now - _lastUpdateTick >= UpdateTickInterval)
            {
                _lastUpdateTick = now;
                await SafelyAsync("checking for updates",
                    () => _updates.TickAsync(stoppingToken)).ConfigureAwait(false);
                await SafelyAsync("checking for tool updates",
                    () => _updates.TickToolsAsync(stoppingToken)).ConfigureAwait(false);
            }

            try { await Task.Delay(Tick, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        await OnStoppingAsync().ConfigureAwait(false);
    }

    /// <summary>Builds the object graph. Deliberately explicit rather than through DI: the wiring
    /// order matters (the reconciler needs a resolved rclone path, the dispatcher needs the
    /// reconciler) and reading it top to bottom is worth more here than container registrations.</summary>
    private void Compose()
    {
        AppPaths.EnsureMachineStore();

        _config = new ConfigStore();
        var settings = _config.LoadSettings();

        _fileLog = new FileLogger(AppPaths.MachineLogsDir, "service", settings.LogRetentionDays);
        _credentials = new CredentialStore();

        try
        {
            _credentials.Load();
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // The blob is bound to SYSTEM on this machine. If it cannot be decrypted the store was
            // written by a different account or restored from another machine, and every mount will
            // fail for want of credentials. Saying so once, loudly, beats a stream of "no
            // credentials" errors that do not explain themselves.
            _logger.LogError(ex,
                "The credential store could not be decrypted. It is bound to this machine's SYSTEM "
                + "account, so a store restored from a backup or another machine cannot be read. "
                + "Re-enter the account credentials in CloudDrive.");
        }

        _tools = new ToolManager(log: _fileLog.Info);

        _alerts = new AlertDispatcher(
            () => (_config.LoadSettings(), id => _credentials.GetNotification(id)),
            log: _fileLog.Info);

        var rclone = _tools.ResolveRclone();
        _reconciler = new MountReconciler(
            _config, _credentials, _alerts,
            _loggerFactory.CreateLogger<MountReconciler>(),
            rclone,
            DriveIconPath());

        _updates = new UpdateCoordinator(
            _config, _reconciler, _alerts, _tools,
            new UpdateService(log: _fileLog.Info),
            _loggerFactory.CreateLogger<UpdateCoordinator>());

        var dispatcher = new IpcDispatcher(
            _config, _credentials, _reconciler, _alerts, _tools, _updates, _fileLog,
            _loggerFactory.CreateLogger<IpcDispatcher>());

        _ipc = new IpcServer(dispatcher.DispatchAsync, _fileLog.Info);

        // Anything that changes configuration triggers a reconcile and tells connected clients.
        dispatcher.ConfigChanged += async reason =>
        {
            _fileLog.Info(reason);
            _reconcileRequested = true;
            await _ipc.PublishAsync(IpcOperation.GetState, new ConfigChangedEvent { Reason = reason })
                .ConfigureAwait(false);
        };

        _reconciler.MountStateChanged += e => _ = _ipc.PublishAsync(IpcOperation.GetStatus, new MountStateEvent
        {
            MappingId = e.MappingId,
            State = e.State,
            Message = e.Message,
        });

        _reconciler.MountLogged += e =>
        {
            _fileLog.Raw(e.Line);
            _ = _ipc.PublishAsync(IpcOperation.GetLogTail, new LogEvent
            {
                MappingId = e.MappingId,
                Line = e.Line,
            });
        };

        _updates.Progress += e => _ipc.PublishAsync(IpcOperation.CheckForUpdate, e);

        // Pick up edits made by anything other than the IPC layer — an administrator editing the
        // JSON by hand, or a configuration-management tool dropping a file in.
        WatchConfig();

        _ipc.Start();
    }

    private async Task OnStartedAsync(CancellationToken ct)
    {
        _fileLog.Info($"CloudDrive service {UpdateService.CurrentVersion} starting on "
                      + $"{OsCapabilities.EditionName} (build {OsCapabilities.BuildNumber}).");

        var settings = _config.LoadSettings();

        if (settings.Tools.AddToSystemPath)
        {
            await SafelyAsync("registering the tools directory on PATH",
                () => Task.FromResult(_tools.RegisterOnPath())).ConfigureAwait(false);
        }

        if (_reconciler.UnavailableReason is { } reason) _fileLog.Warn(reason);
        if (!WinFsp.IsInstalled) _fileLog.Warn("WinFsp is not installed; drive-letter mounts will fail.");
        if (!OsCapabilities.SupportsFilesOnDemand)
            _fileLog.Info("Files On-Demand is unavailable on this OS; only drive mounts are offered.");

        await ReportVersionChangeAsync(ct).ConfigureAwait(false);

        await _alerts.RaiseAsync(Alert.Info(AlertKind.ServiceStarted,
            $"CloudDrive started on {Environment.MachineName}",
            $"Version {UpdateService.CurrentVersion} on {OsCapabilities.EditionName}."), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Detects that the running version differs from the one recorded at the last shutdown, which
    /// means an update was applied. The process that performed it was replaced mid-flight and could
    /// not report the outcome itself, so this is the only place it can be reported.
    /// </summary>
    private async Task ReportVersionChangeAsync(CancellationToken ct)
    {
        var stampPath = Path.Combine(AppPaths.MachineDir, "version.txt");
        string? previous = null;

        try
        {
            if (File.Exists(stampPath)) previous = (await File.ReadAllTextAsync(stampPath, ct).ConfigureAwait(false)).Trim();
            await File.WriteAllTextAsync(stampPath, UpdateService.CurrentVersion, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _fileLog.Warn($"Could not record the running version: {ex.Message}");
            return;
        }

        if (previous is null || previous == UpdateService.CurrentVersion) return;

        _fileLog.Info($"Upgraded from {previous} to {UpdateService.CurrentVersion}.");
        await SafelyAsync("reporting the completed update",
            () => _updates.ReportCompletedUpdateAsync(previous, ct)).ConfigureAwait(false);
    }

    private async Task OnStoppingAsync()
    {
        _fileLog.Info("CloudDrive service stopping.");

        // A stop is usually a reboot or an upgrade, and an alert saying so is useful. Not spooled on
        // failure: by the time anything could retry it, the fact would be stale.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _alerts.RaiseAsync(Alert.Info(AlertKind.ServiceStopping,
                $"CloudDrive stopping on {Environment.MachineName}",
                "Mounts are being released."), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _fileLog.Warn($"The shutdown alert could not be sent: {ex.Message}");
        }

        _configWatcher?.Dispose();
        await _ipc.DisposeAsync().ConfigureAwait(false);
        await _reconciler.DisposeAsync().ConfigureAwait(false);
        await _alerts.DisposeAsync().ConfigureAwait(false);
        _fileLog.Dispose();
    }

    private void WatchConfig()
    {
        try
        {
            _configWatcher = new FileSystemWatcher(AppPaths.MachineDir)
            {
                Filter = "*.json",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            // Just set a flag. The change arrives as a burst of events for one logical edit, and the
            // loop coalesces them into a single reconcile on its next tick.
            _configWatcher.Changed += (_, _) => _reconcileRequested = true;
            _configWatcher.Created += (_, _) => _reconcileRequested = true;
            _configWatcher.Renamed += (_, _) => _reconcileRequested = true;
        }
        catch (Exception ex)
        {
            _fileLog.Warn($"Watching the configuration directory failed; changes will be picked up on the "
                          + $"periodic reconcile instead: {ex.Message}");
        }
    }

    /// <summary>
    /// The icon disk-mode drive letters are branded with, or null when it cannot be found.
    ///
    /// The service is a console executable with no icon of its own, so it points Explorer at the
    /// tray app's icon file, which the installer places alongside it. Falling back to null rather
    /// than to a guess: an icon path that does not resolve leaves Explorer showing a blank drive,
    /// which looks more broken than the default icon does.
    /// </summary>
    private static string? DriveIconPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "clouddrive.ico"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "clouddrive.ico"),
            Path.Combine(AppContext.BaseDirectory, "..", "CloudDrive.exe"),
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (!File.Exists(full)) continue;
            // Explorer accepts "file,index" for an icon inside an executable; index 0 is the app icon.
            return full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? full + ",0" : full;
        }

        return null;
    }

    /// <summary>
    /// Runs a periodic job, logging rather than propagating a failure. One job failing must never
    /// stop the loop — a broken update checker would otherwise take mount recovery down with it.
    /// </summary>
    private async Task SafelyAsync(string what, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed while {What}.", what);
            _fileLog.Error($"Failed while {what}", ex);
        }
    }
}
