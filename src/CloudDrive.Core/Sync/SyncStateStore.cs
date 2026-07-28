using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using CloudDrive.Core.Stores;

namespace CloudDrive.Core.Sync;

/// <summary>
/// Thread-safe, persisted map of remote key → last-synced <see cref="SyncEntry"/> for one mapping.
/// Stored as JSON under <c>%LOCALAPPDATA%\CloudDrive\sync\&lt;mappingId&gt;.json</c>. Lets the
/// reconciler tell local-only vs remote-only vs both-changed.
/// </summary>
public sealed class SyncStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, SyncEntry> _entries;

    public SyncStateStore(string filePath)
    {
        _filePath = filePath;
        _entries = Load(filePath);
    }

    /// <summary>
    /// Default per-mapping state file.
    ///
    /// Under the *user* store, not the machine store: an on-demand root lives in one user's profile
    /// and is synced by that user's session, so its state is per-user by construction. Two accounts
    /// with a mapping on the same remote keep separate state, which is correct — they have separate
    /// local folders.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string FilePathFor(Guid mappingId) =>
        Path.Combine(AppPaths.SyncStateDir, mappingId.ToString("N") + ".json");

    public SyncEntry? Get(string key) => _entries.TryGetValue(key, out var e) ? e : null;

    public void Set(SyncEntry entry)
    {
        _entries[entry.Key] = entry;
        Save();
    }

    public void Remove(string key)
    {
        if (_entries.TryRemove(key, out _)) Save();
    }

    public IReadOnlyCollection<string> Keys => _entries.Keys.ToArray();

    private static ConcurrentDictionary<string, SyncEntry> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var list = JsonSerializer.Deserialize<List<SyncEntry>>(json, Options);
                if (list is not null)
                    return new ConcurrentDictionary<string, SyncEntry>(
                        list.Where(e => !string.IsNullOrEmpty(e.Key))
                            .ToDictionary(e => e.Key, e => e));
            }
        }
        catch { /* start fresh on any corruption */ }
        return new ConcurrentDictionary<string, SyncEntry>();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(_entries.Values.ToList(), Options);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch { /* persistence is best-effort */ }
    }
}
