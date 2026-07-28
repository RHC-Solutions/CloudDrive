using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using CloudDrive.Core.Models;

namespace CloudDrive.Core.Mounting;

/// <summary>How one protocol fared in the benchmark.</summary>
/// <param name="Protocol">The protocol measured.</param>
/// <param name="Reachable">Whether the TCP port answered at all.</param>
/// <param name="HandshakeMs">TCP connect time, or null when unreachable.</param>
/// <param name="UploadMbps">Measured upload throughput, or null when the transfer failed.</param>
/// <param name="DownloadMbps">Measured download throughput, or null when the transfer failed.</param>
/// <param name="Note">Why it was skipped or how it failed.</param>
public sealed record ProtocolMeasurement(
    StorageProtocol Protocol,
    bool Reachable,
    double? HandshakeMs,
    double? UploadMbps,
    double? DownloadMbps,
    string? Note)
{
    /// <summary>
    /// Combined throughput, used for ranking. Both directions are folded into one number because a
    /// protocol that downloads quickly and uploads at a crawl should not win on half the story.
    /// </summary>
    public double? CombinedMbps => UploadMbps is { } up && DownloadMbps is { } down
        ? 2 * up * down / (up + down) // harmonic mean: penalises a bad direction properly
        : null;

    public bool Usable => CombinedMbps is > 0;
}

/// <summary>The outcome of a full benchmark run.</summary>
public sealed record ProtocolSelection(
    StorageProtocol Winner,
    IReadOnlyList<ProtocolMeasurement> Measurements,
    DateTime MeasuredUtc);

