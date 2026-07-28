namespace CloudDrive.Core.Models;

/// <summary>One selectable endpoint for a provider that has a fixed region list.</summary>
/// <param name="Code">Region code as the vendor's API expects it, e.g. "us-east-1", "fsn1".</param>
/// <param name="DisplayName">What the user sees, e.g. "US East (N. Virginia)".</param>
/// <param name="Endpoint">Endpoint hostname with no scheme, as rclone and the AWS SDK want it.</param>
public sealed record ProviderRegion(string Code, string DisplayName, string Endpoint);

/// <summary>
/// Everything CloudDrive needs to know about one storage brand, as data rather than as a branch.
///
/// Twelve providers only stays maintainable if adding the thirteenth is a table entry. Every place
/// that would otherwise grow a <c>switch</c> over the brand — the credential form, endpoint
/// resolution, which protocols to offer, whether to show a "Bucket" or "Share" box, whether
/// "Copy share link" is greyed out — reads it from here instead.
///
/// Behaviour that genuinely differs in *code* rather than in configuration still lives in the
/// protocol implementations. The descriptor decides which one to use and how to configure it; it
/// does not try to describe how to move bytes.
/// </summary>
public sealed record ProviderDescriptor
{
    public required ProviderId Id { get; init; }

    /// <summary>Brand name as shown in the provider picker.</summary>
    public required string DisplayName { get; init; }

    /// <summary>One line under the name in the picker, explaining what this is.</summary>
    public required string Description { get; init; }

    /// <summary>Protocols this provider can speak, most preferred first.</summary>
    public required IReadOnlyList<StorageProtocol> Protocols { get; init; }

    public required AuthKind Auth { get; init; }

    public required ProviderCapabilities Capabilities { get; init; }

    /// <summary>
    /// What the container is called in this provider's own vocabulary — "Bucket" for S3, "Share"
    /// for SMB, "Drive" for OneDrive. Used verbatim as the field label, because a Wasabi user
    /// looking for "Bucket" should not have to guess that CloudDrive means it by "Container".
    /// </summary>
    public string ContainerLabel { get; init; } = "Path";

    /// <summary>Fixed region list, or empty when the endpoint is typed in freely.</summary>
    public IReadOnlyList<ProviderRegion> Regions { get; init; } = [];

    /// <summary>Default region code for a new account, when <see cref="Regions"/> is non-empty.</summary>
    public string? DefaultRegion { get; init; }

    /// <summary>Default TCP port, or 0 when the protocol's own default applies.</summary>
    public int DefaultPort { get; init; }

    /// <summary>
    /// rclone's <c>provider</c> value for S3 dialects. rclone ships per-vendor quirk sets
    /// (signature version, addressing style, which ACL and storage-class headers to omit); naming
    /// the vendor is meaningfully better than letting "Other" guess.
    /// </summary>
    public string? RcloneS3Provider { get; init; }

    /// <summary>Web console for this provider, opened from the Explorer right-click menu.</summary>
    public string? ConsoleUrl { get; init; }

    /// <summary>Accent colour for the provider chip in the mappings list, as #RRGGBB.</summary>
    public string AccentColor { get; init; } = "#EC6327";

    // --- Convenience predicates, so call sites read as intent rather than as bit twiddling ---

    public bool Has(ProviderCapabilities capability) => (Capabilities & capability) == capability;

    /// <summary>True when the user picks a protocol; false when there is only ever one.</summary>
    public bool SupportsProtocolBenchmark => Protocols.Count > 1;

    /// <summary>The protocol to use when nothing has been chosen or measured yet.</summary>
    public StorageProtocol DefaultProtocol =>
        SupportsProtocolBenchmark ? StorageProtocol.Auto : Protocols[0];

    /// <summary>The protocol an unresolved <see cref="StorageProtocol.Auto"/> falls back to.</summary>
    public StorageProtocol FallbackProtocol => Protocols[0];

    public bool IsS3 => Protocols.Contains(StorageProtocol.S3);

    public bool IsOAuth => Auth == AuthKind.OAuth;
}
