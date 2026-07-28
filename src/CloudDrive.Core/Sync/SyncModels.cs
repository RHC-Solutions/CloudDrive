namespace CloudDrive.Core.Sync;

/// <summary>Last-known synced state of one object (used to detect local/remote changes).</summary>
public sealed class SyncEntry
{
    public string Key { get; set; } = string.Empty;
    public string? ETag { get; set; }
    public long Size { get; set; }
    public long RemoteModifiedUtcTicks { get; set; }

    /// <summary>Local last-write time we recorded at the last successful sync (0 if unknown).</summary>
    public long LocalModifiedUtcTicks { get; set; }
}

/// <summary>What to do with a locally-changed file when pushing to the cloud.</summary>
public enum UploadAction
{
    /// <summary>Local content matches what we last synced — nothing to do.</summary>
    Skip,

    /// <summary>Local changed; upload it.</summary>
    Upload,

    /// <summary>Both local and remote changed since the last sync — needs conflict handling.</summary>
    Conflict,
}

/// <summary>What to do with a remote object when pulling changes down into the folder.</summary>
public enum RemoteAction
{
    /// <summary>Remote matches last sync and a local placeholder exists — nothing to do.</summary>
    Skip,

    /// <summary>No local entry yet — create a placeholder.</summary>
    CreatePlaceholder,

    /// <summary>Remote changed — refresh the placeholder (unless local is dirty).</summary>
    UpdatePlaceholder,

    /// <summary>Remote no longer has the object — remove the local placeholder.</summary>
    DeleteLocal,

    /// <summary>Remote changed but local is also changed — needs conflict handling.</summary>
    Conflict,
}
