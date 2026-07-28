using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace CloudDrive.CloudFiles;

/// <summary>
/// A Windows Cloud Files (cfapi) sync-root provider for one bucket. It registers a local folder as
/// a sync root, connects the hydration callback, and streams object bytes from Hetzner on demand.
/// This is the engine behind the OneDrive-style "Files On-Demand" experience: placeholders occupy
/// no disk space until opened, and Explorer shows the native status column, overlays, and the
/// "Always keep on this device" / "Free up space" menu items.
///
/// One-way (read) for this milestone: it does not upload local changes.
/// </summary>
public sealed class CloudFilesProvider : IDisposable
{
    private const string ProviderName = "CloudDrive";

    private readonly string _syncRootPath;
    private readonly Guid _providerId;
    private readonly byte[] _syncRootIdentity;
    private readonly Func<string, long, long, CancellationToken, Task<Stream>> _openRead;
    private readonly Action<string>? _log;

    // The delegate must be kept alive for the whole connection or the GC will collect the thunk
    // that Windows calls back into.
    private CF_CALLBACK? _fetchCallback;
    private CF_CONNECTION_KEY _connectionKey;
    private bool _connected;

    /// <param name="syncRootPath">Local folder to register as the sync root.</param>
    /// <param name="providerId">Stable per-mapping provider GUID.</param>
    /// <param name="syncRootIdentity">Opaque bytes identifying this root (e.g. the remote name).</param>
    /// <param name="openRead">Fetches object bytes: (s3key, offset, length, ct) -> stream.</param>
    public CloudFilesProvider(
        string syncRootPath,
        Guid providerId,
        byte[] syncRootIdentity,
        Func<string, long, long, CancellationToken, Task<Stream>> openRead,
        Action<string>? log = null)
    {
        _syncRootPath = syncRootPath ?? throw new ArgumentNullException(nameof(syncRootPath));
        _providerId = providerId;
        _syncRootIdentity = syncRootIdentity;
        _openRead = openRead ?? throw new ArgumentNullException(nameof(openRead));
        _log = log;
    }

    public string SyncRootPath => _syncRootPath;

    /// <summary>Raised (with the remote key) after a file finishes hydrating, so watchers can ignore
    /// the resulting disk writes rather than treating them as user edits.</summary>
    public event Action<string>? Hydrated;

    /// <summary>True if a sync root is already registered at this path.</summary>
    public unsafe bool IsRegistered()
    {
        // CfGetSyncRootInfoByPath returns S_OK for a registered root, an error otherwise.
        uint returned;
        var basicBuf = stackalloc byte[512];
        var hr = PInvoke.CfGetSyncRootInfoByPath(
            _syncRootPath, CF_SYNC_ROOT_INFO_CLASS.CF_SYNC_ROOT_INFO_BASIC,
            basicBuf, 512, &returned);
        return hr.Succeeded;
    }

