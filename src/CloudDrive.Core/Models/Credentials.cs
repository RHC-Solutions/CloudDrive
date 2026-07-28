namespace CloudDrive.Core.Models;

/// <summary>
/// The secrets for one <see cref="Account"/>. Which fields matter is decided by the provider's
/// <see cref="AuthKind"/>, which is why this stays one type rather than one per brand — every field
/// here is shared by at least two providers.
///
/// Never written to <c>accounts.json</c>; see <c>CredentialStore</c> for how it is protected at rest.
/// </summary>
public sealed class Credentials
{
    // --- Password / key ------------------------------------------------------------------------

    /// <summary>Account password. Used by SMB, FTP, WebDAV and password-authenticated SFTP.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Path to a private key used instead of the password. SSH-based back ends only.</summary>
    public string? SshKeyFile { get; set; }

    /// <summary>Passphrase protecting <see cref="SshKeyFile"/>, if it has one.</summary>
    public string? SshKeyPassphrase { get; set; }

    // --- Key pair ------------------------------------------------------------------------------

    /// <summary>S3 access key id. For Backblaze this is the application key's <c>keyID</c>.</summary>
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>S3 secret key. For Backblaze this is the <c>applicationKey</c>.</summary>
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>Session token, for temporary STS credentials. Optional.</summary>
    public string? SessionToken { get; set; }

    // --- OAuth ---------------------------------------------------------------------------------

    /// <summary>
    /// The long-lived refresh token from the interactive sign-in. This is the actual credential for
    /// an OAuth account: the service exchanges it for short-lived access tokens and never needs a
    /// browser, which is what lets OneDrive and Google Drive mounts come up before anyone signs in.
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>The most recent access token, cached so a restart does not force an extra exchange.</summary>
    public string? AccessToken { get; set; }

    /// <summary>When <see cref="AccessToken"/> stops being valid.</summary>
    public DateTime? AccessTokenExpiresUtc { get; set; }

    /// <summary>
    /// Client secret for a confidential OAuth client. Empty for CloudDrive's own registrations,
    /// which are public clients using PKCE — an installed app cannot keep a secret, so it does not
    /// pretend to have one. Present only when a user brings a confidential registration of their own.
    /// </summary>
    public string? OAuthClientSecret { get; set; }

    // --- Predicates ------------------------------------------------------------------------------

    /// <summary>True when SFTP can authenticate: a key is enough, otherwise a password is needed.</summary>
    public bool HasSshAuth =>
        !string.IsNullOrWhiteSpace(Password) || !string.IsNullOrWhiteSpace(SshKeyFile);

    public bool HasKeyPair =>
        !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(SecretAccessKey);

    public bool HasOAuth => !string.IsNullOrWhiteSpace(RefreshToken);

    /// <summary>True when there is enough here to authenticate under <paramref name="auth"/>.</summary>
    public bool IsCompleteFor(AuthKind auth) => auth switch
    {
        AuthKind.KeyPair => HasKeyPair,
        AuthKind.OAuth => HasOAuth,
        AuthKind.PasswordOrKey => HasSshAuth,
        _ => !string.IsNullOrWhiteSpace(Password),
    };

    /// <summary>
    /// True when these credentials work over <paramref name="protocol"/> specifically.
    ///
    /// The distinction matters for a Storage Box: an SSH key authenticates SFTP but SMB and WebDAV
    /// are password-only, so a key-only account silently restricts itself to one protocol. Saying so
    /// up front beats letting Auto benchmark a protocol it can never log into.
    /// </summary>
    public bool SupportsProtocol(StorageProtocol protocol) => protocol switch
    {
        StorageProtocol.S3 => HasKeyPair,
        StorageProtocol.Graph or StorageProtocol.GoogleDrive => HasOAuth,
        StorageProtocol.Sftp => HasSshAuth,
        StorageProtocol.Smb or StorageProtocol.WebDav or StorageProtocol.Ftp =>
            !string.IsNullOrWhiteSpace(Password),
        _ => true,
    };

    /// <summary>
    /// An access token that is still good for <paramref name="margin"/>. The margin exists because a
    /// token that expires mid-request fails the request; refreshing slightly early is free.
    /// </summary>
    public bool HasFreshAccessToken(TimeSpan margin) =>
        !string.IsNullOrWhiteSpace(AccessToken)
        && AccessTokenExpiresUtc is { } expiry
        && expiry - margin > DateTime.UtcNow;

    public Credentials Clone() => (Credentials)MemberwiseClone();
}
