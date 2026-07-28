using CloudDrive.Core.Providers;

namespace CloudDrive.Core.Models;

/// <summary>
/// One configured login to one provider. Holds no secrets — those live in <see cref="Credentials"/>,
/// keyed by <see cref="Id"/> in the encrypted store.
///
/// Splitting the account from the mapping is what delivers "multiple accounts from each brand":
/// three AWS logins are three <see cref="Account"/> rows, and any number of <see cref="Mapping"/>
/// rows hang off each. Both source projects tied one credential set to one mount, which made a
/// second account of the same brand mean re-entering the key on every mapping.
/// </summary>
public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What the user calls this login — "Work AWS", "Client backups". Shown everywhere.</summary>
    public string Name { get; set; } = string.Empty;

    public ProviderId Provider { get; set; } = ProviderId.Wasabi;

    // --- Endpoint ------------------------------------------------------------------------------
    // Typed rather than a property bag: host, port, user and region are concepts every non-OAuth
    // back end here genuinely shares. The handful of things only one provider needs go in Options.

    /// <summary>Login name. Unused by S3 (which authenticates by key) and by the OAuth providers.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Server hostname for the file protocols and for a custom S3 endpoint. Blank means "derive it",
    /// which is what a region-based provider and a Hetzner Storage Box both do — see <see cref="Host"/>.
    /// </summary>
    public string? HostOverride { get; set; }

    /// <summary>TCP port, or 0 to use the provider's default.</summary>
    public int Port { get; set; }

    /// <summary>Region or location code for a provider with a fixed region list.</summary>
    public string? RegionCode { get; set; }

    /// <summary>Windows domain or workgroup for SMB. Blank is treated as WORKGROUP.</summary>
    public string? Domain { get; set; }

    /// <summary>Use TLS: FTPS for FTP, HTTPS for a custom S3 or WebDAV endpoint. Ignored elsewhere.</summary>
    public bool UseTls { get; set; } = true;

    /// <summary>
    /// Which protocol to talk to this account over. <see cref="StorageProtocol.Auto"/> measures the
    /// reachable ones and uses the fastest; only meaningful for a multi-protocol provider.
    /// </summary>
    public StorageProtocol Protocol { get; set; } = StorageProtocol.Auto;

    /// <summary>The winner the last <see cref="StorageProtocol.Auto"/> benchmark settled on.</summary>
    public StorageProtocol? ResolvedProtocol { get; set; }

    /// <summary>When <see cref="ResolvedProtocol"/> was measured, so a stale pick can be refreshed.</summary>
    public DateTime? ProtocolMeasuredUtc { get; set; }

    // --- OAuth ---------------------------------------------------------------------------------

    /// <summary>The signed-in identity (UPN or email). Display only; the token is the real credential.</summary>
    public string? OAuthIdentity { get; set; }

    /// <summary>
    /// A client id of the user's own, overriding the one CloudDrive ships. Needed by anyone whose
    /// tenant blocks third-party applications, and by anyone who would rather not depend on
    /// CloudDrive's registration staying verified.
    /// </summary>
    public string? OAuthClientIdOverride { get; set; }

    /// <summary>Azure AD tenant for a OneDrive business account; "common" for personal.</summary>
    public string? TenantId { get; set; }

    /// <summary>Graph drive id or Google shared-drive id, when not the account's default drive.</summary>
    public string? DriveId { get; set; }

    /// <summary>When the stored refresh token was last exchanged successfully.</summary>
    public DateTime? TokenRefreshedUtc { get; set; }

    /// <summary>
    /// Set when the last refresh failed in a way only a human can fix — a revoked grant, a changed
    /// password, an expired unverified-app token. Drives the "re-authorisation required" alert and
    /// the warning badge on the account.
    /// </summary>
    public string? ReauthRequiredReason { get; set; }

    /// <summary>Provider-specific odds and ends that do not deserve a typed field.</summary>
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // --- Derived -------------------------------------------------------------------------------

    public ProviderDescriptor Descriptor => ProviderCatalog.Get(Provider);

    /// <summary>
    /// The hostname to connect to. An explicit override always wins; otherwise it comes from the
    /// region table, or — for a Storage Box — from the username, which is the only provider here
    /// whose hostname is a function of the account name.
    /// </summary>
    public string Host
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(HostOverride)) return HostOverride!.Trim();
            if (Provider == ProviderId.HetznerStorageBox) return StorageBox.HostFor(Username);
            return ProviderCatalog.FindRegion(Provider, RegionCode)?.Endpoint ?? string.Empty;
        }
    }

    /// <summary>The port to connect on, falling back to the provider's default.</summary>
    public int EffectivePort => Port > 0 ? Port : Descriptor.DefaultPort;

    /// <summary>
    /// The protocol to actually use right now: the explicit choice, else the cached Auto winner,
    /// else the provider's fallback. Never returns <see cref="StorageProtocol.Auto"/>.
    /// </summary>
    public StorageProtocol EffectiveProtocol
    {
        get
        {
            var descriptor = Descriptor;
            if (!descriptor.SupportsProtocolBenchmark) return descriptor.Protocols[0];
            if (Protocol != StorageProtocol.Auto) return Protocol;
            return ResolvedProtocol ?? descriptor.FallbackProtocol;
        }
    }

    /// <summary>True when a human must complete an interactive sign-in before this account works.</summary>
    public bool NeedsReauth => !string.IsNullOrWhiteSpace(ReauthRequiredReason);

    /// <summary>Short "who and where" line for lists and log messages.</summary>
    public string Summary => Descriptor.Auth switch
    {
        AuthKind.OAuth => OAuthIdentity ?? Descriptor.DisplayName,
        AuthKind.KeyPair => ProviderCatalog.FindRegion(Provider, RegionCode)?.DisplayName
                            ?? Host,
        _ => string.IsNullOrWhiteSpace(Username) ? Host : $"{Username}@{Host}",
    };

    public Account Clone() => (Account)MemberwiseClone();
}

