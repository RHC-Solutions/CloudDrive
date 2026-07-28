namespace CloudDrive.Core.Models;

/// <summary>
/// Machine-wide settings, persisted to <c>settings.json</c> in the machine store. Contains no
/// secrets: notification tokens and SMTP passwords live in the credential store, referenced from
/// here by id.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Default cache tuning applied to newly created mappings.</summary>
    public CacheSettings DefaultCache { get; set; } = CacheSettings.Default();

    /// <summary>
    /// Payload size in MiB for the Auto protocol benchmark. Bigger is more accurate on a fast link
    /// and slower to run; the default clears TCP slow start without making mounting feel sluggish.
    /// </summary>
    public int BenchmarkPayloadMiB { get; set; } = 16;

    /// <summary>
    /// Re-run the Auto benchmark on every mount instead of reusing the cached winner. Off by
    /// default — the measurement costs seconds and a link rarely changes character — but useful on
    /// a laptop that moves between very different networks.
    /// </summary>
    public bool AlwaysReBenchmark { get; set; }

    /// <summary>How long a measured protocol choice is trusted before it is measured again.</summary>
    public int ProtocolCacheDays { get; set; } = 14;

    /// <summary>Run rclone mounts at DEBUG level.</summary>
    public bool VerboseLogging { get; set; }

    /// <summary>Days of daily log files to keep.</summary>
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>Launch the tray app at user logon.</summary>
    public bool StartAtLogin { get; set; } = true;

    /// <summary>Close the window to the tray instead of exiting.</summary>
    public bool MinimizeToTray { get; set; } = true;

    public UpdateSettings Updates { get; set; } = new();

    public ToolSettings Tools { get; set; } = new();

    public NotificationSettings Notifications { get; set; } = new();
}

/// <summary>How CloudDrive updates itself.</summary>
public sealed class UpdateSettings
{
    /// <summary>Check the release feed at all.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Download and install a new release without being asked, once the machine is idle. When off,
    /// an update is only ever offered, and <see cref="NotifyOnAvailable"/> is how the user hears
    /// about it.
    /// </summary>
    public bool AutoInstallWhenIdle { get; set; } = true;

    /// <summary>Send an alert when a new release is found.</summary>
    public bool NotifyOnAvailable { get; set; } = true;

    /// <summary>Send an alert before applying an update, so a watching administrator can intervene.</summary>
    public bool NotifyBeforeInstall { get; set; } = true;

    /// <summary>Send an alert reporting the outcome after an update is applied.</summary>
    public bool NotifyAfterInstall { get; set; } = true;

    /// <summary>
    /// How often to poll the release feed. Jittered per machine so a fleet installed from one image
    /// does not query GitHub in lockstep.
    /// </summary>
    public int CheckIntervalHours { get; set; } = 6;

    /// <summary>
    /// How long everything must have been quiet before an update may be applied. This gates mount
    /// I/O, open handles, in-flight hydration, and — when someone is signed in — keyboard and mouse
    /// input. Nobody signed in is the normal case on a server and does not block anything.
    /// </summary>
    public int IdleMinutesBeforeInstall { get; set; } = 10;

    /// <summary>
    /// Optional maintenance window, as local <c>HH:mm</c>. When both are set, updates apply only
    /// inside it — a window that crosses midnight (22:00 to 04:00) is handled.
    /// </summary>
    public string? MaintenanceWindowStart { get; set; }

    public string? MaintenanceWindowEnd { get; set; }

    /// <summary>Accept prereleases from the feed. Off: only full releases are offered.</summary>
    public bool IncludePrereleases { get; set; }

    /// <summary>Nothing before this version is offered again — set when a user skips a release.</summary>
    public string? SkippedVersion { get; set; }
}

/// <summary>How the managed third-party tools (rclone, WinFsp, sshfs-win) are kept current.</summary>
public sealed class ToolSettings
{
    /// <summary>Poll each tool's vendor for a newer version.</summary>
    public bool CheckForToolUpdates { get; set; } = true;

