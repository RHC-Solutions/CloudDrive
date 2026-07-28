using System.Collections.Concurrent;
using CloudDrive.Core.Models;
using CloudDrive.Core.Providers;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace CloudDrive.CloudFiles;

/// <summary>
/// A Storage Box over SFTP.
///
/// The one thing that shapes this class is Hetzner's cap of roughly ten concurrent SSH sessions
/// per Storage Box. Opening a connection per operation — the obvious design — hits the ceiling as
/// soon as hydration fans out into parallel range reads, and the server's response is to refuse
/// the extra sessions mid-transfer rather than queue them. So connections live in a small fixed
/// pool that callers borrow from and block on, which bounds usage no matter how much concurrency
/// the layers above ask for.
/// </summary>
public sealed class SftpStorageClient : IRemoteStorageClient
{
    /// <summary>
    /// Pool size. Deliberately well under Hetzner's ~10-session cap: rclone mounts, other clients
    /// and the user's own SSH sessions draw on the same budget, and leaving headroom is cheaper
    /// than diagnosing intermittent "connection reset" errors.
    /// </summary>
    private const int PoolSize = 4;

    private readonly ConnectionInfo _connectionInfo;
    private readonly ConcurrentBag<SftpClient> _pool = new();
    private readonly SemaphoreSlim _slots = new(PoolSize, PoolSize);
    private readonly Action<string>? _log;

    private string? _root;
    private bool _disposed;

    public SftpStorageClient(string host, int port, string username,
        string? password, string? keyFile, string? keyPassphrase, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Username is required.", nameof(username));

        _log = log;
        _connectionInfo = BuildConnectionInfo(host, port, username, password, keyFile, keyPassphrase);
    }

    /// <summary>
    /// Builds a client for an SFTP account.
    ///
    /// A Hetzner Storage Box needs its username reduced to the bare account name: the hostname is
    /// derived from the username, and sending the full host as the login fails. That is the one
    /// provider-specific wrinkle on this path.
    /// </summary>
    public static SftpStorageClient ForMapping(
        Mapping mapping, Account account, Credentials creds, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(creds);

        var user = account.Provider == ProviderId.HetznerStorageBox
            ? StorageBox.UserFor(account.Username)
            : account.Username.Trim();

        return new SftpStorageClient(
            account.Host,
            account.EffectivePort,
            user,
            creds.Password,
            creds.SshKeyFile,
            creds.SshKeyPassphrase,
            log);
    }

    public string ProtocolName => "SFTP";

    /// <summary>A Storage Box has no presigned-URL equivalent; sharing goes through Hetzner's UI.</summary>
    public bool SupportsShareLinks => false;

    public string? CreateShareLink(string key, TimeSpan expiresIn) => null;

