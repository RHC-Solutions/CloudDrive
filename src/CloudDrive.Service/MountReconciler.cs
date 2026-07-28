using System.Runtime.Versioning;
using CloudDrive.Core.Models;
using CloudDrive.Core.Mounting;
using CloudDrive.Core.OAuth;
using CloudDrive.Core.Stores;
using CloudDrive.Notifications;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Service;

/// <summary>
/// Drives the machine's mounts towards whatever the configuration says they should be.
///
/// Written as a converge-on-desired-state loop rather than as a queue of mount and unmount commands.
/// The service can be restarted, the machine can reboot, and rclone can die mid-flight; in every one
/// of those cases the correct recovery is identical — read what should be mounted, compare it with
/// what is mounted, fix the difference. There is no command history to replay and nothing to lose.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MountReconciler : IAsyncDisposable
{
    private readonly ConfigStore _config;
    private readonly CredentialStore _credentials;
    private readonly AlertDispatcher _alerts;
    private readonly ILogger _logger;
    private readonly MountManager? _mounts;
    private readonly string? _unavailableReason;
    private readonly OAuthTokenService _tokens = new();
    private readonly OAuthClientRegistry _oauthClients = new();

    /// <summary>What each mapping is currently mounted with, so a configuration edit is detectable.</summary>
    private readonly Dictionary<Guid, string> _applied = [];

    /// <summary>Mappings already reported as failing, so a retry loop does not alert every pass.</summary>
    private readonly HashSet<Guid> _reportedFailures = [];

    private readonly SemaphoreSlim _gate = new(1, 1);

    public MountReconciler(
        ConfigStore config,
        CredentialStore credentials,
        AlertDispatcher alerts,
        ILogger logger,
        string? rcloneExePath,
        string? driveIconPath)
    {
        _config = config;
        _credentials = credentials;
        _alerts = alerts;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(rcloneExePath) || !File.Exists(rcloneExePath))
        {
            _unavailableReason =
                "rclone was not found. Install it from Settings → Tools, or place rclone.exe next to the service.";
            return;
        }

        _mounts = new MountManager(rcloneExePath)
        {
            DriveIconPath = driveIconPath,
            VerboseLogging = config.LoadSettings().VerboseLogging,
        };

        _mounts.StatusChanged += (_, e) =>
        {
            if (e.Message is not null)
                _logger.LogInformation("[{Mapping}] {State}: {Message}", e.MappingId, e.State, e.Message);
            MountStateChanged?.Invoke(e);
        };
        _mounts.LogReceived += (_, e) =>
        {
            _logger.LogDebug("{Line}", e.Line);
            MountLogged?.Invoke(e);
        };
    }

    /// <summary>Raised on every mount state transition, so the IPC layer can push it to clients.</summary>
    public event Action<MountStatusChangedEventArgs>? MountStateChanged;

    public event Action<MountLogEventArgs>? MountLogged;

    public string? UnavailableReason => _unavailableReason;

    public IReadOnlyList<MountStatus> Snapshot() => _mounts?.Snapshot() ?? [];

    public bool IsMounted(Guid mappingId) => _mounts?.IsMounted(mappingId) ?? false;

    /// <summary>Mount points currently live, for the idle check.</summary>
    public IReadOnlyList<string> LiveMountPoints()
    {
        if (_mounts is null) return [];
        var document = _config.Load();
        return _mounts.Snapshot()
            .Where(s => s.State == MountState.Mounted)
            .Select(s => document.FindMapping(s.MappingId)?.MountPoint)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();
    }

    /// <summary>Names of mounted mappings that must not be interrupted by an update.</summary>
    public IReadOnlyList<string> ProtectedMountedMappings()
    {
        if (_mounts is null) return [];
        var document = _config.Load();
        return _mounts.Snapshot()
            .Where(s => s.State == MountState.Mounted)
            .Select(s => document.FindMapping(s.MappingId))
            .Where(m => m is { BlockAutoUpdateWhileMounted: true })
            .Select(m => m!.Name)
            .ToList();
    }

    /// <summary>
    /// Brings mounts in line with the configuration. Serialised, because a burst of file writes can
    /// fire several change notifications for what is really one edit.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await ReconcileCoreAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task ReconcileCoreAsync(CancellationToken ct)
    {
        if (_mounts is null)
        {
            _logger.LogError("{Reason} No mounts can be started.", _unavailableReason);
            return;
        }

        var document = _config.Load();
        var desired = document.ServiceableMappings().ToDictionary(pair => pair.Mapping.Id);

        foreach (var orphan in document.OrphanedMappings())
        {
            _logger.LogWarning(
                "Mapping '{Name}' refers to an account that no longer exists and will not be mounted.",
                orphan.Name);
        }

        // Gone or edited: drop the old mount first, so the mount point is free before its
        // replacement tries to claim it.
        foreach (var id in _applied.Keys.ToArray())
        {
            var stillWanted = desired.TryGetValue(id, out var pair)
                              && _applied[id] == pair.Mapping.MountFingerprint(pair.Account);
            if (stillWanted) continue;

            _logger.LogInformation("Unmounting {Mapping}.", id);
            try
            {
                await _mounts.UnmountAsync(id, ct).ConfigureAwait(false);
                _applied.Remove(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unmounting {Mapping} failed.", id);
            }
        }

        foreach (var (id, (mapping, account)) in desired)
        {
            ct.ThrowIfCancellationRequested();
            if (_applied.ContainsKey(id) && _mounts.IsMounted(id)) continue;

            await MountOneAsync(mapping, account, ct).ConfigureAwait(false);
        }
    }

    private async Task MountOneAsync(Mapping mapping, Account account, CancellationToken ct)
    {
        var credentials = _credentials.GetAccount(account.Id);
        if (credentials is null || !credentials.IsCompleteFor(account.Descriptor.Auth))
        {
            await ReportFailureOnceAsync(mapping, account, AlertKind.CredentialsRejected,
                    $"No usable credentials for '{account.Name}'",
                    $"'{mapping.Name}' cannot be mounted because the stored credentials for account "
                    + $"'{account.Name}' are missing or incomplete. Re-enter them in CloudDrive.", ct)
                .ConfigureAwait(false);
            return;
        }

        if (account.NeedsReauth)
        {
            await ReportFailureOnceAsync(mapping, account, AlertKind.ReauthRequired,
                    $"'{account.Name}' needs to be signed in again",
                    $"'{mapping.Name}' cannot be mounted: {account.ReauthRequiredReason} "
                    + "Open CloudDrive on a desktop session and authorise the account again.", ct)
                .ConfigureAwait(false);
            return;
        }

        // An OAuth account needs a live access token before rclone is handed its config, because the
        // token goes into that config. This is the whole reason a OneDrive mount can exist before
        // anyone signs in: the interactive half happened once, and the service only refreshes.
        if (account.Descriptor.IsOAuth
            && !await TryRefreshTokenAsync(mapping, account, credentials, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            _logger.LogInformation("Mounting '{Name}' at {MountPoint}.", mapping.Name, mapping.MountPoint);
            await _mounts!.MountAsync(mapping, account, credentials, ct).ConfigureAwait(false);

            _applied[mapping.Id] = mapping.MountFingerprint(account);

            var recovered = _reportedFailures.Remove(mapping.Id);
            if (recovered)
            {
                // Clear the cooldown so the recovery message is not swallowed by the failure that
                // preceded it moments ago. "It is working again" is the alert people most want.
                _alerts.ResetSuppression(AlertKind.MountSucceeded, mapping.Id);
            }

            _logger.LogInformation("Mounted '{Name}' at {MountPoint} over {Protocol}.",
                mapping.Name, mapping.MountPoint, account.EffectiveProtocol);

            await _alerts.RaiseAsync(
                Alert.Info(AlertKind.MountSucceeded,
                        recovered ? $"'{mapping.Name}' is mounted again" : $"'{mapping.Name}' is mounted",
                        $"Mounted at {mapping.MountPoint} over {account.EffectiveProtocol}.")
                    .For(mapping, account), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad mapping must not stop the others; the next reconcile retries it.
            _logger.LogError(ex, "Mounting '{Name}' failed.", mapping.Name);
            await ReportFailureOnceAsync(mapping, account, AlertKind.MountFailed,
                    $"'{mapping.Name}' failed to mount", ex.Message, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Refreshes an OAuth account's access token, returning false when the mount must be abandoned.
    ///
    /// The distinction that matters is between a grant that is *gone* and a network that is merely
    /// down. A revoked or expired grant is recorded on the account and alerted, because only a human
    /// at a browser can fix it and retrying forever would just generate noise. Anything else is left
    /// alone for the next reconcile to retry.
    /// </summary>
    private async Task<bool> TryRefreshTokenAsync(
        Mapping mapping, Account account, Credentials credentials, CancellationToken ct)
    {
        try
        {
            var changed = await _tokens
                .EnsureAccessTokenAsync(account, credentials, _oauthClients, ct)
                .ConfigureAwait(false);

            if (changed)
            {
                // Persist immediately. Providers may rotate the refresh token and revoke the old one,
                // so losing the new value would lock this account out permanently.
                _credentials.UpdateAccount(account.Id, stored =>
                {
                    stored.AccessToken = credentials.AccessToken;
                    stored.AccessTokenExpiresUtc = credentials.AccessTokenExpiresUtc;
                    stored.RefreshToken = credentials.RefreshToken;
                });

                _config.Mutate(document =>
                {
                    var stored = document.FindAccount(account.Id);
                    if (stored is null) return;
                    stored.TokenRefreshedUtc = DateTime.UtcNow;
                    stored.ReauthRequiredReason = null;
                });
            }

            return true;
        }
        catch (OAuthReauthRequiredException ex)
        {
            _logger.LogError("The {Provider} grant for '{Account}' is no longer valid: {Reason}",
                account.Descriptor.DisplayName, account.Name, ex.Message);

            // Recorded on the account so the UI badges it and the next reconcile skips straight to the
            // reauth branch instead of hammering the token endpoint.
            _config.Mutate(document =>
            {
                var stored = document.FindAccount(account.Id);
                if (stored is not null) stored.ReauthRequiredReason = ex.Message;
            });

            await ReportFailureOnceAsync(mapping, account, AlertKind.ReauthRequired,
                    $"'{account.Name}' needs to be signed in again",
                    $"{account.Descriptor.DisplayName} rejected the stored sign-in: {ex.Message} "
                    + "Open CloudDrive on a desktop session and authorise the account again.", ct)
                .ConfigureAwait(false);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Refreshing the token for '{Account}' failed; will retry.", account.Name);
            return false;
        }
    }

    /// <summary>
    /// Alerts about a failure the first time it happens and stays quiet on subsequent passes.
    ///
    /// The reconciler retries every failed mapping on a timer, so without this a permanently broken
    /// mapping would raise an alert on every pass forever. The dispatcher's cooldown would blunt
    /// that, but tracking it here means the recovery message is correctly framed as a recovery.
    /// </summary>
    private async Task ReportFailureOnceAsync(
        Mapping mapping, Account account, AlertKind kind, string title, string message, CancellationToken ct)
    {
        if (!_reportedFailures.Add(mapping.Id)) return;

        var severity = kind == AlertKind.MountFailed ? AlertSeverity.Error : AlertSeverity.Error;
        await _alerts.RaiseAsync(
            new Alert { Kind = kind, Severity = severity, Title = title, Message = message }
                .For(mapping, account), ct).ConfigureAwait(false);
    }

    /// <summary>Mounts one mapping on demand, from an IPC request.</summary>
    public async Task MountAsync(Guid mappingId, CancellationToken ct)
    {
        if (_mounts is null) throw new InvalidOperationException(_unavailableReason);

        var document = _config.Load();
        var mapping = document.FindMapping(mappingId)
            ?? throw new InvalidOperationException("That mapping no longer exists.");
        var account = document.FindAccount(mapping.AccountId)
            ?? throw new InvalidOperationException("That mapping's account no longer exists.");

        if (!mapping.IsServiceable)
            throw new InvalidOperationException(
                "This mapping runs in the user session, not the service. Mount it from the CloudDrive window.");

        var credentials = _credentials.GetAccount(account.Id)
            ?? throw new InvalidOperationException($"No credentials are stored for '{account.Name}'.");

        await _mounts.MountAsync(mapping, account, credentials, ct).ConfigureAwait(false);
        _applied[mappingId] = mapping.MountFingerprint(account);
        _reportedFailures.Remove(mappingId);
    }

    public async Task UnmountAsync(Guid mappingId, CancellationToken ct)
    {
        if (_mounts is null) return;
        await _mounts.UnmountAsync(mappingId, ct).ConfigureAwait(false);
        _applied.Remove(mappingId);
    }

    /// <summary>
    /// Unmounts everything and forgets what was applied, so the next reconcile rebuilds from
    /// scratch. Used before applying an update, which replaces the rclone binary underneath us.
    /// </summary>
    public async Task UnmountAllAsync(CancellationToken ct)
    {
        if (_mounts is null) return;
        await _mounts.UnmountAllAsync(ct).ConfigureAwait(false);
        _applied.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_mounts is not null)
        {
            try { await _mounts.UnmountAllAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Unmounting everything on shutdown failed."); }
            await _mounts.DisposeAsync().ConfigureAwait(false);
        }
        _tokens.Dispose();
        _gate.Dispose();
    }
}
