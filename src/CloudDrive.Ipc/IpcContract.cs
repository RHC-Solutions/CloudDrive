using System.Text.Json;
using System.Text.Json.Serialization;
using CloudDrive.Core.Models;
using CloudDrive.Core.Mounting;

namespace CloudDrive.Ipc;

/// <summary>Every operation the service exposes.</summary>
public enum IpcOperation
{
    /// <summary>Liveness plus version, so a client can tell a stopped service from an old one.</summary>
    Ping,

    // --- Reading (any authenticated user) ---
    GetState,
    GetStatus,
    GetLogTail,
    GetToolState,
    GetCapabilities,

    // --- Mount control ---
    Mount,
    Unmount,
    RemountAll,

    // --- Configuration (administrators) ---
    SaveAccount,
    DeleteAccount,
    TestAccount,
    SaveMapping,
    DeleteMapping,
    SaveSettings,

    /// <summary>
    /// Releases an account's credentials to the caller so the tray app can run a Files On-Demand
    /// root in the user's session. Guarded hard: see <see cref="IpcRequest"/>.
    /// </summary>
    GetCredentialsForOnDemand,

    // --- Notifications ---
    SaveNotificationTarget,
    DeleteNotificationTarget,
    SendTestAlert,

    // --- Updates and tools ---
    CheckForUpdate,
    InstallUpdate,
    SkipUpdate,
    CheckToolUpdates,
    InstallTool,
    RollbackTool,

    /// <summary>Opens a live event stream on this connection. No response body; events follow.</summary>
    Subscribe,
}

/// <summary>An envelope on the wire. One JSON object per line, in both directions.</summary>
public sealed class IpcMessage
{
    /// <summary>Correlates a response with its request. Null on a server-pushed event.</summary>
    public string? Id { get; set; }

    public IpcMessageKind Kind { get; set; }

    public IpcOperation Operation { get; set; }

    /// <summary>Operation-specific body, as raw JSON so the envelope does not need to know the shape.</summary>
    public JsonElement? Payload { get; set; }

    /// <summary>Set on a failed response. Null means success.</summary>
    public string? Error { get; set; }
}

public enum IpcMessageKind
{
    Request,
    Response,
    Event,
}

/// <summary>Shared serialiser settings. Both ends must agree, so they live in one place.</summary>
public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        // Compact: these go over a pipe, not into a file a human reads.
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(JsonElement? element) =>
        element is null ? default : element.Value.Deserialize<T>(Options);
}

// ---------------------------------------------------------------------- Payloads --------------

/// <summary>Everything the UI needs to draw itself, in one round trip.</summary>
public sealed class ServiceSnapshot
{
    public string ServiceVersion { get; set; } = string.Empty;

    public List<Account> Accounts { get; set; } = [];

    public List<Mapping> Mappings { get; set; } = [];

    public AppSettings Settings { get; set; } = new();

    public List<MountStatus> Mounts { get; set; } = [];

    /// <summary>Non-fatal problems worth showing as a banner: no rclone, no WinFsp, an orphaned mapping.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>True when the caller may change configuration. Drives whether the UI is read-only.</summary>
    public bool CallerIsAdministrator { get; set; }
}

/// <summary>What this machine can do, so the UI can hide what will not work.</summary>
public sealed class CapabilityReport
{
    public bool SupportsFilesOnDemand { get; set; }

    public string? FilesOnDemandUnavailableReason { get; set; }

    public bool IsServerCore { get; set; }

    public string EditionName { get; set; } = string.Empty;

    public int BuildNumber { get; set; }

    public bool WinFspInstalled { get; set; }

    public string? RclonePath { get; set; }

    public string? RcloneVersion { get; set; }
}

public sealed class MountRequest
{
    public Guid MappingId { get; set; }
}

public sealed class SaveAccountRequest
{
    public Account Account { get; set; } = new();

    /// <summary>
    /// New or changed secrets. Null leaves the stored credentials alone, which is how an edit that
    /// only renames an account avoids making the user retype a password.
    /// </summary>
    public Credentials? Credentials { get; set; }
}

public sealed class SaveMappingRequest
{
    public Mapping Mapping { get; set; } = new();
}

public sealed class DeleteRequest
{
    public Guid Id { get; set; }
}

/// <summary>The result of trying an account's credentials without mounting anything.</summary>
public sealed class TestAccountResult
{
    public bool Success { get; set; }

    public string? Message { get; set; }

    public StorageProtocol? ProtocolUsed { get; set; }

    /// <summary>Containers found, so the mapping dialog can offer a list instead of a text box.</summary>
    public List<string> Containers { get; set; } = [];
}

public sealed class LogTailRequest
{
    public int Lines { get; set; } = 500;
}

public sealed class LogTailResult
{
    public List<string> Lines { get; set; } = [];
}

/// <summary>Credentials released for a user-session on-demand mapping.</summary>
public sealed class OnDemandCredentialsResult
{
    public Account Account { get; set; } = new();

    public Credentials Credentials { get; set; } = new();
}

public sealed class UpdateCheckResult
{
    public bool UpdateAvailable { get; set; }

    public string CurrentVersion { get; set; } = string.Empty;

    public string? AvailableVersion { get; set; }

    public string? ReleaseNotes { get; set; }

    public string? ReleaseUrl { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Set when an update is found but the machine is not idle enough to apply it.</summary>
    public string? DeferredReason { get; set; }
}

public sealed class ToolStateResult
{
    public List<ToolInfo> Tools { get; set; } = [];

    public DateTime? LastCheckedUtc { get; set; }

    public string ToolsDirectory { get; set; } = string.Empty;

    public bool OnSystemPath { get; set; }
}

public sealed class ToolInfo
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Purpose { get; set; } = string.Empty;

    public string? InstalledVersion { get; set; }

    public string? AvailableVersion { get; set; }

    public bool Required { get; set; }

    public bool CanRollback { get; set; }
}

public sealed class NotificationTargetRequest
{
    public NotificationTarget Target { get; set; } = new();

    /// <summary>Null leaves the stored token alone.</summary>
    public NotificationSecret? Secret { get; set; }
}

// ---------------------------------------------------------------------- Events ----------------

/// <summary>A mount changed state. Pushed to every subscriber.</summary>
public sealed class MountStateEvent
{
    public Guid MappingId { get; set; }

    public MountState State { get; set; }

    public string? Message { get; set; }
}

/// <summary>A log line, so the UI's activity pane is live rather than polled.</summary>
public sealed class LogEvent
{
    public Guid? MappingId { get; set; }

    public string Line { get; set; } = string.Empty;
}

/// <summary>The configuration changed, so a client should refetch. Sent after any mutation.</summary>
public sealed class ConfigChangedEvent
{
    public string? Reason { get; set; }
}

/// <summary>An update was found, is being applied, or finished.</summary>
public sealed class UpdateEvent
{
    public string? Version { get; set; }

    public string Stage { get; set; } = string.Empty;

    public string? Message { get; set; }
}
