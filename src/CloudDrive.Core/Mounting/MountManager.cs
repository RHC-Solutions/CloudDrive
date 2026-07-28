using System.Collections.Concurrent;
using System.Runtime.Versioning;
using CloudDrive.Core.Models;
using CloudDrive.Core.Platform;

namespace CloudDrive.Core.Mounting;

public sealed class MountStatusChangedEventArgs : EventArgs
{
    public required Guid MappingId { get; init; }
    public required MountState State { get; init; }
    public string? Message { get; init; }
}

public sealed class MountLogEventArgs : EventArgs
{
    public required Guid MappingId { get; init; }
    public required string Line { get; init; }
}

/// <summary>A snapshot of one live mount, for the UI and the CLI.</summary>
public sealed record MountStatus(
    Guid MappingId,
    MountState State,
    string? Message,
    StorageProtocol Protocol,
    DateTime? MountedSinceUtc,
    int RestartCount);

/// <summary>
/// Owns the live mounts: one rclone process per mapping, plus the state machine around it.
///
/// Used by the Windows service for serviced mappings and by the tray app for session ones, with no
/// difference in behaviour — the only thing that changes is which process is hosting it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MountManager : IAsyncDisposable
{
    private readonly string _rcloneExePath;
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    /// <summary>Attempts to auto-restart a mount whose process dies while it was up.</summary>
    public int MaxAutoRestarts { get; init; } = 3;

    /// <summary>How long to wait for the mount point to appear before declaring failure.</summary>
    public TimeSpan MountTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public bool VerboseLogging { get; init; }

    /// <summary>Path to an icon given to disk-mode drive letters, or null to leave them unbranded.</summary>
    public string? DriveIconPath { get; init; }

    public MountManager(string rcloneExePath)
    {
        if (string.IsNullOrWhiteSpace(rcloneExePath) || !File.Exists(rcloneExePath))
            throw new FileNotFoundException("rclone.exe was not found.", rcloneExePath);
        _rcloneExePath = rcloneExePath;
    }

    public event EventHandler<MountStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<MountLogEventArgs>? LogReceived;

    public MountState GetState(Guid mappingId) =>
        _sessions.TryGetValue(mappingId, out var s) ? s.State : MountState.Unmounted;

    public bool IsMounted(Guid mappingId) => GetState(mappingId) == MountState.Mounted;

    public IReadOnlyList<MountStatus> Snapshot() =>
    [
        .. _sessions.Values.Select(s => new MountStatus(
            s.Mapping.Id, s.State, s.LastMessage, s.Protocol, s.MountedSinceUtc, s.RestartCount)),
    ];

    /// <summary>
    /// Mounts <paramref name="mapping"/>, returning once the mount point is live or throwing.
    /// </summary>
    public async Task MountAsync(
        Mapping mapping, Account account, Credentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(credentials);

        if (!credentials.IsCompleteFor(account.Descriptor.Auth))
            throw new InvalidOperationException(
                $"The stored credentials for '{account.Name}' are incomplete.");

        if (_sessions.TryGetValue(mapping.Id, out var existing)
            && existing.State is MountState.Mounted or MountState.Mounting)
        {
            return;
        }

        if (MountPointExists(mapping))
        {
            throw new InvalidOperationException(mapping.MountTarget == MountTarget.Directory
                ? $"'{mapping.MountPoint}' already exists. WinFsp creates a directory mountpoint on "
                  + "mount, so it must not be there beforehand."
                : $"Drive {mapping.MountPoint} is already in use by another volume.");
        }

        var protocol = account.EffectiveProtocol;
        if (!credentials.SupportsProtocol(protocol))
            throw new InvalidOperationException(
                $"The stored credentials cannot authenticate over {protocol}.");

        var session = new Session(mapping, account, credentials, protocol, NewProcess());
        _sessions[mapping.Id] = session;

        try
        {
            await StartAsync(session, ct).ConfigureAwait(false);
        }
        catch
        {
            // Dispose the process as well as forgetting the session. Dropping the reference alone
            // leaked a Process handle on every failed mount, and the reconciler retries failed
            // mappings every couple of minutes indefinitely.
            _sessions.TryRemove(mapping.Id, out _);
            session.Process.Dispose();
            throw;
        }
    }

    private RcloneProcess NewProcess() => new(_rcloneExePath) { VerboseLogging = VerboseLogging };

    private async Task StartAsync(Session session, CancellationToken ct)
    {
        var mapping = session.Mapping;
        SetState(session, MountState.Mounting, $"Mounting over {session.Protocol}…");

        var process = session.Process;
        process.LogLineReceived += line =>
            LogReceived?.Invoke(this, new MountLogEventArgs { MappingId = mapping.Id, Line = line });
        process.Exited += (code, requested) => OnExited(session, code, requested);

        var env = RcloneConfig.Build(mapping, session.Account, session.Credentials, session.Protocol);
        process.Start(mapping, session.Protocol, env);

        var deadline = DateTime.UtcNow + MountTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (session.State == MountState.Error)
                throw new InvalidOperationException(
                    session.LastMessage ?? "rclone exited before the mount became available.");

            if (MountPointExists(mapping))
            {
                session.MountedSinceUtc = DateTime.UtcNow;
                SetState(session, MountState.Mounted);
                ApplyWindowsPresentation(mapping);
                return;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        await process.StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        var detail = process.LastErrorLine();
        SetState(session, MountState.Error,
            detail ?? $"Timed out waiting for {mapping.MountPoint} to appear.");
        throw new TimeoutException(
            $"Mounting {mapping.MountPoint} timed out." + (detail is null ? string.Empty : " " + detail));
    }

    /// <summary>
    /// The Windows-side finishing touches a disk-mode mount needs: a branded icon, and the Recycle
    /// Bin dealt with. Both are best-effort — a mount that works but looks generic is a far better
    /// outcome than a mount that fails because a registry write was denied.
    /// </summary>
    private void ApplyWindowsPresentation(Mapping mapping)
    {
        if (mapping.PresentAsNetworkDrive) return; // A network drive has neither problem.

        try
        {
            // Deletes on a fixed disk would otherwise be copied into a $RECYCLE.BIN on the remote,
            // where they go on being billed. Per-user, so it only takes effect for whoever is running
            // this process; PurgeRecycleBin below is the part that works regardless.
            DriveAppearance.SetNukeOnDelete(mapping.MountPoint);
            DriveAppearance.PurgeRecycleBin(mapping.MountPoint);

            if (mapping.UseCustomDriveIcon
                && mapping.MountTarget == MountTarget.DriveLetter
                && !string.IsNullOrWhiteSpace(DriveIconPath))
            {
                DriveAppearance.SetDriveIcon(mapping.DriveLetter, DriveIconPath!, mapping.VolumeLabel);
            }
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke(this, new MountLogEventArgs
            {
                MappingId = mapping.Id,
                Line = $"Drive presentation could not be applied: {ex.Message}",
            });
        }
    }

    private void OnExited(Session session, int exitCode, bool requested)
    {
        // An exit we asked for is handled by UnmountAsync.
        if (requested || session.State is MountState.Unmounting or MountState.Unmounted) return;

        if (session.State == MountState.Mounted && session.RestartCount < MaxAutoRestarts)
        {
            session.RestartCount++;
            SetState(session, MountState.Mounting,
                $"rclone exited (code {exitCode}); restarting, attempt {session.RestartCount} of {MaxAutoRestarts}.");
            _ = RestartAsync(session);
            return;
        }

        var detail = session.Process.LastErrorLine();
        SetState(session, MountState.Error,
            detail ?? $"rclone exited unexpectedly (code {exitCode}).");
    }

    private async Task RestartAsync(Session session)
    {
        try
        {
            // Back off a little. An immediate retry against a network that just dropped burns the
            // restart budget in under a second and reports failure before the link can come back.
            await Task.Delay(TimeSpan.FromSeconds(2 * session.RestartCount)).ConfigureAwait(false);

            session.Process.Dispose();
            session.Process = NewProcess();
            await StartAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetState(session, MountState.Error, $"Restart failed: {ex.Message}");
        }
    }

    public async Task UnmountAsync(Guid mappingId, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(mappingId, out var session)) return;

        SetState(session, MountState.Unmounting);
        await session.Process.StopAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        session.Process.Dispose();
        _sessions.TryRemove(mappingId, out _);

        if (session.Mapping.UseCustomDriveIcon
            && session.Mapping.MountTarget == MountTarget.DriveLetter
            && !string.IsNullOrWhiteSpace(DriveIconPath))
        {
            // Leave no branding on a letter that is about to belong to something else.
            try { DriveAppearance.ClearDriveIcon(session.Mapping.DriveLetter); } catch { /* cosmetic */ }
        }

        SetState(session, MountState.Unmounted);
    }

    public async Task UnmountAllAsync(CancellationToken ct = default)
    {
        foreach (var id in _sessions.Keys.ToArray())
        {
            try { await UnmountAsync(id, ct).ConfigureAwait(false); }
            catch { /* one stubborn mount must not block the rest of shutdown */ }
        }
    }

    private void SetState(Session session, MountState state, string? message = null)
    {
        session.State = state;
        session.LastMessage = message;
        if (state != MountState.Mounted) session.MountedSinceUtc = null;

        StatusChanged?.Invoke(this, new MountStatusChangedEventArgs
        {
            MappingId = session.Mapping.Id,
            State = state,
            Message = message,
        });
    }

    /// <summary>
    /// True once the mount point is live. Both forms are detected the same way because neither
    /// exists until WinFsp attaches: an unmounted drive letter has no root directory, and a
    /// directory mountpoint is created by the mount itself.
    /// </summary>
    private static bool MountPointExists(Mapping mapping)
    {
        var point = mapping.MountPoint;
        if (string.IsNullOrWhiteSpace(point)) return false;
        try
        {
            return mapping.MountTarget == MountTarget.Directory
                ? Directory.Exists(point)
                : Directory.Exists(point + Path.DirectorySeparatorChar);
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync() => await UnmountAllAsync().ConfigureAwait(false);

    private sealed class Session(
        Mapping mapping, Account account, Credentials credentials,
        StorageProtocol protocol, RcloneProcess process)
    {
        public Mapping Mapping { get; } = mapping;
        public Account Account { get; } = account;
        public Credentials Credentials { get; } = credentials;
        public StorageProtocol Protocol { get; } = protocol;
        public RcloneProcess Process { get; set; } = process;
        public MountState State { get; set; } = MountState.Unmounted;
        public string? LastMessage { get; set; }
        public DateTime? MountedSinceUtc { get; set; }
        public int RestartCount { get; set; }
    }
}