    private static ConnectionInfo BuildConnectionInfo(
        string host, int port, string username, string? password, string? keyFile, string? keyPassphrase)
    {
        var methods = new List<AuthenticationMethod>();

        // A key is tried first when present: it survives password rotation and is what Hetzner
        // recommends for unattended access.
        if (!string.IsNullOrWhiteSpace(keyFile))
        {
            if (!File.Exists(keyFile))
                throw new FileNotFoundException("The configured SSH key file was not found.", keyFile);
            var key = string.IsNullOrWhiteSpace(keyPassphrase)
                ? new PrivateKeyFile(keyFile)
                : new PrivateKeyFile(keyFile, keyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(username, key));
        }
        if (!string.IsNullOrWhiteSpace(password))
            methods.Add(new PasswordAuthenticationMethod(username, password));

        if (methods.Count == 0)
            throw new InvalidOperationException("SFTP needs either a password or an SSH key.");

        return new ConnectionInfo(host, port <= 0 ? 22 : port, username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>
    /// Borrows a connected client, creating one only if the pool is empty. Blocks once
    /// <see cref="PoolSize"/> are in use, which is how the session cap is respected.
    /// </summary>
    private async Task<PooledClient> RentAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_pool.TryTake(out var client) || !client.IsConnected)
            {
                client?.Dispose();
                client = new SftpClient(_connectionInfo);
                await Task.Run(client.Connect, ct).ConfigureAwait(false);
                // The login directory is the account's root; every key is relative to it.
                _root ??= client.WorkingDirectory;
            }
            return new PooledClient(this, client);
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    private void Return(SftpClient client)
    {
        // A dead connection is discarded rather than pooled — the next borrower would just have to
        // discover the failure itself.
        if (client.IsConnected) _pool.Add(client);
        else client.Dispose();
        _slots.Release();
    }

    /// <summary>Maps a remote key onto an absolute path under the account root.</summary>
    private string PathFor(string key)
    {
        var root = (_root ?? "/home").TrimEnd('/');
        var relative = (key ?? string.Empty).Replace('\\', '/').TrimStart('/');
        return relative.Length == 0 ? root : $"{root}/{relative}";
    }

    public async IAsyncEnumerable<RemoteEntry> ListAsync(
        string? prefix = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var start = (prefix ?? string.Empty).Replace('\\', '/').Trim('/');

        // An explicit stack rather than recursion: a deep tree should not be able to overflow, and
        // this keeps exactly one connection borrowed for the whole walk instead of one per level.
        var pending = new Stack<string>();
        pending.Push(start);

        using var lease = await RentAsync(ct).ConfigureAwait(false);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            List<ISftpFile> entries;
            try
            {
                entries = await Task.Run(
                    () => lease.Client.ListDirectory(PathFor(dir)).ToList(), ct).ConfigureAwait(false);
            }
            catch (SftpPathNotFoundException)
            {
                // A prefix that does not exist yet is an empty listing, not a failure — this is the
                // normal state the first time a mapping points at a folder that has not been created.
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.Name is "." or "..") continue;
                var key = dir.Length == 0 ? entry.Name : $"{dir}/{entry.Name}";

                if (entry.IsDirectory) { pending.Push(key); continue; }
                if (!entry.IsRegularFile) continue; // skip sockets, symlinks to nowhere, devices

                var modified = entry.LastWriteTimeUtc;
                yield return new RemoteEntry(
                    key, entry.Length, modified, RemoteEntry.SyntheticTag(entry.Length, modified));
            }
        }
    }

    public async Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct = default)
    {
        var lease = await RentAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = await Task.Run(() => lease.Client.OpenRead(PathFor(key)), ct).ConfigureAwait(false);
            if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);

            // The lease must outlive the stream: the caller reads from it long after this method
            // returns, and returning the connection early would let another operation reuse it.
            return new LeasedStream(stream, lease, length);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async Task<string?> PutAsync(string key, string localPath, CancellationToken ct = default)
    {
        using var lease = await RentAsync(ct).ConfigureAwait(false);
        var remotePath = PathFor(key);
        EnsureDirectory(lease.Client, RemoteParent(remotePath));

        await using (var source = File.OpenRead(localPath))
        {
            var stream = source;
            await Task.Run(() => lease.Client.UploadFile(stream, remotePath, canOverride: true), ct)
                .ConfigureAwait(false);
        }

        // Read back what the server recorded rather than trusting the local file's stats: the
        // stored mtime is the server's, and the change token has to match what a later listing
        // reports or every file would look changed on the next reconcile.
        try
        {
            var attrs = await Task.Run(() => lease.Client.GetAttributes(remotePath), ct).ConfigureAwait(false);
            return RemoteEntry.SyntheticTag(attrs.Size, attrs.LastWriteTimeUtc);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not stat '{key}' after upload: {ex.Message}");
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        using var lease = await RentAsync(ct).ConfigureAwait(false);
        DeleteOne(lease.Client, PathFor(key));
    }

    /// <summary>
    /// SFTP has no batch delete, so this is a loop — but it runs on one borrowed connection
    /// instead of reconnecting per key, which is where the time would actually go.
    /// </summary>
    public async Task<DeleteResult> DeleteManyAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var deleted = new List<string>();
        var failed = new List<string>();

        using var lease = await RentAsync(ct).ConfigureAwait(false);
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try { DeleteOne(lease.Client, PathFor(key)); deleted.Add(key); }
            catch (Exception ex)
            {
                _log?.Invoke($"Delete of '{key}' failed: {ex.Message}");
                failed.Add(key);
            }
        }
        return new DeleteResult(deleted, failed);
    }

    private static void DeleteOne(SftpClient client, string path)
    {
        try
        {
            // Directories need the other call, and a caller deleting a tree cannot always tell
            // which it had — the local copy is already gone by the time the watcher fires.
            var attrs = client.GetAttributes(path);
            if (attrs.IsDirectory) client.DeleteDirectory(path);
            else client.DeleteFile(path);
        }
        catch (SftpPathNotFoundException)
        {
            // Already gone: the desired end state, not an error.
        }
    }

    public async Task MoveAsync(string sourceKey, string destKey, CancellationToken ct = default)
    {
        using var lease = await RentAsync(ct).ConfigureAwait(false);
        var from = PathFor(sourceKey);
        var to = PathFor(destKey);
        EnsureDirectory(lease.Client, RemoteParent(to));

        await Task.Run(() =>
        {
            // SFTP rename fails if the target exists; an overwrite is what the caller means.
            try { if (lease.Client.Exists(to)) lease.Client.DeleteFile(to); }
            catch (SftpPathNotFoundException) { /* nothing to clear */ }
            lease.Client.RenameFile(from, to);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Creates a directory and any missing parents (SFTP has no mkdir -p).</summary>
    private static void EnsureDirectory(SftpClient client, string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return;
        if (client.Exists(path)) return;

        EnsureDirectory(client, RemoteParent(path));
        try { client.CreateDirectory(path); }
        catch (SshException)
        {
            // Another concurrent upload may have created it between the check and the call.
            if (!client.Exists(path)) throw;
        }
    }

    private static string RemoteParent(string path)
    {
        var i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        while (_pool.TryTake(out var client))
        {
            try { client.Dispose(); } catch { /* best-effort */ }
        }
        _slots.Dispose();
    }

    /// <summary>A borrowed connection, returned to the pool on dispose.</summary>
    private sealed class PooledClient : IDisposable
    {
        private readonly SftpStorageClient _owner;
        private bool _returned;

        public PooledClient(SftpStorageClient owner, SftpClient client)
        {
            _owner = owner;
            Client = client;
        }

        public SftpClient Client { get; }

        public void Dispose()
        {
            if (_returned) return;
            _returned = true;
            _owner.Return(Client);
        }
    }

    /// <summary>
    /// Wraps an SFTP read stream so the pooled connection is released when the consumer disposes
    /// the stream, and caps reads at the requested range length (SFTP has no ranged GET, so the
    /// limit has to be enforced here or hydration would be handed more bytes than it asked for).
    /// </summary>
    private sealed class LeasedStream : Stream
    {
        private readonly Stream _inner;
        private readonly IDisposable _lease;
        private long _remaining;

        public LeasedStream(Stream inner, IDisposable lease, long? length)
        {
            _inner = inner;
            _lease = lease;
            _remaining = length ?? long.MaxValue;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_remaining <= 0) return 0;
            var slice = buffer[..(int)Math.Min(buffer.Length, _remaining)];
            var read = await _inner.ReadAsync(slice, ct).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _lease.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
