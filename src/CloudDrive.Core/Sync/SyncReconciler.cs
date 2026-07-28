namespace CloudDrive.Core.Sync;

/// <summary>
/// Pure decision logic for two-way sync — no I/O, so it is fully unit-testable. Given the last
/// known synced state plus the current local and/or remote facts, it decides what to do.
/// Conflict policy here is detection only; the caller chooses how to resolve (this app uses
/// last-writer-wins by timestamp).
/// </summary>
public static class SyncReconciler
{
    /// <summary>
    /// Decides how to handle a local file that a watcher reported as changed.
    /// </summary>
    /// <param name="localSize">Current local size.</param>
    /// <param name="localModifiedUtcTicks">Current local last-write (UTC ticks).</param>
    /// <param name="known">Last synced state for this key, or null if never synced.</param>
    /// <param name="remoteChangedSinceSync">
    /// True if the current remote object differs from <paramref name="known"/> (remote moved on).
    /// </param>
    public static UploadAction DecideUpload(
        long localSize, long localModifiedUtcTicks, SyncEntry? known, bool remoteChangedSinceSync)
    {
        var localChanged = known is null
            || known.Size != localSize
            || known.LocalModifiedUtcTicks != localModifiedUtcTicks;

        if (!localChanged)
            return UploadAction.Skip;

        return remoteChangedSinceSync ? UploadAction.Conflict : UploadAction.Upload;
    }

    /// <summary>
    /// Decides how to handle a remote object during a pull reconcile.
    /// </summary>
    /// <param name="remoteETag">Remote ETag (null if the object no longer exists).</param>
    /// <param name="remoteExists">Whether the object still exists remotely.</param>
    /// <param name="known">Last synced state, or null if we have never seen it.</param>
    /// <param name="localExists">Whether a local placeholder/file exists.</param>
    /// <param name="localDirty">Whether the local file has unsynced changes.</param>
    public static RemoteAction DecideRemote(
        string? remoteETag, bool remoteExists, SyncEntry? known, bool localExists, bool localDirty)
    {
        if (!remoteExists)
        {
            // Gone remotely. Remove locally unless the user has local changes (then it's a conflict).
            if (!localExists) return RemoteAction.Skip;
            return localDirty ? RemoteAction.Conflict : RemoteAction.DeleteLocal;
        }

        if (!localExists)
            return RemoteAction.CreatePlaceholder;

        var remoteChanged = known is null || !string.Equals(known.ETag, remoteETag, StringComparison.Ordinal);
        if (!remoteChanged)
            return RemoteAction.Skip;

        return localDirty ? RemoteAction.Conflict : RemoteAction.UpdatePlaceholder;
    }
}
