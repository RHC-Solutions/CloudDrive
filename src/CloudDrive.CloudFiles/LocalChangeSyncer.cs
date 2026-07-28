using System.Collections.Concurrent;
using CloudDrive.Core.Sync;

namespace CloudDrive.CloudFiles;

/// <summary>
/// Watches an on-demand folder and pushes local changes back to Hetzner: new/edited files are
/// uploaded and marked in-sync, deletes remove the remote file (cascading for folders), and
/// renames become server-side moves (cascading for folders). A persisted
/// <see cref="SyncStateStore"/> lets it skip redundant uploads and recognise hydration/self writes
/// so they don't cause upload loops.
/// </summary>
internal sealed class LocalChangeSyncer : IDisposable
{
    private const int DebounceMs = 1500;
    private const int IgnoreSeconds = 20;

    /// <summary>
    /// How many files upload at once. Dropping a folder in used to fan out one unbounded task per
    /// file, so a thousand files meant a thousand simultaneous transfers competing for the link;
    /// a bounded queue finishes the same work sooner and keeps memory predictable.
    /// </summary>
    private const int MaxConcurrentUploads = 8;

    /// <summary>How many server-side moves run at once when a folder is renamed.</summary>
    private const int MaxConcurrentMoves = 8;

    private readonly string _syncRoot;
    private readonly string _prefix;
    private readonly IRemoteStorageClient _remote;
    private readonly CloudFilesProvider _provider;
    private readonly SyncStateStore _state;
    private readonly Action<string>? _log;

    private readonly ConcurrentDictionary<string, DateTime> _ignoreUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _uploadGate;
    private readonly Timer _debounce;
    private FileSystemWatcher? _watcher;

    public LocalChangeSyncer(string syncRoot, string prefix, IRemoteStorageClient remote,
        CloudFilesProvider provider, SyncStateStore state, Action<string>? log,
        int maxConcurrentUploads = MaxConcurrentUploads)
    {
        _syncRoot = syncRoot;
        _prefix = prefix;
        _remote = remote;
        _provider = provider;
        _state = state;
        _log = log;
        var limit = Math.Max(1, maxConcurrentUploads);
        _uploadGate = new SemaphoreSlim(limit, limit);
        _debounce = new Timer(_ => ProcessPending(), null, Timeout.Infinite, Timeout.Infinite);
        _provider.Hydrated += OnHydrated;
    }

    public void Start()
    {
        _watcher = new FileSystemWatcher(_syncRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) => QueueChange(e.FullPath);
        _watcher.Changed += (_, e) => QueueChange(e.FullPath);
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.EnableRaisingEvents = true;
        _log?.Invoke($"Two-way sync watching {_syncRoot} over {_remote.ProtocolName}");
    }

