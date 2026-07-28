using System.Runtime.Versioning;
using CloudDrive.Core.Models;
using CloudDrive.Core.Stores;

namespace CloudDrive.Core.OAuth;

/// <summary>
/// Client ids an administrator has supplied, in <c>%ProgramData%\CloudDrive\oauth-clients.json</c>.
///
/// This file is the reason "bundled default" works before CloudDrive's own registrations exist: a
/// deployment can point every machine at its own Azure AD application and Google project once,
/// without a rebuild and without editing each account by hand.
/// </summary>
public sealed class OAuthClientFile
{
    /// <summary>Azure AD application (client) ID for OneDrive and SharePoint.</summary>
    public string? MicrosoftClientId { get; set; }

    /// <summary>Default Azure AD tenant: <c>common</c>, <c>organizations</c>, or a tenant GUID.</summary>
    public string? MicrosoftTenant { get; set; }

    /// <summary>Google Cloud OAuth client ID, of type "Desktop app".</summary>
    public string? GoogleClientId { get; set; }

    /// <summary>
    /// Google issues a "client secret" even to desktop clients. It is not a secret in any meaningful
    /// sense — it ships inside every installed copy of the app — but Google's token endpoint rejects
    /// the exchange without it, so it has to be carried. Kept here rather than in the credential
    /// store precisely because it protects nothing.
    /// </summary>
    public string? GoogleClientSecret { get; set; }
}

/// <summary>Resolves which client id an account should authorise with.</summary>
[SupportedOSPlatform("windows")]
public sealed class OAuthClientRegistry
{
    private readonly JsonFileStore<OAuthClientFile> _store;

    public OAuthClientRegistry(string? path = null) =>
        _store = new JsonFileStore<OAuthClientFile>(
            path ?? Path.Combine(AppPaths.MachineDir, "oauth-clients.json"), machineScope: true);

    public string FilePath => _store.Path;

    public OAuthClientFile Load() => _store.TryLoad() ?? new OAuthClientFile();

    /// <summary>
    /// The client id to use for <paramref name="account"/>, and where it came from.
    ///
    /// Precedence is account override, then the machine file, then whatever is compiled in. The
    /// account override wins because it is the most specific and the most deliberate: someone who
    /// typed a client id into one account meant that account to use it.
    /// </summary>
    public (string ClientId, string Source) Resolve(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (!string.IsNullOrWhiteSpace(account.OAuthClientIdOverride))
            return (account.OAuthClientIdOverride!.Trim(), "this account");

        var file = Load();
        var fromFile = account.Provider switch
        {
            ProviderId.OneDrive => file.MicrosoftClientId,
            ProviderId.GoogleDrive => file.GoogleClientId,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(fromFile))
            return (fromFile!.Trim(), System.IO.Path.GetFileName(_store.Path));

        var bundled = OAuthProviders.BundledClientIdFor(account.Provider);
        return string.IsNullOrWhiteSpace(bundled)
            ? (string.Empty, "nowhere")
            : (bundled, "this build");
    }

    /// <summary>The Google client secret, when one is configured. Null for Microsoft.</summary>
    public string? ClientSecretFor(Account account)
    {
        if (account.Provider != ProviderId.GoogleDrive) return null;
        var configured = Load().GoogleClientSecret;
        return string.IsNullOrWhiteSpace(configured) ? null : configured!.Trim();
    }

    /// <summary>The Azure AD tenant for an account: its own, then the machine default, then common.</summary>
    public string TenantFor(Account account)
    {
        if (!string.IsNullOrWhiteSpace(account.TenantId)) return account.TenantId!.Trim();
        var configured = Load().MicrosoftTenant;
        // "common" accepts both personal Microsoft accounts and any organisation, which is the widest
        // audience and the right default for a tool a user runs on their own machine.
        return string.IsNullOrWhiteSpace(configured) ? "common" : configured!.Trim();
    }

    /// <summary>
    /// Why sign-in cannot proceed, phrased so the user knows what to do, or null when it can.
    /// </summary>
    public string? DescribeMissingClientId(Account account)
    {
        var (clientId, _) = Resolve(account);
        if (!string.IsNullOrWhiteSpace(clientId)) return null;

        var config = OAuthProviders.Require(account.Provider);
        return $"""
            No OAuth client ID is configured for {config.DisplayName}, so CloudDrive cannot start a
            sign-in.

            Register an application at:
              {config.RegistrationUrl}

            {(account.Provider == ProviderId.OneDrive
                ? "Choose 'Mobile and desktop applications' as the platform and add the redirect URI\n  http://localhost"
                : "Choose 'Desktop app' as the application type.")}

            Then supply the client ID either in this account's advanced settings, or for the whole
            machine in:
              {_store.Path}

            See docs/OAUTH.md for the full walkthrough.
            """;
    }
}
