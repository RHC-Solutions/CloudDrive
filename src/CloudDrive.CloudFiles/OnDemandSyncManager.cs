using System.Runtime.Versioning;
using System.Text;
using CloudDrive.Core.Models;
using CloudDrive.Core.Stores;
using CloudDrive.Core.Sync;

namespace CloudDrive.CloudFiles;

/// <summary>
/// Orchestrates one mapping's Files On-Demand folder: builds the protocol client and
/// <see cref="CloudFilesProvider"/>, registers/connects the sync root, populates placeholders from
/// the remote listing, pushes local changes back up (two-way), periodically pulls remote changes,
/// and exposes pin / free-up-space / auto-dehydrate operations.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OnDemandSyncManager : IDisposable
{
    private readonly Mapping _mapping;
    private readonly Account _account;
    private readonly string _prefix;
    private readonly IRemoteStorageClient _remote;
    private readonly CloudFilesProvider _provider;
    private readonly SyncStateStore _state;
    private readonly Action<string>? _log;
    private LocalChangeSyncer? _syncer;
    private Timer? _remotePull;

    public OnDemandSyncManager(
        Mapping mapping, Account account, Credentials credentials, Action<string>? log = null)
    {
        _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _log = log;
        _prefix = mapping.KeyPrefix;
        _remote = RemoteStorageClientFactory.Create(mapping, account, credentials, log);
        _state = new SyncStateStore(SyncStateStore.FilePathFor(mapping.Id));

        SyncRootPath = ResolveFolderPath(mapping);
        _provider = new CloudFilesProvider(
            SyncRootPath,
            providerId: mapping.Id,
            syncRootIdentity: Encoding.UTF8.GetBytes(mapping.RemoteName),
            openRead: (key, offset, length, ct) => _remote.OpenReadAsync(key, offset, length, ct),
            log: log);
    }

    /// <summary>The local folder that appears in Explorer.</summary>
    public string SyncRootPath { get; }

    /// <summary>The protocol this folder is actually talking over, for the UI and logs.</summary>
    public string ProtocolName => _remote.ProtocolName;

    /// <summary>True when this mapping's back end can mint a time-limited share URL.</summary>
    public bool SupportsShareLinks => _remote.SupportsShareLinks;

    /// <summary>Default folder: %USERPROFILE%\CloudDrive\&lt;name&gt; when none is configured.</summary>
    public static string ResolveFolderPath(Mapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (!string.IsNullOrWhiteSpace(mapping.LocalFolderPath))
            return mapping.LocalFolderPath!;

        var leaf = !string.IsNullOrWhiteSpace(mapping.Name) ? mapping.Name
            : !string.IsNullOrWhiteSpace(mapping.Container) ? mapping.Container
            : "Storage";

        return Path.Combine(AppPaths.DefaultOnDemandRoot, Sanitize(leaf));
    }

    /// <summary>
    /// Registers + connects the sync root, populates placeholders from the remote, and starts
    /// watching for local changes to push back up (two-way).
    /// </summary>
    public async Task EnableAsync(CancellationToken ct = default)
    {
        _provider.Register();
        ApplyBranding();
        _provider.Connect();
        await PopulateAsync(ct).ConfigureAwait(false);

        // Start the watcher after population so the initial placeholders don't look like new files.
        _syncer = new LocalChangeSyncer(SyncRootPath, _prefix, _remote, _provider, _state, _log,
            maxConcurrentUploads: UploadConcurrencyFor(_account.EffectiveProtocol));
        _syncer.Start();

        // Periodically pull remote changes (new/updated/deleted files) into the folder.
        _remotePull = new Timer(_ => _ = ReconcileRemoteAsync(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// How many uploads may overlap. SFTP is the outlier: every concurrent transfer holds one of
    /// the Storage Box's small pool of SSH sessions, so the fan-out has to stay inside the pool
    /// the client maintains rather than queueing behind it.
    /// </summary>
    private static int UploadConcurrencyFor(StorageProtocol protocol) => protocol switch
    {
        StorageProtocol.Sftp => 4,
        StorageProtocol.WebDav => 6,
        _ => 8,
    };

    /// <summary>Disconnects hydration and local-change syncing but leaves the folder in place.</summary>
    public void Disable()
    {
        _remotePull?.Dispose();
        _remotePull = null;
        _syncer?.Dispose();
        _syncer = null;
        _provider.Disconnect();
    }

    /// <summary>Disconnects and removes the sync-root registration (keeps local files).</summary>
    public void Unregister() => _provider.Unregister();

    public void Pin(string fullPath) => _provider.SetPinned(fullPath, pinned: true);
    public void Unpin(string fullPath) => _provider.SetPinned(fullPath, pinned: false);
    public void FreeUpSpace(string fullPath) => _provider.Dehydrate(fullPath);

    /// <summary>A time-limited share link, or null when the protocol has no equivalent.</summary>
    public string? CreateShareLink(string key, TimeSpan expiresIn) => _remote.CreateShareLink(key, expiresIn);

    /// <summary>
    /// Populates the local namespace with placeholders for every remote file (under the mapping's
    /// optional sub-path). Existing entries are skipped, so it is safe to re-run.
    /// </summary>
    public async Task PopulateAsync(CancellationToken ct = default)
    {
        var byDirectory = new Dictionary<string, List<PlaceholderInfo>>(StringComparer.OrdinalIgnoreCase);

        await foreach (var item in _remote.ListAsync(_prefix, ct).ConfigureAwait(false))
        {
            if (item.Key.EndsWith('/')) continue; // directory marker
            if (!TrySplitKey(item.Key, out var relativeDir, out var fileName)) continue;

            var fullPath = LocalPathForRelative(relativeDir, fileName);
            RecordRemoteState(item); // track the change token so future remote edits are detectable
            if (File.Exists(fullPath)) continue; // already created/hydrated

            if (!byDirectory.TryGetValue(relativeDir, out var list))
                byDirectory[relativeDir] = list = new List<PlaceholderInfo>();
            list.Add(new PlaceholderInfo(fileName, item.Key, item.Size, item.LastModifiedUtc));
        }

        var created = 0;
        foreach (var (dir, files) in byDirectory)
        {
            try { created += _provider.CreatePlaceholders(dir, files); }
            catch (Exception ex) { _log?.Invoke($"Placeholder creation in '{dir}' failed: {ex.Message}"); }
        }
        _log?.Invoke($"On-demand folder '{SyncRootPath}' ({_remote.ProtocolName}): {created} placeholder(s) created.");
    }

    /// <summary>
    /// Pulls remote changes into the folder: new files become placeholders, files deleted remotely
    /// have their (clean, cloud-only) placeholders removed, and remote updates refresh cloud-only
    /// placeholders. Hydrated or locally-changed files are never overwritten — those are logged as
    /// conflicts (local wins).
    /// </summary>
    public async Task ReconcileRemoteAsync(CancellationToken ct = default)
    {
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            await foreach (var item in _remote.ListAsync(_prefix, ct).ConfigureAwait(false))
            {
                if (item.Key.EndsWith('/')) continue;
                if (!TrySplitKey(item.Key, out var relativeDir, out var fileName)) continue;
                seen.Add(item.Key);

                var fullPath = LocalPathForRelative(relativeDir, fileName);
                var known = _state.Get(item.Key);
                var localExists = File.Exists(fullPath);
                var localDirty = IsLocalDirty(fullPath, known);

                var action = SyncReconciler.DecideRemote(item.ETag, remoteExists: true, known, localExists, localDirty);
                switch (action)
                {
                    case RemoteAction.CreatePlaceholder:
                        try
                        {
                            _provider.CreatePlaceholders(relativeDir,
                                new[] { new PlaceholderInfo(fileName, item.Key, item.Size, item.LastModifiedUtc) });
                            RecordRemoteState(item);
                        }
                        catch (Exception ex) { _log?.Invoke($"Pull-create '{item.Key}' failed: {ex.Message}"); }
                        break;

                    case RemoteAction.UpdatePlaceholder:
                        // Only safe to refresh when there is no local data to lose.
                        if (CloudFilesProvider.IsDehydrated(fullPath))
                        {
                            try { File.Delete(fullPath); } catch { /* ignore */ }
                            try
                            {
                                _provider.CreatePlaceholders(relativeDir,
                                    new[] { new PlaceholderInfo(fileName, item.Key, item.Size, item.LastModifiedUtc) });
                                RecordRemoteState(item);
                            }
                            catch (Exception ex) { _log?.Invoke($"Pull-update '{item.Key}' failed: {ex.Message}"); }
                        }
                        else
                        {
                            _log?.Invoke($"Conflict (remote changed, local present): {item.Key} — keeping local.");
                        }
                        break;

                    case RemoteAction.Conflict:
                        _log?.Invoke($"Conflict on {item.Key} — keeping local copy.");
                        break;
                }
            }

            // Files we tracked but that are gone remotely: remove clean cloud-only placeholders.
            foreach (var key in _state.Keys)
            {
                if (seen.Contains(key)) continue;
                if (!TrySplitKey(key, out var dir, out var name)) { _state.Remove(key); continue; }
                var fullPath = LocalPathForRelative(dir, name);
                if (!File.Exists(fullPath)) { _state.Remove(key); continue; }
                if (CloudFilesProvider.IsDehydrated(fullPath))
                {
                    try { File.Delete(fullPath); _state.Remove(key); _log?.Invoke($"Removed (deleted remotely): {key}"); }
                    catch (Exception ex) { _log?.Invoke($"Remove '{key}' failed: {ex.Message}"); }
                }
                else
                {
                    _log?.Invoke($"Conflict (deleted remotely, local present): {key} — keeping local.");
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Remote reconcile failed: {ex.Message}");
        }
    }

    private bool IsLocalDirty(string fullPath, SyncEntry? known)
    {
        try
        {
            if (!File.Exists(fullPath)) return false;
            if (CloudFilesProvider.IsDehydrated(fullPath)) return false; // no local data
            var info = new FileInfo(fullPath);
            return known is null
                || known.Size != info.Length
                || known.LocalModifiedUtcTicks != info.LastWriteTimeUtc.Ticks;
        }
        catch { return false; }
    }

    private void RecordRemoteState(RemoteEntry item) => _state.Set(new SyncEntry
    {
        Key = item.Key,
        ETag = item.ETag,
        Size = item.Size,
        RemoteModifiedUtcTicks = item.LastModifiedUtc.Ticks,
    });

    private bool TrySplitKey(string key, out string relativeDir, out string fileName)
    {
        relativeDir = string.Empty;
        fileName = string.Empty;
        var relativeKey = _prefix.Length > 0 && key.StartsWith(_prefix, StringComparison.Ordinal)
            ? key[_prefix.Length..]
            : key;
        relativeKey = relativeKey.TrimStart('/');
        if (relativeKey.Length == 0) return false;
        var segments = relativeKey.Split('/');
        fileName = segments[^1];
        relativeDir = segments.Length > 1 ? string.Join(Path.DirectorySeparatorChar, segments[..^1]) : string.Empty;
        return true;
    }

    private string LocalPathForRelative(string relativeDir, string fileName) =>
        Path.Combine(SyncRootPath, relativeDir.Length == 0 ? fileName : Path.Combine(relativeDir, fileName));

    /// <summary>
    /// "Return to cloud after some time": dehydrates hydrated, unpinned files whose last access is
    /// older than <paramref name="idleFor"/>. Windows Storage Sense can also do this automatically
    /// (we enable that policy), but this gives us an explicit fallback.
    /// </summary>
    public int DehydrateIdle(TimeSpan idleFor)
    {
        if (!Directory.Exists(SyncRootPath)) return 0;
        var cutoff = DateTime.Now - idleFor;
        var dehydrated = 0;

        foreach (var file in Directory.EnumerateFiles(SyncRootPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                // A cloud-only placeholder carries RECALL_ON_DATA_ACCESS — already dehydrated, skip.
                if (((int)info.Attributes & FileAttributeRecallOnDataAccess) != 0) continue;
                // Pinned files carry the PINNED attribute; leave them on disk.
                if (((int)info.Attributes & FileAttributePinned) != 0) continue;
                if (info.LastAccessTime > cutoff) continue;
                _provider.Dehydrate(file);
                dehydrated++;
            }
            catch (Exception ex) { _log?.Invoke($"Dehydrate '{file}' failed: {ex.Message}"); }
        }
        if (dehydrated > 0) _log?.Invoke($"Auto-dehydrated {dehydrated} idle file(s) in '{SyncRootPath}'.");
        return dehydrated;
    }

    // Cloud Files placeholder attributes (winnt.h). Not all are in the .NET FileAttributes enum.
    private const int FileAttributeRecallOnDataAccess = 0x00400000;
    private const int FileAttributePinned = 0x00080000;

    private void ApplyBranding()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;

        var iconResource = exe + ",0";
        FolderBranding.Apply(SyncRootPath, iconResource,
            tooltip: $"CloudDrive — {_mapping.RemoteDescription} ({_remote.ProtocolName}, Files On-Demand)");

        var displayName = string.IsNullOrWhiteSpace(_mapping.Name)
            ? _mapping.RemoteDescription
            : _mapping.Name;
        NavPaneRegistration.Register(_mapping.Id, $"CloudDrive - {displayName}", SyncRootPath, iconResource);
    }

    /// <summary>Removes the Explorer sidebar entry for a mapping (used when it is deleted).</summary>
    public static void RemoveNavPaneEntry(Guid mappingId) => NavPaneRegistration.Unregister(mappingId);

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    public void Dispose()
    {
        _remotePull?.Dispose();
        _syncer?.Dispose();
        _provider.Dispose();
        _remote.Dispose();
    }
}
