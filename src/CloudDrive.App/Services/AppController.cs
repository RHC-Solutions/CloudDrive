using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Windows;
using CloudDrive.App.ViewModels;
using CloudDrive.CloudFiles;
using CloudDrive.Core;
using CloudDrive.Core.Models;
using CloudDrive.Core.Platform;
using CloudDrive.Core.Stores;
using CloudDrive.Ipc;

namespace CloudDrive.App.Services;

/// <summary>
/// The tray app's single point of contact with the service, and the owner of anything that has to
/// run in the user's own session.
///
/// The split matters and is the whole architecture in miniature: drive-letter mounts belong to the
/// LocalSystem service and this class only asks it to act, while Files On-Demand folders are run
/// here, because a Cloud Files sync root lives inside a user profile and calls back into that user's
/// session. There is no session-0 equivalent, so that work cannot be delegated.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppController : IAsyncDisposable
{
    private readonly FileLogger _log;
    private readonly Dictionary<Guid, OnDemandSyncManager> _onDemand = [];
    private readonly Lock _onDemandGate = new();

    private IpcClient? _client;
    private ServiceSnapshot? _snapshot;

    public AppController()
    {
        AppPaths.EnsureUserStore();
        _log = new FileLogger(AppPaths.UserLogsDir, "app");
        _log.LineWritten += line => Application.Current?.Dispatcher.BeginInvoke(() => AppendLog(line));
    }

    public ObservableCollection<MappingViewModel> Mappings { get; } = [];

    public ObservableCollection<string> LogLines { get; } = [];

    public AppSettings Settings => _snapshot?.Settings ?? new AppSettings();

    public IReadOnlyList<Account> Accounts => _snapshot?.Accounts ?? [];

    public CapabilityReport Capabilities { get; private set; } = new();

    /// <summary>True when this user may change configuration; the UI goes read-only otherwise.</summary>
    public bool IsAdministrator => _snapshot?.CallerIsAdministrator ?? false;

    public string? ServiceVersion => _snapshot?.ServiceVersion;

    /// <summary>Banner text: whatever the service is unhappy about, plus anything local.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>Raised when the state has been refreshed, so views can rebind.</summary>
    public event Action? StateRefreshed;

    /// <summary>Raised when the connection to the service drops or returns.</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _client?.IsConnected ?? false;

    // ---------------------------------------------------------------- Connection --------------

    /// <summary>
    /// Connects to the service and loads state. Returns the reason it could not, or null on success.
    ///
    /// A failure here is not fatal to the app: the window still opens and explains that the service
    /// is stopped, with a button to start it. Silently showing an empty mappings list would look
    /// exactly like "you have no mappings", which is a much worse lie.
    /// </summary>
    public async Task<string?> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            await DisposeClientAsync().ConfigureAwait(false);

            var client = new IpcClient();
            client.EventReceived += OnServiceEvent;
            client.Disconnected += OnDisconnected;

            await client.ConnectAsync(ct: ct).ConfigureAwait(false);
            await client.SubscribeAsync(ct).ConfigureAwait(false);
            _client = client;

            await RefreshAsync(ct).ConfigureAwait(false);
            ConnectionChanged?.Invoke(true);
            return null;
        }
        catch (ServiceUnavailableException ex)
        {
            _log.Warn(ex.Message);
            ConnectionChanged?.Invoke(false);
            return ex.Message;
        }
        catch (Exception ex)
        {
            _log.Error("Connecting to the service failed", ex);
            ConnectionChanged?.Invoke(false);
            return ex.Message;
        }
    }

    private void OnDisconnected(Exception? ex)
    {
        _log.Warn("The connection to the CloudDrive service was lost; reconnecting.");
        ConnectionChanged?.Invoke(false);
        _ = ReconnectLoopAsync();
    }

    /// <summary>
    /// Reconnects with a backoff. The service restarts on its own during an update, so a dropped
    /// connection is an expected event rather than an error state the user should have to clear.
    /// </summary>
    private async Task ReconnectLoopAsync()
    {
        foreach (var delay in new[] { 2, 5, 10, 20, 30, 60 })
        {
            await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
            if (await ConnectAsync().ConfigureAwait(false) is null)
            {
                _log.Info("Reconnected to the CloudDrive service.");
                return;
            }
        }
        _log.Error("Could not reconnect to the CloudDrive service. Check that it is running.");
    }

    // ---------------------------------------------------------------- State -------------------

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_client is null) return;

        _snapshot = await _client.CallAsync<ServiceSnapshot>(IpcOperation.GetState, null, ct)
            .ConfigureAwait(false);
        Capabilities = await _client.CallAsync<CapabilityReport>(IpcOperation.GetCapabilities, null, ct)
                           .ConfigureAwait(false) ?? new CapabilityReport();

        if (_snapshot is null) return;

        var warnings = new List<string>(_snapshot.Warnings);
        if (!IsAdministrator)
        {
            warnings.Add(
                "You are signed in as a standard user, so CloudDrive is read-only here. "
                + "Configuration changes need administrator rights.");
        }
        Warnings = warnings;

        SyncMappingList(_snapshot);
        StateRefreshed?.Invoke();
    }

    /// <summary>
    /// Updates the observable collection in place rather than clearing and refilling it, so the
    /// selected row and the scroll position survive a refresh — which happens on every mount state
    /// change and would otherwise make the list unusable while things are mounting.
    /// </summary>
    private void SyncMappingList(ServiceSnapshot snapshot)
    {
        var accounts = snapshot.Accounts.ToDictionary(a => a.Id);
        var states = snapshot.Mounts.ToDictionary(m => m.MappingId);

        var wanted = snapshot.Mappings
            .Where(m => accounts.ContainsKey(m.AccountId))
            .ToList();

        foreach (var stale in Mappings.Where(vm => wanted.All(m => m.Id != vm.Id)).ToList())
            Mappings.Remove(stale);

        foreach (var mapping in wanted)
        {
            var account = accounts[mapping.AccountId];
            var existing = Mappings.FirstOrDefault(vm => vm.Id == mapping.Id);

            if (existing is null)
            {
                existing = new MappingViewModel(mapping, account);
                Mappings.Add(existing);
            }
            else
            {
                existing.Update(mapping, account);
            }

            if (states.TryGetValue(mapping.Id, out var status))
            {
                existing.State = status.State;
                existing.StatusMessage = status.Message;
            }
            else if (mapping.Mode == MappingMode.OnDemandFolder)
            {
                // On-demand folders are ours, not the service's, so their state is not in the
                // snapshot.
                lock (_onDemandGate)
                    existing.State = _onDemand.ContainsKey(mapping.Id) ? MountState.Mounted : MountState.Unmounted;
            }
            else
            {
                existing.State = MountState.Unmounted;
            }
        }
    }

    private void OnServiceEvent(IpcOperation operation, JsonElement? payload)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        switch (operation)
        {
            case IpcOperation.GetStatus when IpcJson.Deserialize<MountStateEvent>(payload) is { } e:
                dispatcher.BeginInvoke(() =>
                {
                    var row = Mappings.FirstOrDefault(m => m.Id == e.MappingId);
                    if (row is null) return;
                    row.State = e.State;
                    row.StatusMessage = e.Message;
                    if (e.Message is not null) AppendLog($"[{row.Name}] {e.Message}");
                });
                break;

            case IpcOperation.GetLogTail when IpcJson.Deserialize<LogEvent>(payload) is { } log:
                dispatcher.BeginInvoke(() => AppendLog(log.Line));
                break;

            case IpcOperation.GetState:
                dispatcher.BeginInvoke(async () => await RefreshAsync().ConfigureAwait(false));
                break;

            case IpcOperation.CheckForUpdate when IpcJson.Deserialize<UpdateEvent>(payload) is { } update:
                dispatcher.BeginInvoke(() => UpdateAnnounced?.Invoke(update));
                break;
        }
    }

    /// <summary>Raised when the service reports update progress, so the tray can show a balloon.</summary>
    public event Action<UpdateEvent>? UpdateAnnounced;

    // ---------------------------------------------------------------- Mount control -----------

    /// <summary>
    /// Mounts a mapping, routing to whichever half of the system owns it.
    /// </summary>
    public async Task MountAsync(MappingViewModel row, CancellationToken ct = default)
    {
        if (row.Mapping.Mode == MappingMode.OnDemandFolder)
        {
            await EnableOnDemandAsync(row, ct).ConfigureAwait(false);
            return;
        }

        RequireClient();
        row.State = MountState.Mounting;
        await _client!.CallAsync(IpcOperation.Mount, new MountRequest { MappingId = row.Id }, ct)
            .ConfigureAwait(false);
    }

    public async Task UnmountAsync(MappingViewModel row, CancellationToken ct = default)
    {
        if (row.Mapping.Mode == MappingMode.OnDemandFolder)
        {
            DisableOnDemand(row.Id);
            row.State = MountState.Unmounted;
            return;
        }

        RequireClient();
        row.State = MountState.Unmounting;
        await _client!.CallAsync(IpcOperation.Unmount, new MountRequest { MappingId = row.Id }, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a Files On-Demand folder in this session.
    ///
    /// The credentials come from the service over the pipe, which will only release them for a
    /// mapping this user owns. They are never persisted here.
    /// </summary>
    private async Task EnableOnDemandAsync(MappingViewModel row, CancellationToken ct)
    {
        if (!Capabilities.SupportsFilesOnDemand)
            throw new InvalidOperationException(
                Capabilities.FilesOnDemandUnavailableReason
                ?? "Files On-Demand is not available on this version of Windows.");

        lock (_onDemandGate)
        {
            if (_onDemand.ContainsKey(row.Id)) return;
        }

        RequireClient();
        row.State = MountState.Mounting;

        var released = await _client!
            .CallAsync<OnDemandCredentialsResult>(
                IpcOperation.GetCredentialsForOnDemand, new MountRequest { MappingId = row.Id }, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The service did not return credentials for this mapping.");

        var manager = new OnDemandSyncManager(
            row.Mapping, released.Account, released.Credentials,
            line => _log.Raw($"[{row.Name}] {line}"));

        try
        {
            await manager.EnableAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            manager.Dispose();
            row.State = MountState.Error;
            throw;
        }

        lock (_onDemandGate) _onDemand[row.Id] = manager;
        row.State = MountState.Mounted;
        _log.Info($"'{row.Name}' is available at {manager.SyncRootPath} over {manager.ProtocolName}.");
    }

    private void DisableOnDemand(Guid mappingId)
    {
        OnDemandSyncManager? manager;
        lock (_onDemandGate)
        {
            if (!_onDemand.Remove(mappingId, out manager)) return;
        }
        try { manager?.Dispose(); }
        catch (Exception ex) { _log.Error("Stopping an on-demand folder failed", ex); }
    }

    /// <summary>Starts every on-demand mapping this user owns and has marked auto-mount.</summary>
    public async Task AutoMountAsync(CancellationToken ct = default)
    {
        foreach (var row in Mappings.Where(m =>
                     m.Mapping.Mode == MappingMode.OnDemandFolder && m.Mapping.AutoMount).ToList())
        {
            try { await EnableOnDemandAsync(row, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.Error($"Auto-mounting '{row.Name}' failed", ex); }
        }
    }

    // ---------------------------------------------------------------- Configuration -----------

    public async Task<Account?> SaveAccountAsync(
        Account account, Credentials? credentials, CancellationToken ct = default)
    {
        RequireClient();
        return await _client!.CallAsync<Account>(
            IpcOperation.SaveAccount,
            new SaveAccountRequest { Account = account, Credentials = credentials }, ct)
            .ConfigureAwait(false);
    }

    public async Task DeleteAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        RequireClient();
        await _client!.CallAsync(IpcOperation.DeleteAccount, new DeleteRequest { Id = accountId }, ct)
            .ConfigureAwait(false);
    }

    public async Task<Mapping?> SaveMappingAsync(Mapping mapping, CancellationToken ct = default)
    {
        RequireClient();
        return await _client!.CallAsync<Mapping>(
            IpcOperation.SaveMapping, new SaveMappingRequest { Mapping = mapping }, ct)
            .ConfigureAwait(false);
    }

    public async Task DeleteMappingAsync(Guid mappingId, CancellationToken ct = default)
    {
        DisableOnDemand(mappingId);
        RequireClient();
        await _client!.CallAsync(IpcOperation.DeleteMapping, new DeleteRequest { Id = mappingId }, ct)
            .ConfigureAwait(false);
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        RequireClient();
        await _client!.CallAsync(IpcOperation.SaveSettings, settings, ct).ConfigureAwait(false);
    }

    public async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        RequireClient();
        return await _client!.CallAsync<UpdateCheckResult>(IpcOperation.CheckForUpdate, null, ct)
            .ConfigureAwait(false);
    }

    public async Task<ToolStateResult?> GetToolStateAsync(CancellationToken ct = default)
    {
        RequireClient();
        return await _client!.CallAsync<ToolStateResult>(IpcOperation.GetToolState, null, ct)
            .ConfigureAwait(false);
    }

    public async Task SendTestAlertAsync(
        NotificationTarget target, NotificationSecret? secret, CancellationToken ct = default)
    {
        RequireClient();
        await _client!.CallAsync(
            IpcOperation.SendTestAlert,
            new NotificationTargetRequest { Target = target, Secret = secret }, ct)
            .ConfigureAwait(false);
    }

    public async Task SaveNotificationTargetAsync(
        NotificationTarget target, NotificationSecret? secret, CancellationToken ct = default)
    {
        RequireClient();
        await _client!.CallAsync(
            IpcOperation.SaveNotificationTarget,
            new NotificationTargetRequest { Target = target, Secret = secret }, ct)
            .ConfigureAwait(false);
    }

    public async Task DeleteNotificationTargetAsync(Guid targetId, CancellationToken ct = default)
    {
        RequireClient();
        await _client!.CallAsync(IpcOperation.DeleteNotificationTarget, new DeleteRequest { Id = targetId }, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Loads recent service log lines into the activity pane on startup.</summary>
    public async Task LoadLogTailAsync(CancellationToken ct = default)
    {
        if (_client is null) return;
        try
        {
            var tail = await _client
                .CallAsync<LogTailResult>(IpcOperation.GetLogTail, new LogTailRequest { Lines = 300 }, ct)
                .ConfigureAwait(false);
            if (tail is null) return;
            foreach (var line in tail.Lines) AppendLog(line);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not read the service log: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- Service lifecycle -------

    /// <summary>
    /// Installs and starts the service, relaunching elevated for the step that needs it.
    /// </summary>
    public static bool InstallService()
    {
        var exe = ServiceControl.ResolveServiceExe()
            ?? throw new FileNotFoundException(
                "CloudDrive.Service.exe was not found next to the application. Reinstall CloudDrive.");

        if (IsElevated())
        {
            ServiceControl.Install(exe);
            ServiceControl.Start(TimeSpan.FromSeconds(45));
            return true;
        }

        // One elevated relaunch for one verb, rather than running the whole UI as administrator.
        return ServiceControl.RelaunchElevated("--install-service");
    }

    public static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // ---------------------------------------------------------------- Logging -----------------

    /// <summary>Appends to the activity pane, capping it so a chatty mount cannot exhaust memory.</summary>
    private void AppendLog(string line)
    {
        const int cap = 2000;
        LogLines.Add(line);
        while (LogLines.Count > cap) LogLines.RemoveAt(0);
    }

    public string LogText => string.Join(Environment.NewLine, LogLines);

    public string UserLogDirectory => AppPaths.UserLogsDir;

    // ---------------------------------------------------------------- Export ------------------

    /// <summary>
    /// Exports accounts and mappings as JSON.
    ///
    /// Secrets are deliberately excluded and cannot be included: they are DPAPI-protected under the
    /// service's SYSTEM profile and are not readable here at all. An export is therefore a
    /// configuration backup, not a credential backup, and restoring one means re-entering passwords.
    /// </summary>
    public async Task ExportAsync(string path, CancellationToken ct = default)
    {
        RequireClient();
        var snapshot = _snapshot ?? throw new InvalidOperationException("Nothing has been loaded yet.");

        var payload = new
        {
            exported = DateTime.UtcNow,
            machine = Environment.MachineName,
            version = snapshot.ServiceVersion,
            note = "Credentials are not exported; they are bound to this machine.",
            accounts = snapshot.Accounts,
            mappings = snapshot.Mappings,
            settings = snapshot.Settings,
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        });
        await File.WriteAllTextAsync(path, json, Encoding.UTF8, ct).ConfigureAwait(false);
        _log.Info($"Exported the configuration to {path}.");
    }

    private void RequireClient()
    {
        if (_client is null || !_client.IsConnected)
            throw new ServiceUnavailableException(
                "Not connected to the CloudDrive service. Check that it is running.");
    }

    private async Task DisposeClientAsync()
    {
        if (_client is null) return;
        var client = _client;
        _client = null;
        await client.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        // Stop the on-demand folders explicitly. Leaving a sync root connected after the app exits
        // leaves Explorer showing placeholders that can never hydrate, which looks like data loss.
        lock (_onDemandGate)
        {
            foreach (var manager in _onDemand.Values)
            {
                try { manager.Dispose(); } catch { /* shutting down */ }
            }
            _onDemand.Clear();
        }

        await DisposeClientAsync().ConfigureAwait(false);
        _log.Dispose();
    }
}
