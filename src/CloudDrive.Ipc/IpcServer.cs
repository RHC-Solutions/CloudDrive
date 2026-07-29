using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

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
    /// Throws unless the caller is an administrator.
    ///
    /// <para>This now guards only what is genuinely machine-wide: settings, notification targets, tool
    /// installation, updates, and mappings hosted by the LocalSystem service. It used to guard every
    /// write, which made CloudDrive unusable without elevation; accounts and session-hosted mappings are
    /// authorised by ownership instead.</para>
    /// </summary>
    /// <param name="what">
    /// What is being changed, so the message says which machine-wide thing needs the rights rather than
    /// implying that all configuration does.
    /// </param>
    public void RequireAdministrator(string what = "This setting")
    {
        if (!Caller.IsAdministrator)
            throw new UnauthorizedAccessException(
                $"{what} applies to the whole machine, so changing it needs administrator rights. "
                + "Accounts and mappings you own do not.");
    }
}

/// <summary>
/// The named-pipe server the service runs, and the only way anything talks to it.
///
/// <para><b>The ACL is the security boundary.</b> Interactive users need to reach the pipe — the tray
/// app is a client — so it cannot simply be locked to Administrators. Instead every authenticated
/// user may connect and issue read-only calls, and the dispatcher authorises each operation against
/// the caller's own token, read from the client's process rather than taken on trust from the message.
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
        // A persistent failure here is a configuration problem, not a transient one, so the loop backs
        // off and stops repeating itself. Logging every attempt at a fixed one-second interval across
        // four listeners produced hundreds of identical lines a minute and buried everything else.
        var consecutiveFailures = 0;
        string? lastReported = null;

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                consecutiveFailures = 0;
                lastReported = null;

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
                consecutiveFailures++;

                // Report a new message immediately, then only on an exponential schedule, so a
                // permanent fault is stated clearly once instead of scrolling past.
                if (ex.Message != lastReported || IsPowerOfTwo(consecutiveFailures))
                {
                    lastReported = ex.Message;
                    _log?.Invoke(
                        $"Could not listen on \\\\.\\pipe\\{PipeName} (attempt {consecutiveFailures}): "
                        + $"{ex.Message}");
                }

                // 1s, 2s, 4s … capped at 30s. Fast enough to recover from a race, slow enough that a
                // permanent failure costs nothing.
                var backoff = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(5, consecutiveFailures - 1))));
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

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
        //
        // Deliberately ReadWrite and *not* CreateNewInstance. Granting CreateNewInstance broadly would
        // let any logged-in user stand up their own instance of \\.\pipe\CloudDrive and answer other
        // users' clients as though it were the service — classic named-pipe squatting. Clients never
        // need that right; only the process hosting the pipe does.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        // The service itself and administrators get FullControl, which includes CreateNewInstance.
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(sid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        // The owner of *this* process needs CreateNewInstance explicitly, and this line is a bug fix.
        //
        // Every additional listener beyond the first is a second instance of the same pipe name, and
        // creating one requires CreateNewInstance on the existing pipe. In production the service runs
        // as LocalSystem and picks that up from the ACE above — but a host running unelevated for
        // troubleshooting matches only the Authenticated Users ACE, so its first listener succeeded
        // and every other one was denied forever. The symptom was a service that accepted exactly one
        // connection and then refused everything with "access is denied" three times a second.
        if (WindowsIdentity.GetCurrent().User is { } owner)
        {
            security.AddAccessRule(new PipeAccessRule(
                owner,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
        }

        // Explicitly deny the network logon SID. A named pipe is reachable over SMB by default, and
        // nothing here is meant to be driven from another machine. Added last; the framework
        // canonicalises the DACL so deny entries are evaluated first.
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
            // A client that disappears mid-conversation is routine. Recorded anyway, with the type,
            // because swallowing it silently hid a real fault: the connection was being torn down
            // before any request was served and there was nothing in the log to say why.
            _log?.Invoke($"IPC connection ended: {ex}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"IPC connection failed: {ex}");
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
    /// Determines who connected, by reading the client process's token — <b>without impersonating</b>.
    ///
    /// <para>The identity comes from the operating system, never from anything in the message: a client
    /// can claim what it likes in JSON, but it cannot fake the token Windows attaches to its end of the
    /// pipe.</para>
    ///
    /// <para><b>Why not <c>RunAsClient</c>.</b> That impersonates the caller, and the impersonation was
    /// still in effect when the request handler ran. Because clients connect at
    /// <see cref="TokenImpersonationLevel.Identification"/> — deliberately, so a rogue server cannot act
    /// as the user — that token cannot satisfy an access check, so every file operation inside a handler
    /// failed. <see cref="File.Exists"/> returns <c>false</c> on access-denied rather than throwing, so
    /// the failures were completely silent: the service reported no accounts and no mappings while the
    /// configuration sat on disk, and a save would have read that empty document, added one entry and
    /// written it back — destroying everything else. Reading the client's process token needs no
    /// impersonation and leaves this thread's own privileges untouched.</para>
    /// </summary>
    private static IpcCaller IdentifyCaller(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid))
            throw new IOException("The pipe client's process could not be identified.");

        // PROCESS_QUERY_LIMITED_INFORMATION is enough to open the token and is grantable across
        // integrity levels, unlike PROCESS_QUERY_INFORMATION.
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, clientPid);
        if (process.IsInvalid)
            throw new IOException($"The pipe client's process ({clientPid}) could not be opened.");

        if (!OpenProcessToken(process, TokenQuery | TokenDuplicate, out var token))
            throw new IOException("The pipe client's token could not be opened.");

        using (token)
        {
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            var sid = identity.User?.Value ?? string.Empty;

            // Group membership is evaluated on the caller's own token, so a user whose administrator
            // rights are filtered by UAC is correctly treated as a standard user.
            var isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
                          || string.Equals(sid, "S-1-5-18", StringComparison.Ordinal); // LocalSystem

            return new IpcCaller(sid, identity.Name, isAdmin);
        }
    }

    private const int ProcessQueryLimitedInformation = 0x1000;

    // TOKEN_QUERY reads the SID and groups; TOKEN_DUPLICATE is also required because the
    // WindowsIdentity(IntPtr) constructor duplicates the handle it is given. Asking for QUERY alone
    // produced "Access is denied" from inside the constructor, which reads like a permissions problem
    // with the client rather than a missing right in this request.
    private const int TokenQuery = 0x0008;
    private const int TokenDuplicate = 0x0002;

    // Classic DllImport rather than LibraryImport: the source generator does not marshal SafeHandle
    // types, and using raw IntPtrs here would trade a handle leak for a compile-time convenience.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        int desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle process, int desiredAccess, out SafeAccessTokenHandle token);

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
