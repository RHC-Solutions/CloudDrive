using System.Diagnostics;
using System.Text;
using CloudDrive.Core.Models;

namespace CloudDrive.Core.Mounting;

/// <summary>
/// One <c>rclone mount</c> child process: builds its arguments, injects the remote's config through
/// the environment, captures its log output, and terminates it on unmount.
///
/// Secrets reach rclone as environment variables rather than command-line arguments. A command line
/// is readable by any user on the machine through the process list; an environment block is not.
/// </summary>
public sealed class RcloneProcess : IDisposable
{
    private readonly string _exePath;
    private Process? _process;
    private volatile bool _stopRequested;

    public RcloneProcess(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("An rclone path is required.", nameof(exePath));
        _exePath = exePath;
    }

    /// <summary>Run the mount at DEBUG level.</summary>
    public bool VerboseLogging { get; init; }

    /// <summary>Raised for each line rclone writes. rclone logs to stderr, so both streams feed this.</summary>
    public event Action<string>? LogLineReceived;

    /// <summary>Raised when the process exits, with its exit code and whether we asked it to.</summary>
    public event Action<int, bool>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// The last few log lines, kept so a mount that dies during startup can report *why* rather than
    /// just "the process exited". rclone puts the useful message — bad credentials, host unreachable
    /// — in its final lines before quitting.
    /// </summary>
    public IReadOnlyList<string> RecentLog
    {
        get { lock (_recentLog) return _recentLog.ToArray(); }
    }

    private const int RecentLogCapacity = 40;
    private readonly Queue<string> _recentLog = new(RecentLogCapacity);

    public void Start(Mapping mapping, StorageProtocol protocol, IReadOnlyDictionary<string, string> remoteEnv)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(remoteEnv);

        if (IsRunning)
            throw new InvalidOperationException("This mount already has a running rclone process.");

        _stopRequested = false;

        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // rclone writes UTF-8. Without forcing it, .NET decodes with the console code page and
            // non-ASCII filenames (Cyrillic, Hebrew, CJK) arrive in the log as mojibake.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in RcloneArguments.BuildMount(mapping, protocol, VerboseLogging))
            psi.ArgumentList.Add(arg);
        foreach (var kv in remoteEnv)
            psi.Environment[kv.Key] = kv.Value;

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => OnLine(e.Data);
        _process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        _process.Exited += (_, _) => Exited?.Invoke(TryGetExitCode(), _stopRequested);

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void OnLine(string? line)
    {
        if (line is null) return;

        lock (_recentLog)
        {
            if (_recentLog.Count == RecentLogCapacity) _recentLog.Dequeue();
            _recentLog.Enqueue(line);
        }

        LogLineReceived?.Invoke(line);
    }

    private int TryGetExitCode()
    {
        try { return _process?.ExitCode ?? -1; }
        catch { return -1; }
    }

    /// <summary>
    /// Stops the mount and waits for the process to go. WinFsp notices the exit and releases the
    /// mount point.
    ///
    /// Killing rather than signalling is deliberate: rclone on Windows has no graceful-shutdown
    /// signal, and the VFS cache is written through on every operation, so there is nothing buffered
    /// to lose. The tree is killed because rclone can spawn helpers.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var proc = _process;
        if (proc is null || proc.HasExited) return;

        _stopRequested = true;

        try
        {
            proc.Kill(entireProcessTree: true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* timed out; best effort */ }
        catch (InvalidOperationException) { /* already exited */ }
    }

    /// <summary>
    /// The most likely explanation for a failed mount, taken from rclone's own output.
    ///
    /// rclone reports authentication and connectivity problems in its log and then exits with a
    /// generic code, so the exit code alone tells the user nothing. Surfacing the last line that
    /// looks like an error is far more useful than "exit code 1".
    /// </summary>
    public string? LastErrorLine()
    {
        foreach (var line in RecentLog.Reverse())
        {
            if (line.Contains("ERROR", StringComparison.Ordinal)
                || line.Contains("Fatal error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("couldn't connect", StringComparison.OrdinalIgnoreCase)
                || line.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
            {
                return line.Trim();
            }
        }
        return null;
    }

    public void Dispose()
    {
        try { _process?.Dispose(); } catch { /* ignore */ }
        _process = null;
    }
}