    /// <summary>
    /// Install a newer tool automatically once idle. Held to the same idle gate as an app update,
    /// because swapping the rclone binary means remounting everything that uses it.
    /// </summary>
    public bool AutoInstallWhenIdle { get; set; } = true;

    public int CheckIntervalHours { get; set; } = 24;

    /// <summary>
    /// Register the managed tools directory on the machine <c>PATH</c>, so <c>rclone</c> works from
    /// any shell. Machine scope rather than user scope: the service has no user hive, which is the
    /// same reason the configuration lives in <c>%ProgramData%</c>.
    /// </summary>
    public bool AddToSystemPath { get; set; } = true;

    /// <summary>How many superseded versions of a tool to keep for rollback.</summary>
    public int KeepPreviousVersions { get; set; } = 2;
}

/// <summary>Which channels alerts go to, and how loudly.</summary>
public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;

    public List<NotificationTarget> Targets { get; set; } = [];

    /// <summary>
    /// How long the same (event type, mapping) pair is suppressed after firing. Without this, a
    /// mount that flaps every few seconds turns into hundreds of messages and the channel gets muted
    /// — which is worse than no alerting at all.
    /// </summary>
    public int DedupeCooldownMinutes { get; set; } = 15;

    /// <summary>
    /// Replace per-event delivery with one periodic summary. 0 disables digests and sends events as
    /// they happen.
    /// </summary>
    public int DigestIntervalMinutes { get; set; }

    /// <summary>Longest an undeliverable alert stays in the on-disk spool before it is dropped.</summary>
    public int SpoolRetentionHours { get; set; } = 48;
}

/// <summary>Where alerts are sent. The secret half lives in the credential store under <see cref="Id"/>.</summary>
public sealed class NotificationTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public NotificationChannelKind Kind { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Nothing below this severity is delivered to this target.</summary>
    public AlertSeverity MinimumSeverity { get; set; } = AlertSeverity.Warning;

    /// <summary>
    /// Event types this target accepts. Empty means every type at or above
    /// <see cref="MinimumSeverity"/>.
    /// </summary>
    public List<AlertKind> EventFilter { get; set; } = [];

    // --- Non-secret channel configuration -----------------------------------------------------

    /// <summary>Telegram: the numeric chat id or an @channel name.</summary>
    public string? TelegramChatId { get; set; }

    /// <summary>Telegram: the topic id, for a chat with topics enabled.</summary>
    public int? TelegramThreadId { get; set; }

    /// <summary>Slack: the channel to post to, when using a bot token rather than a webhook.</summary>
    public string? SlackChannel { get; set; }

    /// <summary>Email: SMTP server hostname.</summary>
    public string? SmtpHost { get; set; }

    public int SmtpPort { get; set; } = 587;

    /// <summary>Email: use STARTTLS or implicit TLS, decided from the port.</summary>
    public bool SmtpUseTls { get; set; } = true;

    public string? SmtpUsername { get; set; }

    public string? EmailFrom { get; set; }

    public List<string> EmailTo { get; set; } = [];
}

public enum NotificationChannelKind
{
    Telegram,
    Slack,
    Email,
}

public enum AlertSeverity
{
    /// <summary>Something happened and worked. Mounted, updated, started.</summary>
    Info,

    /// <summary>Degraded but self-healing. A mount dropped and was restarted.</summary>
    Warning,

    /// <summary>Broken and staying broken until someone acts.</summary>
    Error,
}

/// <summary>
/// What happened. Kept as an enum rather than a free string so a target can filter on it and the
/// deduplicator can key on it.
/// </summary>
public enum AlertKind
{
    ServiceStarted,
    ServiceStopping,
    MountSucceeded,
    MountFailed,
    MountLost,
    MountRestarted,
    MountGaveUp,
    CredentialsRejected,

    /// <summary>An OAuth refresh failed in a way only an interactive sign-in can fix.</summary>
    ReauthRequired,

    SyncConflict,
    SyncFailing,
    CacheDiskLow,
    QuotaNearLimit,
    UpdateAvailable,
    UpdateInstalling,
    UpdateInstalled,
    UpdateFailed,
    ToolUpdated,
    TestMessage,
}
