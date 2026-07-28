using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CloudDrive.Core.Models;

namespace CloudDrive.CloudFiles;

/// <summary>
/// A Storage Box over SMB/CIFS.
///
/// Unlike the other backends there is no protocol library here: Windows already has an SMB
/// redirector, so the share is authenticated once with <c>WNetAddConnection2</c> and then read and
/// written through ordinary <see cref="System.IO"/> calls against the UNC path. That gets kernel
/// caching and readahead for free, and keeps this the shortest of the four clients.
///
/// The connection is registered without a drive letter (a "deviceless" mapping), so it consumes no
/// letter and stays invisible in Explorer — the on-demand folder is the user-facing surface.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SmbStorageClient : IRemoteStorageClient
{
    private readonly string _root;
    private readonly Action<string>? _log;
    private bool _connected;
    private bool _disposed;

    public SmbStorageClient(string host, string share, string username, string password, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
        if (string.IsNullOrWhiteSpace(share)) throw new ArgumentException("Share is required.", nameof(share));

        _log = log;
        _root = $@"\\{host}\{share.Trim('\\', '/')}";
        Connect(username, password);
    }

    /// <summary>
    /// Builds a client for an SMB account.
    ///
    /// The share comes from the mapping's container for a normal SMB server. A Storage Box does not
    /// have the user pick one: the main account is always <c>backup</c> and a sub-account is a share
    /// named after itself, so it is derived rather than asked for.
    /// </summary>
    public static SmbStorageClient ForMapping(
        Mapping mapping, Account account, Credentials creds, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(creds);
        if (string.IsNullOrWhiteSpace(creds.Password))
            throw new InvalidOperationException("SMB authenticates with a password; an SSH key cannot be used.");

        var isStorageBox = account.Provider == ProviderId.HetznerStorageBox;
        var share = isStorageBox
            ? StorageBox.ShareFor(account.Username)
            : mapping.Container.Trim('/', '\\');
        if (string.IsNullOrWhiteSpace(share))
            throw new InvalidOperationException("This mapping needs a share name.");

        return new SmbStorageClient(
            account.Host,
            share,
            isStorageBox ? StorageBox.UserFor(account.Username) : account.Username.Trim(),
            creds.Password,
            log);
    }

    public string ProtocolName => "SMB";

    public bool SupportsShareLinks => false;

    public string? CreateShareLink(string key, TimeSpan expiresIn) => null;

    private void Connect(string username, string password)
    {
        var resource = new NetResource
        {
            Scope = ResourceScope.GlobalNetwork,
            ResourceType = ResourceType.Disk,
            DisplayType = ResourceDisplayType.Share,
            RemoteName = _root,
        };

        var result = WNetAddConnection2(resource, password, username, 0);
        switch (result)
        {
            case NoError:
            case ErrorSessionCredentialConflict:
                // A session to this server already exists under these or other credentials.
                // Windows allows only one set per server, so reuse it rather than tearing down a
                // connection something else may be using.
                _connected = result == NoError;
                return;
            default:
                throw new Win32Exception(result,
                    $"Could not connect to {_root}: {new Win32Exception(result).Message} " +
                    "(check that SMB is enabled for this Storage Box and that port 445 is not blocked).");
        }
    }

    private string PathFor(string key)
    {
        var relative = (key ?? string.Empty).Replace('/', '\\').Trim('\\');
        return relative.Length == 0 ? _root : Path.Combine(_root, relative);
    }

    private string KeyFor(string fullPath) =>
        Path.GetRelativePath(_root, fullPath).Replace('\\', '/');

    public async IAsyncEnumerable<RemoteEntry> ListAsync(
        string? prefix = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var start = PathFor(prefix ?? string.Empty);
        if (!Directory.Exists(start)) yield break;

        // EnumerateFiles streams rather than materialising the tree, which matters for a Storage
        // Box holding hundreds of thousands of files.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var path in Directory.EnumerateFiles(start, "*", options))
        {
            ct.ThrowIfCancellationRequested();

            RemoteEntry entry;
            try
            {
                var info = new FileInfo(path);
                var modified = info.LastWriteTimeUtc;
                entry = new RemoteEntry(
                    KeyFor(path), info.Length, modified, RemoteEntry.SyntheticTag(info.Length, modified));
            }
            catch (Exception ex)
            {
                // A file can vanish or lock between enumeration and stat; skip it rather than
                // aborting the whole listing.
                _log?.Invoke($"Skipping '{path}': {ex.Message}");
                continue;
            }

            yield return entry;
            await Task.Yield();
        }
    }

    public Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct = default)
    {
        var stream = new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1 << 16, useAsync: true);
        try
        {
            if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);
            // SMB has no ranged read, so the requested length is enforced client-side or hydration
            // would be handed more bytes than the range it asked for.
            Stream result = length is null ? stream : new BoundedStream(stream, length.Value);
            return Task.FromResult(result);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public async Task<string?> PutAsync(string key, string localPath, CancellationToken ct = default)
    {
        var target = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        await using (var source = File.OpenRead(localPath))
        await using (var destination = new FileStream(target, FileMode.Create, FileAccess.Write,
                         FileShare.None, bufferSize: 1 << 20, useAsync: true))
        {
            await source.CopyToAsync(destination, 1 << 20, ct).ConfigureAwait(false);
        }

        try
        {
            var info = new FileInfo(target);
            return RemoteEntry.SyntheticTag(info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not stat '{key}' after upload: {ex.Message}");
            return null;
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (DirectoryNotFoundException) { /* already gone */ }
        catch (FileNotFoundException) { /* already gone */ }
        return Task.CompletedTask;
    }

    public async Task<DeleteResult> DeleteManyAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var deleted = new List<string>();
        var failed = new List<string>();

        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try { await DeleteAsync(key, ct).ConfigureAwait(false); deleted.Add(key); }
            catch (Exception ex)
            {
                _log?.Invoke($"Delete of '{key}' failed: {ex.Message}");
                failed.Add(key);
            }
        }
        return new DeleteResult(deleted, failed);
    }

    public Task MoveAsync(string sourceKey, string destKey, CancellationToken ct = default)
    {
        var from = PathFor(sourceKey);
        var to = PathFor(destKey);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);

        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to, overwrite: true);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Only drop the session this instance established. Cancelling one we merely reused would
        // break whatever else is holding it.
        if (!_connected) return;
        try { WNetCancelConnection2(_root, 0, false); } catch { /* best-effort */ }
    }

    // --- Win32 network-connection interop ---

    private const int NoError = 0;
    private const int ErrorSessionCredentialConflict = 1219;

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetAddConnection2(
        NetResource netResource, string? password, string? username, uint flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetCancelConnection2(string name, uint flags, bool force);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class NetResource
    {
        public ResourceScope Scope;
        public ResourceType ResourceType;
        public ResourceDisplayType DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    private enum ResourceScope { Connected = 1, GlobalNetwork, Remembered, Recent, Context }

    private enum ResourceType { Any = 0, Disk = 1, Print = 2, Reserved = 8 }

    private enum ResourceDisplayType { Generic = 0, Domain = 1, Server = 2, Share = 3 }

    /// <summary>Caps a stream at a byte count, for backends with no ranged read.</summary>
    private sealed class BoundedStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public BoundedStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
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
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
