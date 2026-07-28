using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudDrive.Core.Stores;

/// <summary>
/// A JSON file holding one <typeparamref name="T"/>, written atomically.
///
/// Atomic because the service watches these files and reloads on change: a reader that catches the
/// file mid-write would see truncated JSON and, worse, could conclude that zero mappings are
/// configured and unmount everything. Writing to a temp file and renaming means a reader sees either
/// the old content or the new one.
/// </summary>
public sealed class JsonFileStore<T> where T : class, new()
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Enums by name, not by number: the files are meant to be readable and hand-editable by an
        // administrator, and a renumbered enum must never silently repoint an account at the wrong
        // provider.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly bool _machineScope;

    public JsonFileStore(string path, bool machineScope)
    {
        _path = path;
        _machineScope = machineScope;
    }

    public string Path => _path;

    public bool Exists => File.Exists(_path);

    /// <summary>When the file last changed, for cheap change detection. Null when absent.</summary>
    public DateTime? LastWriteUtc => File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : null;

    /// <summary>
    /// Reads the file, returning a fresh <typeparamref name="T"/> when it is missing.
    ///
    /// A file that exists but does not parse throws rather than being silently replaced with
    /// defaults. Quietly discarding a corrupt mappings file would unmount everything and lose the
    /// configuration; failing loudly leaves the file on disk to be repaired.
    /// </summary>
    public T Load()
    {
        if (!File.Exists(_path)) return new T();

        string json;
        try
        {
            json = ReadShared(_path);
        }
        catch (IOException) when (File.Exists(_path))
        {
            // A concurrent writer briefly holds the file during the rename. One retry covers it.
            Thread.Sleep(50);
            json = ReadShared(_path);
        }

        if (string.IsNullOrWhiteSpace(json)) return new T();

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"'{_path}' is not valid CloudDrive configuration: {ex.Message}", ex);
        }
    }

    /// <summary>Loads, or returns null if the file is missing or unreadable. For non-critical state.</summary>
    public T? TryLoad()
    {
        try { return File.Exists(_path) ? Load() : null; }
        catch { return null; }
    }

    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (_machineScope) AppPaths.EnsureMachineStore();
        else AppPaths.EnsureUserStore();

        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(value, Options);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>
    /// Opens for reading while tolerating another process holding the file open. Without
    /// <see cref="FileShare.ReadWrite"/>, a read racing the service's own write throws.
    /// </summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