    /// <summary>Registers the folder as a sync root (idempotent via CF_REGISTER_FLAG_UPDATE).</summary>
    public unsafe void Register()
    {
        Directory.CreateDirectory(_syncRootPath);

        var version = "1.0";
        fixed (char* pName = ProviderName)
        fixed (char* pVersion = version)
        fixed (byte* pIdentity = _syncRootIdentity)
        {
            var registration = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)sizeof(CF_SYNC_REGISTRATION),
                ProviderName = pName,
                ProviderVersion = pVersion,
                SyncRootIdentity = pIdentity,
                SyncRootIdentityLength = (uint)_syncRootIdentity.Length,
                ProviderId = _providerId,
            };

            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)sizeof(CF_SYNC_POLICIES),
                Hydration = new CF_HYDRATION_POLICY
                {
                    Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_FULL,
                    Modifier = CF_HYDRATION_POLICY_MODIFIER.CF_HYDRATION_POLICY_MODIFIER_AUTO_DEHYDRATION_ALLOWED,
                },
                // We eagerly create the whole namespace, so no on-demand placeholder population.
                Population = new CF_POPULATION_POLICY
                {
                    Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_FULL,
                    Modifier = CF_POPULATION_POLICY_MODIFIER.CF_POPULATION_POLICY_MODIFIER_NONE,
                },
                InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_NONE,
            };

            PInvoke.CfRegisterSyncRoot(_syncRootPath, registration, policies,
                CF_REGISTER_FLAGS.CF_REGISTER_FLAG_UPDATE).ThrowOnFailure();
        }
        _log?.Invoke($"Registered sync root at {_syncRootPath}");
    }

    /// <summary>Connects hydration callbacks. Call once after <see cref="Register"/>.</summary>
    public unsafe void Connect()
    {
        if (_connected) return;

        _fetchCallback = OnFetchData;
        var callbacks = new[]
        {
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA,
                Callback = _fetchCallback,
            },
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE, // terminator
            },
        };

        CF_CONNECTION_KEY key;
        PInvoke.CfConnectSyncRoot(
            _syncRootPath, callbacks, null,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO
            | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
            &key).ThrowOnFailure();

        _connectionKey = key;
        _connected = true;
        _log?.Invoke("Connected sync root (hydration active).");
    }

    public void Disconnect()
    {
        if (!_connected) return;
        PInvoke.CfDisconnectSyncRoot(_connectionKey);
        _connected = false;
        _fetchCallback = null;
    }

    /// <summary>Removes the sync-root registration (does not delete local files).</summary>
    public void Unregister()
    {
        Disconnect();
        try { PInvoke.CfUnregisterSyncRoot(_syncRootPath); }
        catch (Exception ex) { _log?.Invoke($"Unregister failed: {ex.Message}"); }
    }

    /// <summary>
    /// Creates file placeholders in <paramref name="relativeDir"/> (relative to the sync root).
    /// Directories are created as real folders first so nested keys land in the right place.
    /// </summary>
    public unsafe int CreatePlaceholders(string relativeDir, IReadOnlyList<PlaceholderInfo> files)
    {
        if (files.Count == 0) return 0;

        var baseDir = string.IsNullOrEmpty(relativeDir)
            ? _syncRootPath
            : Path.Combine(_syncRootPath, relativeDir);
        Directory.CreateDirectory(baseDir);

        var allocations = new List<IntPtr>(files.Count * 2);
        try
        {
            var infos = new CF_PLACEHOLDER_CREATE_INFO[files.Count];
            for (var i = 0; i < files.Count; i++)
            {
                var f = files[i];
                var namePtr = Marshal.StringToHGlobalUni(f.FileName);
                allocations.Add(namePtr);

                var identityBytes = Encoding.UTF8.GetBytes(f.FileIdentity);
                var idPtr = Marshal.AllocHGlobal(identityBytes.Length);
                Marshal.Copy(identityBytes, 0, idPtr, identityBytes.Length);
                allocations.Add(idPtr);

                infos[i] = new CF_PLACEHOLDER_CREATE_INFO
                {
                    RelativeFileName = (char*)namePtr,
                    FsMetadata = new CF_FS_METADATA
                    {
                        FileSize = f.Size,
                        BasicInfo = new FILE_BASIC_INFO
                        {
                            CreationTime = f.LastModifiedUtc.ToFileTimeUtc(),
                            LastWriteTime = f.LastModifiedUtc.ToFileTimeUtc(),
                            ChangeTime = f.LastModifiedUtc.ToFileTimeUtc(),
                            LastAccessTime = f.LastModifiedUtc.ToFileTimeUtc(),
                            FileAttributes = (uint)FileAttributes.Normal,
                        },
                    },
                    FileIdentity = (void*)idPtr,
                    FileIdentityLength = (uint)identityBytes.Length,
                    Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
                };
            }

            uint processed;
            PInvoke.CfCreatePlaceholders(baseDir, infos, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, &processed)
                .ThrowOnFailure();
            return (int)processed;
        }
        finally
        {
            foreach (var p in allocations) Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>Pins ("Always keep on this device") or unpins a hydrated/placeholder file.</summary>
    public unsafe void SetPinned(string fullPath, bool pinned)
    {
        using var handle = OpenFileHandle(fullPath);
        PInvoke.CfSetPinState(handle,
            pinned ? CF_PIN_STATE.CF_PIN_STATE_PINNED : CF_PIN_STATE.CF_PIN_STATE_UNPINNED,
            CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_NONE, null).ThrowOnFailure();
    }

    /// <summary>"Free up space": releases the local bytes, leaving a cloud-only placeholder.</summary>
    public unsafe void Dehydrate(string fullPath)
    {
        using var handle = OpenFileHandle(fullPath);
        var length = new FileInfo(fullPath).Length;
        PInvoke.CfDehydratePlaceholder(handle, 0, length,
            CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE, null).ThrowOnFailure();
    }

    /// <summary>Marks a file as in-sync (used after a local change is uploaded to the cloud).</summary>
    public unsafe void MarkInSync(string fullPath)
    {
        using var handle = OpenFileHandle(fullPath);
        PInvoke.CfSetInSyncState(handle, CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
            CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE, null).ThrowOnFailure();
    }

    /// <summary>
    /// Converts a normal file created locally into an in-sync placeholder participating in
    /// on-demand (called after the new file has been uploaded).
    /// </summary>
    public unsafe void ConvertToPlaceholder(string fullPath, string fileIdentity)
    {
        var identity = Encoding.UTF8.GetBytes(fileIdentity);
        using var handle = OpenFileHandle(fullPath);
        fixed (byte* pId = identity)
        {
            PInvoke.CfConvertToPlaceholder(handle, pId, (uint)identity.Length,
                CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC, null, null).ThrowOnFailure();
        }
    }

    /// <summary>True if the file is a cloud-only (dehydrated) placeholder with no local data.</summary>
    public static bool IsDehydrated(string fullPath) =>
        ((int)new FileInfo(fullPath).Attributes & FileAttributeRecallOnDataAccess) != 0;

    private const int FileAttributeRecallOnDataAccess = 0x00400000;

    private static SafeFileHandle OpenFileHandle(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

    /// <summary>Cloud Files FETCH_DATA callback: Windows needs a byte range hydrated.</summary>
    private unsafe void OnFetchData(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        var fetch = callbackParameters->Anonymous.FetchData;
        var connectionKey = callbackInfo->ConnectionKey;
        var transferKey = callbackInfo->TransferKey;
        var offset = fetch.RequiredFileOffset;
        var length = fetch.RequiredLength;

        var key = Encoding.UTF8.GetString(
            (byte*)callbackInfo->FileIdentity, (int)callbackInfo->FileIdentityLength);

        // Service the transfer off the callback thread (S3 I/O must not block the filter callback).
        _ = Task.Run(() => HydrateAsync(connectionKey, transferKey, key, offset, length));
    }

    /// <summary>Size of each parallel hydration range request. A multiple of the sector size.</summary>
    private const int HydrationChunkSizeBytes = 4 << 20; // 4 MiB

    /// <summary>Concurrent range GETs per hydration. Peak buffer use is this × the chunk size.</summary>
    private const int HydrationStreams = 8;

    private async Task HydrateAsync(CF_CONNECTION_KEY connectionKey, long transferKey, string key, long offset, long length)
    {
        try
        {
            // One sequential GET leaves most of the link idle: Hetzner serves a single stream far
            // slower than several in parallel. Anything spanning more than one chunk is fetched as
            // concurrent ranges; small reads (thumbnails, metadata probes) keep the simple path,
            // where the extra requests would cost more than they save.
            if (length > HydrationChunkSizeBytes)
                await HydrateParallelAsync(connectionKey, transferKey, key, offset, length).ConfigureAwait(false);
            else
                await HydrateSequentialAsync(connectionKey, transferKey, key, offset, length).ConfigureAwait(false);

            Hydrated?.Invoke(key);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Hydration of '{key}' failed: {ex.Message}");
            TransferError(connectionKey, transferKey, offset, length);
        }
    }

    private async Task HydrateSequentialAsync(
        CF_CONNECTION_KEY connectionKey, long transferKey, string key, long offset, long length)
    {
        await using var stream = await _openRead(key, offset, length, CancellationToken.None).ConfigureAwait(false);

        // cfapi requires each non-final chunk length to be a multiple of the sector size.
        const int chunkSize = 1 << 20; // 1 MiB
        var buffer = new byte[chunkSize];
        var position = offset;
        var remaining = length;

        while (remaining > 0)
        {
            var want = (int)Math.Min(chunkSize, remaining);
            var read = await ReadAtLeastAsync(stream, buffer, want).ConfigureAwait(false);
            if (read <= 0) break;
            TransferData(connectionKey, transferKey, buffer, position, read);
            position += read;
            remaining -= read;
        }
    }

    private async Task HydrateParallelAsync(
        CF_CONNECTION_KEY connectionKey, long transferKey, string key, long offset, long length)
    {
        var end = offset + length;
        var chunks = new List<(long Offset, int Length)>();
        for (var pos = offset; pos < end; pos += HydrationChunkSizeBytes)
            chunks.Add((pos, (int)Math.Min(HydrationChunkSizeBytes, end - pos)));

        // CfExecute calls for a single transfer key are serialised through this gate; only the
        // network reads overlap. Windows accepts the completed ranges in any order.
        using var transferGate = new SemaphoreSlim(1, 1);

        await Parallel.ForEachAsync(
            chunks,
            new ParallelOptions { MaxDegreeOfParallelism = HydrationStreams },
            async (chunk, ct) =>
            {
                var buffer = ArrayPool<byte>.Shared.Rent(chunk.Length);
                try
                {
                    await using var stream = await _openRead(key, chunk.Offset, chunk.Length, ct)
                        .ConfigureAwait(false);
                    var read = await ReadAtLeastAsync(stream, buffer, chunk.Length).ConfigureAwait(false);

                    // A short read would leave a hole in the range, and Windows would wait forever
                    // for bytes that are never coming. Fail loudly so the outer catch reports it.
                    if (read < chunk.Length)
                        throw new IOException(
                            $"Short read at offset {chunk.Offset}: wanted {chunk.Length} bytes, got {read}.");

                    await transferGate.WaitAsync(ct).ConfigureAwait(false);
                    try { TransferData(connectionKey, transferKey, buffer, chunk.Offset, read); }
                    finally { transferGate.Release(); }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }).ConfigureAwait(false);
    }

    private static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, count - total)).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private unsafe void TransferData(CF_CONNECTION_KEY connectionKey, long transferKey, byte[] buffer, long offset, int count)
    {
        fixed (byte* p = buffer)
        {
            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)sizeof(CF_OPERATION_INFO),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                ConnectionKey = connectionKey,
                TransferKey = transferKey,
            };
            var opParams = new CF_OPERATION_PARAMETERS { ParamSize = TransferDataParamSize };
            opParams.Anonymous.TransferData.Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE;
            opParams.Anonymous.TransferData.CompletionStatus = new NTSTATUS(0); // STATUS_SUCCESS
            opParams.Anonymous.TransferData.Buffer = p;
            opParams.Anonymous.TransferData.Offset = offset;
            opParams.Anonymous.TransferData.Length = count;
            PInvoke.CfExecute(opInfo, ref opParams).ThrowOnFailure();
        }
    }

    private unsafe void TransferError(CF_CONNECTION_KEY connectionKey, long transferKey, long offset, long length)
    {
        try
        {
            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)sizeof(CF_OPERATION_INFO),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                ConnectionKey = connectionKey,
                TransferKey = transferKey,
            };
            var opParams = new CF_OPERATION_PARAMETERS { ParamSize = TransferDataParamSize };
            opParams.Anonymous.TransferData.Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE;
            opParams.Anonymous.TransferData.CompletionStatus = new NTSTATUS(unchecked((int)0xC0000185)); // STATUS_IO_DEVICE_ERROR
            opParams.Anonymous.TransferData.Buffer = null;
            opParams.Anonymous.TransferData.Offset = offset;
            opParams.Anonymous.TransferData.Length = length;
            PInvoke.CfExecute(opInfo, ref opParams);
        }
        catch { /* nothing more we can do */ }
    }

    // ParamSize for a TRANSFER_DATA op = offset of the union + size of the TransferData member.
    private static readonly uint TransferDataParamSize =
        (uint)Marshal.OffsetOf<CF_OPERATION_PARAMETERS>(nameof(CF_OPERATION_PARAMETERS.Anonymous))
        + (uint)Marshal.SizeOf<CF_OPERATION_PARAMETERS._Anonymous_e__Union._TransferData_e__Struct>();

    public void Dispose() => Disconnect();
}

/// <summary>Data needed to create one file placeholder.</summary>
/// <param name="FileName">Leaf file name within its directory.</param>
/// <param name="FileIdentity">Opaque identity we get back on hydration (the full remote key).</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="LastModifiedUtc">Last-modified timestamp.</param>
public sealed record PlaceholderInfo(string FileName, string FileIdentity, long Size, DateTime LastModifiedUtc);
