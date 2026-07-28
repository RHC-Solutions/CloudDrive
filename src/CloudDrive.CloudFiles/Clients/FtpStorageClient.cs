using System.Net;
using System.Runtime.Versioning;
using CloudDrive.Core.Models;
using FluentFTP;
using FluentFTP.Exceptions;

namespace CloudDrive.CloudFiles;

/// <summary>
/// An FTP or FTPS server.
///
/// FTP is the least capable back end here and the implementation reflects that. It has no
/// multiplexing — one command channel carries one transfer, with a second connection opened per
/// data transfer — no checksums in the base standard, and no batched delete. What it does have is
/// real directories and a rename verb, so the on-demand engine gets cheap moves.
///
/// <para><b>On connection handling.</b> A single control connection is held and reused rather than
/// dialled per operation, because an FTP handshake is expensive: TCP, then a login, then a TLS
/// negotiation for FTPS. Access is serialised behind a lock, since a control connection genuinely
/// cannot carry two commands at once — that is a property of the protocol, not a shortcut.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FtpStorageClient : IRemoteStorageClient
{
    private readonly AsyncFtpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string>? _log;
    private readonly string _root;
    private bool _disposed;

    public FtpStorageClient(
        string host, int port, string username, string password,
        bool useTls, bool implicitTls, string? root = null, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("A host is required.", nameof(host));

        _log = log;
        _root = NormalizeRoot(root);

        _client = new AsyncFtpClient(host, new NetworkCredential(username, password), port <= 0 ? 21 : port);
        _client.Config.EncryptionMode = !useTls ? FtpEncryptionMode.None
            : implicitTls ? FtpEncryptionMode.Implicit
            : FtpEncryptionMode.Explicit;
        _client.Config.ValidateAnyCertificate = false;
        // Passive mode: an active-mode data connection asks the *server* to dial back to the client,
        // which no NAT or client firewall will allow. Every practical FTP client defaults to passive.
        _client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        _client.Config.RetryAttempts = 3;
        _client.Config.ReadTimeout = 30_000;
        _client.Config.ConnectTimeout = 20_000;
    }

    /// <summary>Builds a client for an FTP account.</summary>
    public static FtpStorageClient ForMapping(
        Mapping mapping, Account account, Credentials creds, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(creds);

        if (string.IsNullOrWhiteSpace(creds.Password))
            throw new InvalidOperationException("FTP authenticates with a password.");

        var implicitTls = account.Options.GetValueOrDefault("ftp_implicit_tls") == "true";

        return new FtpStorageClient(
            account.Host,
            account.EffectivePort,
            account.Username.Trim(),
            creds.Password,
            account.UseTls,
            implicitTls,
            mapping.Container,
            log);
    }

    public string ProtocolName => "FTP";

    /// <summary>FTP has no presigned-URL concept, so sharing has to happen out of band.</summary>
    public bool SupportsShareLinks => false;

    private static string NormalizeRoot(string? root) =>
        string.IsNullOrWhiteSpace(root) ? string.Empty : root.Trim().Replace('\\', '/').Trim('/');

    /// <summary>Turns a mapping-relative key into a server path.</summary>
    private string ToRemotePath(string key)
    {
        var clean = key.Replace('\\', '/').TrimStart('/');
        return _root.Length == 0 ? "/" + clean : $"/{_root}/{clean}";
    }

    /// <summary>Turns a server path back into a mapping-relative key.</summary>
    private string ToKey(string remotePath)
    {
        var clean = remotePath.Replace('\\', '/').TrimStart('/');
        if (_root.Length > 0 && clean.StartsWith(_root + "/", StringComparison.OrdinalIgnoreCase))
            clean = clean[(_root.Length + 1)..];
        return clean;
    }

    private async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
            {
                _log?.Invoke("Connecting to the FTP server…");
                await _client.Connect(ct).ConfigureAwait(false);
            }
            return new Release(_gate);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public async IAsyncEnumerable<RemoteEntry> ListAsync(
        string? prefix = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var start = ToRemotePath(prefix ?? string.Empty).TrimEnd('/');
        if (start.Length == 0) start = "/";

        // An explicit stack rather than recursion: a deep tree on a remote server is untrusted input,
        // and recursing on it risks a stack overflow that would take the whole process down.
        var pending = new Stack<string>();
        pending.Push(start);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            FtpListItem[] items;
            using (await AcquireAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    items = await _client.GetListing(directory, ct).ConfigureAwait(false);
                }
                catch (FtpCommandException ex) when (ex.CompletionCode is "550")
                {
                    // 550 is "no such file or directory". A prefix that does not exist yet is a
                    // normal state for a new mapping, not an error.
                    continue;
                }
            }

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();

                if (item.Type == FtpObjectType.Directory)
                {
                    pending.Push(item.FullName);
                }
                else if (item.Type == FtpObjectType.File)
                {
                    var modified = item.Modified == default ? DateTime.UtcNow : item.Modified.ToUniversalTime();
                    yield return new RemoteEntry(
                        ToKey(item.FullName),
                        item.Size < 0 ? 0 : item.Size,
                        modified,
                        // FTP has no ETag, and the optional XCRC extension is rarely implemented, so
                        // the change token is derived from size and mtime.
                        RemoteEntry.SyntheticTag(item.Size < 0 ? 0 : item.Size, modified));
                }
                // Symlinks are skipped: following one can leave the mapping's root or produce a
                // cycle, and neither is something a sync engine should discover the hard way.
            }
        }
    }

    public async Task<Stream> OpenReadAsync(
        string key, long offset, long? length, CancellationToken ct = default)
    {
        using var _ = await AcquireAsync(ct).ConfigureAwait(false);

        // checkIfFileExists: false skips a SIZE round trip before every read. Hydration only ever
        // asks for keys that came out of a listing, and the read itself reports a missing file
        // perfectly well — paying for an extra command per range request would roughly double the
        // latency of opening a file.
        var stream = await _client
            .OpenRead(ToRemotePath(key), FtpDataType.Binary, offset, checkIfFileExists: false, token: ct)
            .ConfigureAwait(false);

        // FTP's REST command sets a start offset but has no way to express an end, so the server
        // streams to the end of the file. Windows asks for bounded ranges during hydration, so the
        // limit is imposed here rather than pulling gigabytes the caller will discard.
        return length is { } len ? new BoundedStream(stream, len) : stream;
    }

    public async Task<string?> PutAsync(string key, string localPath, CancellationToken ct = default)
    {
        using var _ = await AcquireAsync(ct).ConfigureAwait(false);

        var remote = ToRemotePath(key);
        var status = await _client
            .UploadFile(localPath, remote, FtpRemoteExists.Overwrite, createRemoteDir: true,
                FtpVerify.None, progress: null, token: ct)
            .ConfigureAwait(false);

        if (status == FtpStatus.Failed)
            throw new IOException($"Uploading '{key}' over FTP failed.");

        var info = new FileInfo(localPath);
        var modified = await TryGetModifiedAsync(remote, ct).ConfigureAwait(false) ?? info.LastWriteTimeUtc;
        return RemoteEntry.SyntheticTag(info.Length, modified);
    }

    private async Task<DateTime?> TryGetModifiedAsync(string remotePath, CancellationToken ct)
    {
        try
        {
            var stamp = await _client.GetModifiedTime(remotePath, ct).ConfigureAwait(false);
            return stamp == default ? null : stamp.ToUniversalTime();
        }
        catch (FtpCommandException)
        {
            // MDTM is optional and plenty of servers do not implement it.
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        using var _ = await AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            await _client.DeleteFile(ToRemotePath(key), ct).ConfigureAwait(false);
        }
        catch (FtpCommandException ex) when (ex.CompletionCode is "550")
        {
            // Already gone, which is the state the caller wanted.
        }
    }

    /// <summary>
    /// Deletes one at a time. FTP has no batch verb, so this reports per-key outcomes rather than
    /// pretending to be atomic.
    /// </summary>
    public async Task<DeleteResult> DeleteManyAsync(
        IEnumerable<string> keys, CancellationToken ct = default)
    {
        var deleted = new List<string>();
        var failed = new List<string>();

        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DeleteAsync(key, ct).ConfigureAwait(false);
                deleted.Add(key);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Invoke($"Deleting '{key}' failed: {ex.Message}");
                failed.Add(key);
            }
        }

        return new DeleteResult(deleted, failed);
    }

    public async Task MoveAsync(string sourceKey, string destKey, CancellationToken ct = default)
    {
        using var _ = await AcquireAsync(ct).ConfigureAwait(false);

        var destination = ToRemotePath(destKey);
        var parent = destination[..destination.LastIndexOf('/')];
        if (parent.Length > 0 && !await _client.DirectoryExists(parent, ct).ConfigureAwait(false))
            await _client.CreateDirectory(parent, ct).ConfigureAwait(false);

        await _client.MoveFile(ToRemotePath(sourceKey), destination, FtpRemoteExists.Overwrite, ct)
            .ConfigureAwait(false);
    }

    public string? CreateShareLink(string key, TimeSpan expiresIn) => null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _client.Dispose(); } catch { /* closing */ }
        _gate.Dispose();
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            gate.Release();
        }
    }

    /// <summary>
    /// Caps a stream at <paramref name="limit"/> bytes. Needed because FTP can express where a
    /// transfer starts but not where it ends.
    /// </summary>
    private sealed class BoundedStream(Stream inner, long limit) : Stream
    {
        private readonly long _limit = limit;
        private long _remaining = limit;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _limit;

        public override long Position
        {
            get => _limit - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0) return 0;
            var slice = buffer[..(int)Math.Min(buffer.Length, _remaining)];
            var read = await inner.ReadAsync(slice, cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
