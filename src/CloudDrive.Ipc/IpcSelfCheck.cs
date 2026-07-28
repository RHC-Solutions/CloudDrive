using System.Runtime.Versioning;
using System.Text.Json;

namespace CloudDrive.Ipc;

/// <summary>
/// Confirms the IPC wire format works in this process before anything depends on it.
///
/// Serialising an envelope and reading it back exercises the whole path a real request takes,
/// including the enum converter — which is where a missing framework assembly showed up as connections
/// that were accepted and then silently dropped. A startup check turns that into one clear message.
/// </summary>
[SupportedOSPlatform("windows")]
public static class IpcSelfCheck
{
    /// <summary>
    /// Why IPC cannot work in this process, or null when it can.
    /// </summary>
    public static string? Describe()
    {
        try
        {
            var probe = new IpcMessage
            {
                Id = "selfcheck",
                // Both enum properties matter: this is the pair that has to survive a round trip for
                // any request at all to be understood.
                Kind = IpcMessageKind.Request,
                Operation = IpcOperation.Ping,
                Payload = JsonSerializer.SerializeToElement(new { ok = true }, IpcJson.Options),
            };

            var wire = IpcJson.Serialize(probe);
            var parsed = JsonSerializer.Deserialize<IpcMessage>(wire, IpcJson.Options);

            if (parsed is null)
                return "The CloudDrive IPC self-check produced no message. This build is broken.";

            if (parsed.Kind != IpcMessageKind.Request || parsed.Operation != IpcOperation.Ping)
            {
                return "The CloudDrive IPC self-check did not round-trip correctly: "
                       + $"got Kind={parsed.Kind}, Operation={parsed.Operation}. This build is broken.";
            }

            return null;
        }
        catch (FileNotFoundException ex)
        {
            // The characteristic failure: an assembly the JSON stack needs is not resolvable. The
            // message on these is usually empty, so the assembly name has to be dug out of FileName.
            return $"""
                CloudDrive cannot use its IPC protocol in this process, so no client would be able to
                reach the service.

                A required part of the .NET runtime could not be loaded:
                  {ex.FileName ?? "(unknown assembly)"}

                This normally means the deployment is incomplete. Reinstall CloudDrive, or run the
                self-contained build produced by scripts\publish.ps1, which carries its own runtime.
                """;
        }
        catch (Exception ex)
        {
            return $"The CloudDrive IPC self-check failed: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