/// <summary>
/// Measures which protocol is actually fastest to a given account from this machine, rather than
/// guessing from a table.
///
/// Which one wins genuinely depends on where the user is. SMB can be the fastest option on a
/// low-latency link and completely blocked on a coffee-shop network; WebDAV is often the only one
/// that survives a corporate firewall; SFTP is the one that is always switched on. A hard-coded
/// preference order would be wrong for a large fraction of users, and wrong in a way they could not
/// diagnose.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProtocolSelector
{
    private readonly string _rcloneExePath;
    private readonly Action<string>? _log;

    public ProtocolSelector(string rcloneExePath, Action<string>? log = null)
    {
        _rcloneExePath = rcloneExePath;
        _log = log;
    }

    /// <summary>Payload size per direction, in MiB.</summary>
    public int TestPayloadMiB { get; init; } = 16;

    /// <summary>How long to wait for a TCP handshake before calling a port unreachable.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long one direction of one protocol may take before it is abandoned.</summary>
    public TimeSpan TransferTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether <paramref name="account"/> needs measuring: it is on Auto, has more than one protocol
    /// to choose between, and the cached answer is missing or stale.
    /// </summary>
    public static bool NeedsMeasurement(Account account, int cacheDays = 14)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (account.Protocol != StorageProtocol.Auto) return false;
        if (!account.Descriptor.SupportsProtocolBenchmark) return false;
        if (account.ResolvedProtocol is null || account.ProtocolMeasuredUtc is null) return true;

        return DateTime.UtcNow - account.ProtocolMeasuredUtc.Value > TimeSpan.FromDays(Math.Max(1, cacheDays));
    }

    /// <summary>
    /// Probes, measures and ranks every protocol the account and its credentials can use.
    ///
    /// Protocols are measured strictly one at a time. Running them concurrently would have them
    /// competing for the same uplink and would measure contention rather than capability.
    /// </summary>
    public async Task<ProtocolSelection> SelectFastestAsync(
        Mapping mapping, Account account, Credentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(credentials);

        var candidates = account.Descriptor.Protocols
            .Where(p => p != StorageProtocol.Auto)
            .ToList();

        var measurements = new List<ProtocolMeasurement>();

        foreach (var protocol in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (!credentials.SupportsProtocol(protocol))
            {
                // Worth saying rather than silently skipping: a key-only account quietly restricting
                // itself to SFTP is exactly the kind of thing a user should be told about.
                measurements.Add(new ProtocolMeasurement(protocol, false, null, null, null,
                    "The stored credentials cannot authenticate over this protocol."));
                continue;
            }

            var port = PortFor(protocol, account);
            var handshake = await ProbePortAsync(account.Host, port, ct).ConfigureAwait(false);
            if (handshake is null)
            {
                // Reporting unreachable separately from slow is the difference between "SMB is slow
                // here" and "your ISP blocks port 445", which are different problems with different
                // fixes.
                measurements.Add(new ProtocolMeasurement(protocol, false, null, null, null,
                    $"Port {port} did not answer."));
                _log?.Invoke($"{protocol}: port {port} unreachable.");
                continue;
            }

            _log?.Invoke($"{protocol}: connected in {handshake:F0} ms, measuring throughput…");
            var measurement = await MeasureAsync(mapping, account, credentials, protocol, handshake.Value, ct)
                .ConfigureAwait(false);
            measurements.Add(measurement);

            if (measurement.CombinedMbps is { } mbps)
                _log?.Invoke($"{protocol}: {mbps:F1} Mbit/s combined.");
            else
                _log?.Invoke($"{protocol}: {measurement.Note}");
        }

        var winner = PickWinner(measurements, account);
        _log?.Invoke($"Chose {winner}.");
        return new ProtocolSelection(winner, measurements, DateTime.UtcNow);
    }

    /// <summary>
    /// The fastest usable protocol; failing that, the fastest to answer its port; failing that, the
    /// provider's declared fallback. There is always an answer, because refusing to mount because a
    /// benchmark was inconclusive would be worse than mounting over a reasonable default.
    /// </summary>
    private static StorageProtocol PickWinner(IReadOnlyList<ProtocolMeasurement> measurements, Account account)
    {
        var fastest = measurements
            .Where(m => m.Usable)
            .MaxBy(m => m.CombinedMbps);
        if (fastest is not null) return fastest.Protocol;

        var reachable = measurements
            .Where(m => m.Reachable && m.HandshakeMs is not null)
            .MinBy(m => m.HandshakeMs);
        return reachable?.Protocol ?? account.Descriptor.FallbackProtocol;
    }

    private static int PortFor(StorageProtocol protocol, Account account) => protocol switch
    {
        StorageProtocol.Sftp => account.EffectivePort > 0 ? account.EffectivePort : 22,
        StorageProtocol.Smb => 445,
        StorageProtocol.WebDav => account.UseTls ? 443 : 80,
        StorageProtocol.Ftp => account.EffectivePort > 0 ? account.EffectivePort : 21,
        _ => 443,
    };

    /// <summary>TCP handshake time in milliseconds, or null when the port does not answer.</summary>
    private async Task<double?> ProbePortAsync(string host, int port, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;

        using var client = new TcpClient();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return stopwatch.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Transfers a real payload up and back down over one protocol.
    ///
    /// A real transfer rather than a synthetic latency estimate, because throughput on these
    /// back ends is dominated by things latency does not predict: SFTP's window scaling, SMB's
    /// signing overhead, whether TLS is terminated near the user. The payload is random so no layer
    /// in between can compress it into a flattering result.
    /// </summary>
    private async Task<ProtocolMeasurement> MeasureAsync(
        Mapping mapping, Account account, Credentials credentials,
        StorageProtocol protocol, double handshakeMs, CancellationToken ct)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "CloudDrive.bench." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        var payloadBytes = Math.Max(1, TestPayloadMiB) * 1024L * 1024L;
        var localUp = Path.Combine(scratch, "payload.bin");
        var remoteName = $".clouddrive-benchmark-{Guid.NewGuid():N}.tmp";

        try
        {
            await WriteRandomAsync(localUp, payloadBytes, ct).ConfigureAwait(false);

            var env = RcloneConfig.Build(mapping, account, credentials, protocol);
            var remote = mapping.RemoteTargetFor(protocol).TrimEnd('/');

            var upSeconds = await RunRcloneAsync(
                ["copyto", localUp, $"{remote}/{remoteName}"], env, ct).ConfigureAwait(false);
            if (upSeconds is null)
                return new ProtocolMeasurement(protocol, true, handshakeMs, null, null, "Upload failed.");

            var localDown = Path.Combine(scratch, "roundtrip.bin");
            var downSeconds = await RunRcloneAsync(
                ["copyto", $"{remote}/{remoteName}", localDown], env, ct).ConfigureAwait(false);

            // Always try to remove the probe file, even when the download failed — otherwise a
            // failed benchmark leaves litter on the user's paid storage.
            await RunRcloneAsync(["deletefile", $"{remote}/{remoteName}"], env, CancellationToken.None)
                .ConfigureAwait(false);

            if (downSeconds is null)
                return new ProtocolMeasurement(protocol, true, handshakeMs, Mbps(payloadBytes, upSeconds.Value),
                    null, "Download failed.");

            return new ProtocolMeasurement(
                protocol, true, handshakeMs,
                Mbps(payloadBytes, upSeconds.Value),
                Mbps(payloadBytes, downSeconds.Value),
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProtocolMeasurement(protocol, true, handshakeMs, null, null, ex.Message);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* temp */ }
        }
    }

    private static double Mbps(long bytes, double seconds) =>
        seconds <= 0 ? 0 : bytes * 8 / seconds / 1_000_000;

    private static async Task WriteRandomAsync(string path, long bytes, CancellationToken ct)
    {
        await using var stream = File.Create(path);
        var buffer = new byte[1024 * 1024];
        Random.Shared.NextBytes(buffer);

        var written = 0L;
        while (written < bytes)
        {
            var chunk = (int)Math.Min(buffer.Length, bytes - written);
            await stream.WriteAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);
            written += chunk;
        }
    }

    /// <summary>Runs one rclone command, returning how long it took or null when it failed.</summary>
    private async Task<double?> RunRcloneAsync(
        string[] arguments, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _rcloneExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        // Keep the measurement about the link, not about rclone's retry logic papering over a
        // failure — a protocol that needs three attempts is not the fast one.
        psi.ArgumentList.Add("--retries");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--low-level-retries");
        psi.ArgumentList.Add("1");
        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var process = Process.Start(psi);
        if (process is null) return null;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TransferTimeout);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return null;
        }

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            _log?.Invoke(error.Split('\n').LastOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal))?.Trim()
                         ?? $"rclone exited with code {process.ExitCode}.");
            return null;
        }

        return stopwatch.Elapsed.TotalSeconds;
    }
}
