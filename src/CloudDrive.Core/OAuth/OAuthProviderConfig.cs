using CloudDrive.Core.Models;

namespace CloudDrive.Core.OAuth;

/// <summary>
/// The OAuth 2 endpoints, scopes and client identity for one provider.
///
/// Both providers here are **public clients** in OAuth terms: an installed application cannot keep a
/// secret, so there is none. Security comes from PKCE, which binds the authorisation code to the
/// process that requested it, plus a loopback redirect that only this machine can receive.
/// </summary>
public sealed record OAuthProviderConfig
{
    public required ProviderId Provider { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Authorisation endpoint. May contain <c>{tenant}</c>, substituted per account.</summary>
    public required string AuthorizeEndpoint { get; init; }

    public required string TokenEndpoint { get; init; }

    /// <summary>Space-separated scopes. Must include whatever yields a refresh token.</summary>
    public required string Scopes { get; init; }

    /// <summary>Where the provider's own app registrations are managed, shown in the setup guidance.</summary>
    public required string RegistrationUrl { get; init; }

    /// <summary>
    /// Extra authorisation parameters this provider needs to issue a refresh token. Microsoft uses
    /// the <c>offline_access</c> scope for that; Google needs query parameters instead.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExtraAuthorizeParameters { get; init; } =
        new Dictionary<string, string>();

    /// <summary>An endpoint returning the signed-in identity, so the account can be labelled.</summary>
    public string? IdentityEndpoint { get; init; }

    /// <summary>JSON property on the identity response holding the user's name or address.</summary>
    public IReadOnlyList<string> IdentityFields { get; init; } = [];
}

/// <summary>
/// Endpoint and client-id configuration for the OAuth providers.
///
/// <para><b>On client ids.</b> CloudDrive is designed to ship with its own registrations so sign-in
/// works out of the box, and to let anyone substitute their own. The bundled values live in
/// <see cref="BundledClientIds"/> and are <b>deliberately empty in source</b>: a client id is not a
/// secret, but committing one ties every build to one registration, and a tenant that blocks
/// third-party applications needs its own regardless. Until they are filled in — or an account
/// supplies an override — sign-in explains exactly what to register and where.</para>
///
/// <para>A client id can be supplied three ways, in order of precedence: per account
/// (<see cref="Account.OAuthClientIdOverride"/>), from a
/// <c>%ProgramData%\CloudDrive\oauth-clients.json</c> file an administrator drops in, or from the
/// bundled constants below. The file exists so a deployment can configure this once without a
/// rebuild, which is what makes the "bundled default" story work before the registrations are made.</para>
/// </summary>
public static class OAuthProviders
{
    /// <summary>
    /// Client ids compiled into this build. Empty until CloudDrive's own app registrations exist;
    /// see <c>docs/OAUTH.md</c> for what to register and how to supply them.
    /// </summary>
    public static class BundledClientIds
    {
        /// <summary>Azure AD application (client) ID for OneDrive and SharePoint.</summary>
        public const string Microsoft = "";

        /// <summary>Google Cloud OAuth client ID of type "Desktop app".</summary>
        public const string Google = "";
    }

    public static readonly OAuthProviderConfig Microsoft = new()
    {
        Provider = ProviderId.OneDrive,
        DisplayName = "OneDrive",
        // {tenant} is "common" for a personal account or a mixed audience, or a tenant GUID to pin
        // sign-in to one organisation.
        AuthorizeEndpoint = "https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize",
        TokenEndpoint = "https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
        // offline_access is what makes Microsoft return a refresh token; without it the mount would
        // stop working an hour after sign-in and could never be revived without a browser.
        // Files.ReadWrite.All covers SharePoint document libraries as well as OneDrive.
        Scopes = "offline_access openid profile User.Read Files.ReadWrite.All Sites.ReadWrite.All",
        RegistrationUrl = "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade",
        IdentityEndpoint = "https://graph.microsoft.com/v1.0/me",
        IdentityFields = ["userPrincipalName", "mail", "displayName"],
    };

    public static readonly OAuthProviderConfig Google = new()
    {
        Provider = ProviderId.GoogleDrive,
        DisplayName = "Google Drive",
        AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
        TokenEndpoint = "https://oauth2.googleapis.com/token",
        // The broad drive scope, because CloudDrive presents the whole drive as a filesystem and
        // drive.file would only ever see files this application itself created — which is not a
        // drive mount, it is an empty folder.
        Scopes = "openid email https://www.googleapis.com/auth/drive",
        RegistrationUrl = "https://console.cloud.google.com/apis/credentials",
        ExtraAuthorizeParameters = new Dictionary<string, string>
        {
            // Google returns a refresh token only with access_type=offline, and only on the *first*
            // consent unless prompt=consent forces the screen again. Without the second parameter a
            // user who has authorised before gets an access token and no refresh token, and the mount
            // silently stops working an hour later.
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            // Ask Google to include any previously granted scopes, so re-authorising for one thing
            // does not quietly revoke another.
            ["include_granted_scopes"] = "true",
        },
        IdentityEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo",
        IdentityFields = ["email", "name"],
    };

    public static readonly IReadOnlyList<OAuthProviderConfig> All = [Microsoft, Google];

    /// <summary>The configuration for a provider, or null when it does not use OAuth.</summary>
    public static OAuthProviderConfig? For(ProviderId provider) =>
        All.FirstOrDefault(c => c.Provider == provider);

    public static OAuthProviderConfig Require(ProviderId provider) =>
        For(provider) ?? throw new ArgumentOutOfRangeException(
            nameof(provider), provider, "This provider does not use OAuth.");

    /// <summary>Bundled client id for a provider, or empty when this build has none.</summary>
    public static string BundledClientIdFor(ProviderId provider) => provider switch
    {
        ProviderId.OneDrive => BundledClientIds.Microsoft,
        ProviderId.GoogleDrive => BundledClientIds.Google,
        _ => string.Empty,
    };
}
