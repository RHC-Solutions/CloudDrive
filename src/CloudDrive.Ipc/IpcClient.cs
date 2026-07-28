using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace CloudDrive.Ipc;

/// <summary>Thrown when the service answers a request with an error.</summary>
public sealed class IpcException(string message) : Exception(message);

/// <summary>Thrown when the service cannot be reached at all.</summary>
public sealed class ServiceUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// The client half of the pipe, used by both the tray app and the CLI.
///
/// One persistent connection carries request/response traffic and server-pushed events together,
/// correlated by id. A connection per request would be simpler, but the UI needs live mount state
/// and log lines, and polling for those would be both laggy and wasteful.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IpcClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private Task? _reader;

    /// <summary>Raised for every event pushed by the service.</summary>
    public event Action<IpcOperation, JsonElement?>? EventReceived;

    /// <summary>Raised when the connection drops, so the UI can show a banner and retry.</summary>
    public event Action<Exception?>? Disconnected;

    public bool IsConnected => _pipe is { IsConnected: true };

    /// <summary>
    /// Connects to the service.
    ///
    /// A short timeout on purpose: the service either is running or is not, and a UI that hangs for
    /// thirty seconds on startup because the service is stopped is worse than one that says so
    /// immediately and offers to start it.
    /// </summary>
    public async Task ConnectAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(
            ".", IpcServer.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous,
            // Refuse to let the server impersonate us. The service is LocalSystem and needs no help
            // from a client's token; granting it would let a compromised service act as the user.
            TokenImpersonationLevel.Identification);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
        {
            pipe.Dispose();
            throw new ServiceUnavailableException(
                "The CloudDrive service is not responding. It may be stopped — check Services, or run "
                + "'cdrive service start' from an elevated prompt.", ex);
        }

        _pipe = pipe;
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        _reader = Task.Run(() => ReadLoopAsync(_shutdown.Token), CancellationToken.None);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        Exception? fault = null;
        try
        {
            using var reader = new StreamReader(_pipe!, Encoding.UTF8, false, 8192, leaveOpen: true);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;

                IpcMessage? message;
                try { message = JsonSerializer.Deserialize<IpcMessage>(line, IpcJson.Options); }
                catch (JsonException) { continue; }
                if (message is null) continue;

                if (message.Kind == IpcMessageKind.Event)
                {
                    EventReceived?.Invoke(message.Operation, message.Payload);
                }
                else if (message.Id is { } id && _pending.TryRemove(id, out var waiter))
                {
                    waiter.TrySetResult(message);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { fault = ex; }
        finally
        {
            // Never leave a caller awaiting a reply that can no longer arrive.
            foreach (var (_, waiter) in _pending)
            {
                waiter.TrySetException(new ServiceUnavailableException(
                    "The connection to the CloudDrive service was lost.", fault));
            }
            _pending.Clear();

            if (!ct.IsCancellationRequested) Disconnected?.Invoke(fault);
        }
    }

    /// <summary>Sends a request and returns the deserialised response body.</summary>
    public async Task<TResult?> CallAsync<TResult>(
        IpcOperation operation, object? payload = null, CancellationToken ct = default)
    {
        var response = await SendAsync(operation, payload, ct).ConfigureAwait(false);
        return IpcJson.Deserialize<TResult>(response.Payload);
    }

    /// <summary>Sends a request whose response body is not needed.</summary>
    public async Task CallAsync(IpcOperation operation, object? payload = null, CancellationToken ct = default) =>
        await SendAsync(operation, payload, ct).ConfigureAwait(false);

    private async Task<IpcMessage> SendAsync(IpcOperation operation, object? payload, CancellationToken ct)
    {
        if (_writer is null || _pipe is not { IsConnected: true })
            throw new ServiceUnavailableException("Not connected to the CloudDrive service.");

        var id = Guid.NewGuid().ToString("N");
        var message = new IpcMessage
        {
            Id = id,
            Kind = IpcMessageKind.Request,
            Operation = operation,
            Payload = payload is null
                ? null
                : JsonSerializer.SerializeToElement(payload, payload.GetType(), IpcJson.Options),
        };

        var waiter = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        try
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try { await _writer.WriteLineAsync(IpcJson.Serialize(message)).ConfigureAwait(false); }
            finally { _writeGate.Release(); }

            // A bounded wait, so a service wedged mid-operation surfaces as an error rather than a
            // UI that never repaints. Generous enough for a mount, which waits on a network.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(3));

            var response = await waiter.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            if (response.Error is { } error) throw new IpcException(error);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            throw new ServiceUnavailableException($"The service did not answer '{operation}' in time.");
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    /// <summary>Asks the service to push live events on this connection.</summary>
    public Task SubscribeAsync(CancellationToken ct = default) =>
        CallAsync(IpcOperation.Subscribe, null, ct);

    /// <summary>Whether the service is reachable, without throwing. For a startup check.</summary>
    public static async Task<bool> IsServiceRunningAsync(CancellationToken ct = default)
    {
        await using var client = new IpcClient();
        try
        {
            await client.ConnectAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            await client.CallAsync(IpcOperation.Ping, null, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try { _writer?.Dispose(); } catch { /* closing */ }
        try { _pipe?.Dispose(); } catch { /* closing */ }

        if (_reader is not null)
        {
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* shutting down */ }
        }

        _writeGate.Dispose();
        _shutdown.Dispose();
    }
}
