using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace CloudDrive.Ipc;

/// <summary>Who is on the other end of a pipe connection.</summary>
/// <param name="Sid">The caller's SID, used to decide what they may see.</param>
/// <param name="Name">DOMAIN\user, for log lines.</param>
/// <param name="IsAdministrator">Whether the caller's token carries the Administrators group.</param>
public sealed record IpcCaller(string Sid, string Name, bool IsAdministrator);

/// <summary>A request plus who asked, handed to the service's dispatcher.</summary>
public sealed record IpcRequest(IpcOperation Operation, JsonElement? Payload, IpcCaller Caller)
{
    public T? Body<T>() => IpcJson.Deserialize<T>(Payload);

    /// <summary>
    /// Throws unless the caller is an administrator. Every configuration change goes through this:
    /// the mappings the service mounts name arbitrary mount points and remote paths, so being able
    /// to edit them is equivalent to controlling what the LocalSystem service does.
    /// </summary>
    public void RequireAdministrator()
    {
        if (!Caller.IsAdministrator)
            throw new UnauthorizedAccessException(
                "Changing CloudDrive's configuration requires administrator rights.");
    }
}

/// <summary>
/// The named-pipe server the service runs, and the only way anything talks to it.
///
/// <para><b>The ACL is the security boundary.</b> Interactive users need to reach the pipe — the tray
/// app is a client — so it cannot simply be locked to Administrators. Instead every authenticated
/// user may connect and issue read-only calls, and the dispatcher authorises each operation against
/// the caller's own token, obtained by impersonation rather than taken on trust from the message.
/// Anonymous and network logons are refused outright: this is a local IPC channel and a remote
/// caller has no business on it.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IpcServer : IAsyncDisposable
{
    /// <summary>Pipe name. <c>\\.\pipe\CloudDrive</c> in full.</summary>
    public const string PipeName = "CloudDrive";

    private const int MaxConcurrentConnections = 16;

    private readonly Func<IpcRequest, CancellationToken, Task<object?>> _dispatch;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _acceptors = [];

    public IpcServer(Func<IpcRequest, CancellationToken, Task<object?>> dispatch, Action<string>? log = null)
    {
        _dispatch = dispatch;
        _log = log;
    }

    /// <summary>Starts accepting connections. Returns immediately.</summary>
    public void Start()
    {
        // Several listeners rather than one, so a slow client cannot block everyone else from
        // connecting while the single instance is busy being handed over.
        for (var i = 0; i < 4; i++)
            _acceptors.Add(Task.Run(() => AcceptLoopAsync(_shutdown.Token)));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var handling = pipe;
                pipe = null; // ownership moves to the handler
                _ = Task.Run(() => HandleConnectionAsync(handling, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"IPC accept failed: {ex.Message}");
                // Do not spin at full speed if the pipe cannot be created at all.
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    /// <summary>
    /// Creates a listener with the ACL applied at creation time.
    ///
    /// The ACL has to be set when the pipe instance is created, not afterwards: between creation and
    /// a later <c>SetAccessControl</c> the pipe would briefly carry the default descriptor, and a
    /// client that connected in that window would have got in under the wrong rules.
    /// </summary>
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();

        // Authenticated users may connect and exchange messages. What they are allowed to *ask for*
        // is decided per operation against their token, not here.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        // The service itself and administrators additionally get the right to manage instances.
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(sid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        // Explicitly deny the network logon SID. A named pipe is reachable over SMB by default, and
        // nothing here is meant to be driven from another machine.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            MaxConcurrentConnections,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            pipeSecurity: security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        Guid? subscriptionId = null;

        try
        {
            var caller = IdentifyCaller(pipe);

            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 8192, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 8192, leaveOpen: true)
            {
                AutoFlush = true,
            };
            // One writer lock per connection: pushed events and request responses share the stream,
            // and two concurrent writes would interleave into unparseable JSON.
            var writeGate = new SemaphoreSlim(1, 1);

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break; // client hung up
                if (line.Length == 0) continue;

                IpcMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<IpcMessage>(line, IpcJson.Options);
                }
                catch (JsonException ex)
                {
                    _log?.Invoke($"IPC received malformed JSON from {caller.Name}: {ex.Message}");
                    continue;
                }
                if (message is null || message.Kind != IpcMessageKind.Request) continue;

                if (message.Operation == IpcOperation.Subscribe)
                {
                    subscriptionId = Guid.NewGuid();
                    _subscribers[subscriptionId.Value] = new Subscriber(writer, writeGate);
                    await RespondAsync(writer, writeGate, message, result: null, error: null, ct)
                        .ConfigureAwait(false);
                    continue;
                }

                object? result = null;
                string? error = null;
                try
                {
                    result = await _dispatch(
                        new IpcRequest(message.Operation, message.Payload, caller), ct).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException ex)
                {
                    error = ex.Message;
                    _log?.Invoke($"IPC denied {message.Operation} to {caller.Name}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _log?.Invoke($"IPC {message.Operation} failed for {caller.Name}: {ex}");
                }

                await RespondAsync(writer, writeGate, message, result, error, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // A client that disappears mid-conversation is routine, not an error.
        }
        catch (Exception ex)
        {
            _log?.Invoke($"IPC connection failed: {ex.Message}");
        }
        finally
        {
            if (subscriptionId is { } id) _subscribers.TryRemove(id, out _);
            try { if (pipe.IsConnected) pipe.Disconnect(); } catch { /* already gone */ }
            pipe.Dispose();
        }
    }

    private static async Task RespondAsync(
        StreamWriter writer, SemaphoreSlim gate, IpcMessage request, object? result, string? error,
        CancellationToken ct)
    {
        var response = new IpcMessage
        {
            Id = request.Id,
            Kind = IpcMessageKind.Response,
            Operation = request.Operation,
            Error = error,
            Payload = result is null
                ? null
                : JsonSerializer.SerializeToElement(result, result.GetType(), IpcJson.Options),
        };

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await writer.WriteLineAsync(IpcJson.Serialize(response)).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Determines who connected, by impersonating them and reading the resulting token.
    ///
    /// The identity is taken from the operating system, never from anything in the message. A client
    /// can claim whatever it likes in JSON; it cannot fake the token Windows attaches to its end of
    /// the pipe.
    /// </summary>
    private static IpcCaller IdentifyCaller(NamedPipeServerStream pipe)
    {
        string sid = string.Empty;
        string name = "unknown";
        var isAdmin = false;

        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            sid = identity.User?.Value ?? string.Empty;
            name = identity.Name;

            // Check group membership on the caller's own token rather than resolving the group
            // separately, so a user whose administrator rights are filtered by UAC is correctly
            // treated as a standard user.
            var principal = new WindowsPrincipal(identity);
            isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator)
                      || string.Equals(sid, "S-1-5-18", StringComparison.Ordinal); // LocalSystem
        });

        return new IpcCaller(sid, name, isAdmin);
    }

    /// <summary>Pushes an event to every subscriber. Failures drop that subscriber, not the event.</summary>
    public async Task PublishAsync(IpcOperation operation, object payload, CancellationToken ct = default)
    {
        if (_subscribers.IsEmpty) return;

        var message = new IpcMessage
        {
            Kind = IpcMessageKind.Event,
            Operation = operation,
            Payload = JsonSerializer.SerializeToElement(payload, payload.GetType(), IpcJson.Options),
        };
        var line = IpcJson.Serialize(message);

        foreach (var (id, subscriber) in _subscribers)
        {
            try
            {
                await subscriber.Gate.WaitAsync(ct).ConfigureAwait(false);
                try { await subscriber.Writer.WriteLineAsync(line).ConfigureAwait(false); }
                finally { subscriber.Gate.Release(); }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                _subscribers.TryRemove(id, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(_acceptors).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { /* shutting down anyway */ }
        _shutdown.Dispose();
    }

    private sealed record Subscriber(StreamWriter Writer, SemaphoreSlim Gate);
}