    /// <summary>After hydration, record the file's state so it isn't mistaken for a local edit.</summary>
    private void OnHydrated(string key)
    {
        var path = LocalPathForKey(key);
        Ignore(path);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            var known = _state.Get(key);
            _state.Set(new SyncEntry
            {
                Key = key,
                ETag = known?.ETag,
                Size = info.Length,
                LocalModifiedUtcTicks = info.LastWriteTimeUtc.Ticks,
                RemoteModifiedUtcTicks = known?.RemoteModifiedUtcTicks ?? 0,
            });
        }
        catch { /* best-effort */ }
    }

    private void Ignore(string path) => _ignoreUntil[path] = DateTime.UtcNow.AddSeconds(IgnoreSeconds);

    private bool IsIgnored(string path) =>
        _ignoreUntil.TryGetValue(path, out var until) && DateTime.UtcNow < until;

    private void QueueChange(string path)
    {
        if (Directory.Exists(path)) return;
        _pending[path] = 1;
        _debounce.Change(DebounceMs, Timeout.Infinite);
    }

    private void ProcessPending()
    {
        var paths = _pending.Keys.ToArray();
        foreach (var p in paths) _pending.TryRemove(p, out _);
        foreach (var path in paths)
            _ = Task.Run(() => UploadGatedAsync(path));
    }

    /// <summary>Runs one upload, waiting its turn so only a bounded number are in flight.</summary>
    private async Task UploadGatedAsync(string path)
    {
        await _uploadGate.WaitAsync().ConfigureAwait(false);
        try { await UploadIfNeededAsync(path).ConfigureAwait(false); }
        finally { _uploadGate.Release(); }
    }

    private async Task UploadIfNeededAsync(string path)
    {
        try
        {
            if (!File.Exists(path) || IsIgnored(path)) return;
            if (CloudFilesProvider.IsDehydrated(path)) return; // cloud-only placeholder, nothing local to push
            if (IsLocked(path)) { QueueChange(path); return; }

            var info = new FileInfo(path);
            var key = KeyForLocalPath(path);
            var known = _state.Get(key);

            var action = SyncReconciler.DecideUpload(
                info.Length, info.LastWriteTimeUtc.Ticks, known, remoteChangedSinceSync: false);
            if (action == UploadAction.Skip) return;

            Ignore(path); // suppress the writes our own in-sync marking will cause
            string? etag = null;
            await RemoteRetry.RunAsync(async () => etag = await _remote.PutAsync(key, path).ConfigureAwait(false), _log)
                .ConfigureAwait(false);

            try { _provider.MarkInSync(path); }
            catch { try { _provider.ConvertToPlaceholder(path, key); } catch { /* leave as normal file */ } }

            _state.Set(new SyncEntry
            {
                Key = key,
                ETag = etag,
                Size = info.Length,
                LocalModifiedUtcTicks = info.LastWriteTimeUtc.Ticks,
                RemoteModifiedUtcTicks = DateTime.UtcNow.Ticks,
            });
            _log?.Invoke($"Uploaded {key}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Upload of '{path}' failed: {ex.Message}");
        }
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        var path = e.FullPath;
        if (IsIgnored(path)) return;
        var key = KeyForLocalPath(path);
        // The path is already gone, so we can't tell file vs directory — handle both: delete the
        // exact entry and every tracked entry beneath it (folder cascade).
        var childPrefix = key + "/";
        var keysToDelete = new List<string> { key };
        keysToDelete.AddRange(_state.Keys.Where(k => k.StartsWith(childPrefix, StringComparison.Ordinal)));

        _ = Task.Run(async () =>
        {
            try
            {
                DeleteResult? result = null;
                await RemoteRetry.RunAsync(
                    async () => result = await _remote.DeleteManyAsync(keysToDelete).ConfigureAwait(false),
                    _log).ConfigureAwait(false);
                if (result is null) return;

                // Only forget the keys that actually went; the rest stay tracked so they aren't
                // silently orphaned on the remote.
                foreach (var k in result.Deleted)
                    _state.Remove(k);

                if (result.Deleted.Count == 1)
                    _log?.Invoke($"Deleted {result.Deleted[0]}");
                else if (result.Deleted.Count > 1)
                    _log?.Invoke($"Deleted {result.Deleted.Count} items under {key}");

                if (result.Failed.Count > 0)
                    _log?.Invoke($"Delete failed for {result.Failed.Count} item(s) under {key}: " +
                                 string.Join(", ", result.Failed.Take(5)));
            }
            catch (Exception ex) { _log?.Invoke($"Delete of '{key}' failed: {ex.Message}"); }
        });
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        var oldKey = KeyForLocalPath(e.OldFullPath);
        var newKey = KeyForLocalPath(e.FullPath);
        var isDirectory = Directory.Exists(e.FullPath);

        var moves = new List<(string From, string To)>();
        if (!isDirectory) moves.Add((oldKey, newKey));
        var oldPrefix = oldKey + "/";
        var newPrefix = newKey + "/";
        foreach (var k in _state.Keys.Where(k => k.StartsWith(oldPrefix, StringComparison.Ordinal)))
            moves.Add((k, newPrefix + k[oldPrefix.Length..]));

        _ = Task.Run(async () =>
        {
            // On the file protocols a move is one cheap server-side rename; on S3 it is a
            // copy + delete. Either way they're independent, so a bounded batch runs at once.
            await Parallel.ForEachAsync(
                moves,
                new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentMoves },
                async (move, _) =>
                {
                    var (from, to) = move;
                    try
                    {
                        await RemoteRetry.RunAsync(() => _remote.MoveAsync(from, to), _log).ConfigureAwait(false);
                        var known = _state.Get(from);
                        _state.Remove(from);
                        if (known is not null) { known.Key = to; _state.Set(known); }
                        _log?.Invoke($"Renamed {from} -> {to}");
                    }
                    catch (Exception ex) { _log?.Invoke($"Rename '{from}' failed: {ex.Message}"); }
                }).ConfigureAwait(false);
        });
    }

    private string KeyForLocalPath(string path)
    {
        var relative = Path.GetRelativePath(_syncRoot, path).Replace(Path.DirectorySeparatorChar, '/');
        return _prefix + relative;
    }

    private string LocalPathForKey(string key)
    {
        var relative = _prefix.Length > 0 && key.StartsWith(_prefix, StringComparison.Ordinal)
            ? key[_prefix.Length..]
            : key;
        return Path.Combine(_syncRoot, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool IsLocked(string path)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (IOException) { return true; }
        catch { return false; }
    }

    public void Dispose()
    {
        _provider.Hydrated -= OnHydrated;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _debounce.Dispose();
        _uploadGate.Dispose();
    }
}
