using System.Runtime.Versioning;
using System.Text;
using CloudDrive.Core.Stores;

namespace CloudDrive.Core;

/// <summary>
/// Daily rolling log files, with retention.
///
/// Deliberately not a logging framework. The service also writes to the Windows Event Log through
/// <c>Microsoft.Extensions.Logging</c>; this is the verbose companion an administrator reads when
/// the Event Log entry says a mount failed but not why, and it has to keep working when the machine
/// store is the thing that is broken.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileLogger : IDisposable
{
    private readonly string _directory;
    private readonly string _prefix;
    private readonly int _retentionDays;
    private readonly Lock _gate = new();
    private DateOnly _currentDay;
    private StreamWriter? _writer;

    /// <param name="directory">Where log files go. Defaults to the machine log directory.</param>
    /// <param name="prefix">File name prefix, so the service and the app do not share one file.</param>
    /// <param name="retentionDays">Days to keep. Older files are deleted on the first write of a day.</param>
    public FileLogger(string? directory = null, string prefix = "clouddrive", int retentionDays = 30)
    {
        _directory = directory ?? AppPaths.MachineLogsDir;
        _prefix = prefix;
        _retentionDays = Math.Max(1, retentionDays);
    }

    /// <summary>Raised for every line, so the UI can mirror the log without re-reading the file.</summary>
    public event Action<string>? LineWritten;

    public string Directory => _directory;

    public void Info(string message) => Write("INFO ", message);

    public void Warn(string message) => Write("WARN ", message);

    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception ex) =>
        Write("ERROR", $"{message} — {ex.GetType().Name}: {ex.Message}");

    /// <summary>Writes a line already formatted by something else, such as rclone's own output.</summary>
    public void Raw(string line) => Write(null, line);

    private void Write(string? level, string message)
    {
        var timestamp = DateTime.Now;
        var formatted = level is null
            ? $"[{timestamp:HH:mm:ss}] {message}"
            : $"[{timestamp:HH:mm:ss}] {level} {message}";

        lock (_gate)
        {
            try
            {
                EnsureWriter(DateOnly.FromDateTime(timestamp));
                _writer?.WriteLine(formatted);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down. A full disk or a locked directory means the
                // line is lost, and that is strictly better than a crash inside an error handler.
            }
        }

        LineWritten?.Invoke(formatted);
    }

    private void EnsureWriter(DateOnly day)
    {
        if (_writer is not null && day == _currentDay) return;

        _writer?.Flush();
        _writer?.Dispose();

        System.IO.Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{_prefix}-{day:yyyy-MM-dd}.log");

        // Share read-write so an administrator can tail the file, and so a second CloudDrive process
        // writing to the same directory does not fail to open it.
        var stream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };

        _currentDay = day;
        PruneOldFiles();
    }

    private void PruneOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-_retentionDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, $"{_prefix}-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    try { File.Delete(file); } catch { /* in use; next roll retries */ }
                }
            }
        }
        catch
        {
            // Retention is housekeeping, not correctness.
        }
    }

    /// <summary>The most recent <paramref name="lines"/> from today's file, for the UI's log pane.</summary>
    public IReadOnlyList<string> Tail(int lines = 500)
    {
        try
        {
            var path = Path.Combine(_directory, $"{_prefix}-{DateTime.Now:yyyy-MM-dd}.log");
            if (!File.Exists(path)) return [];

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var buffer = new Queue<string>(lines);
            while (reader.ReadLine() is { } line)
            {
                if (buffer.Count == lines) buffer.Dequeue();
                buffer.Enqueue(line);
            }
            return [.. buffer];
        }
        catch
        {
            return [];
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}