/// <summary>
/// The derivations a Hetzner Storage Box needs, which no other provider does: its hostname, its SMB
/// share name and its sub-account rules are all functions of the username rather than things the
/// user types.
/// </summary>
public static class StorageBox
{
    public const string Domain = "your-storagebox.de";

    /// <summary>The share a main account's files live in; a sub-account uses its own name instead.</summary>
    public const string MainAccountShare = "backup";

    /// <summary>
    /// Hetzner caps a Storage Box at roughly ten concurrent SSH sessions. Going over does not queue:
    /// the server refuses the extra sessions and rclone surfaces them as checksum errors partway
    /// through a large copy, so the SFTP tuning is clamped to stay underneath.
    /// </summary>
    public const int MaxSshConnections = 10;

    /// <summary>
    /// Hostname for a Storage Box user. Sub-accounts (<c>u123456-sub1</c>) get their own hostname,
    /// so the username is used verbatim rather than reduced to the parent account.
    /// </summary>
    public static string HostFor(string? username)
    {
        var user = (username ?? string.Empty).Trim();
        if (user.Length == 0) return string.Empty;
        // Tolerate someone pasting the full hostname into the username box.
        if (user.EndsWith("." + Domain, StringComparison.OrdinalIgnoreCase)) return user.ToLowerInvariant();
        return $"{user.ToLowerInvariant()}.{Domain}";
    }

    /// <summary>The account name, from either a bare username or a full Storage Box hostname.</summary>
    public static string UserFor(string? usernameOrHost)
    {
        var value = (usernameOrHost ?? string.Empty).Trim().ToLowerInvariant();
        var suffix = "." + Domain;
        return value.EndsWith(suffix, StringComparison.Ordinal) ? value[..^suffix.Length] : value;
    }

    public static bool IsSubAccount(string? username) =>
        UserFor(username).Contains("-sub", StringComparison.OrdinalIgnoreCase);

    /// <summary>SMB share: the main account exposes <c>backup</c>, a sub-account a share named after itself.</summary>
    public static string ShareFor(string? username)
    {
        var user = UserFor(username);
        return IsSubAccount(user) ? user : MainAccountShare;
    }
}
