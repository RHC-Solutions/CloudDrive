using CloudDrive.Core.Models;
using CloudDrive.Core.Providers;

namespace CloudDrive.Core.Mounting;

/// <summary>
/// Builds the rclone remote definition for one mapping, as environment variables.
///
/// Nothing is written to an <c>rclone.conf</c> on disk. rclone lets any config key be overridden by
/// <c>RCLONE_CONFIG_&lt;REMOTE&gt;_&lt;KEY&gt;</c> in the environment, and injecting the remote that
/// way keeps every secret out of a file and off the command line, where it would show up in the
/// process list for any user on the machine to read.
///
/// One builder covers twelve providers because the shape is per *protocol*, and the per-*brand*
/// differences (which endpoint, which rclone dialect, which quirks) come from
/// <see cref="ProviderCatalog"/> rather than from a branch here.
/// </summary>
public static class RcloneConfig
{
    /// <summary>Environment variables defining <paramref name="mapping"/>'s remote.</summary>
    public static IReadOnlyDictionary<string, string> Build(
        Mapping mapping, Account account, Credentials credentials) =>
        Build(mapping, account, credentials, account.EffectiveProtocol);

    /// <summary>
    /// As <see cref="Build(Mapping, Account, Credentials)"/>, but over an explicit protocol. The
    /// protocol is a parameter so the Auto benchmark can build a config for each candidate without
    /// mutating the account.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(
        Mapping mapping, Account account, Credentials credentials, StorageProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(credentials);

        if (protocol == StorageProtocol.Auto)
            throw new ArgumentException("Auto must be resolved to a concrete protocol first.", nameof(protocol));

        var descriptor = account.Descriptor;
        if (!descriptor.Protocols.Contains(protocol))
            throw new InvalidOperationException(
                $"{descriptor.DisplayName} cannot be reached over {protocol}.");
        if (!credentials.SupportsProtocol(protocol))
            throw new InvalidOperationException(
                $"The stored credentials cannot authenticate over {protocol}. "
                + (protocol is StorageProtocol.Smb or StorageProtocol.WebDav or StorageProtocol.Ftp
                    ? "These protocols need a password; an SSH key cannot be used for them."
                    : "Check the account's credentials."));

        var config = protocol switch
        {
            StorageProtocol.S3 => BuildS3(account, credentials),
            StorageProtocol.Sftp => BuildSftp(account, credentials),
            StorageProtocol.Smb => BuildSmb(account, credentials),
            StorageProtocol.WebDav => BuildWebDav(account, credentials),
            StorageProtocol.Ftp => BuildFtp(account, credentials),
            StorageProtocol.Graph => BuildOneDrive(account, credentials),
            StorageProtocol.GoogleDrive => BuildGoogleDrive(account, credentials),
            _ => throw new NotSupportedException($"Unsupported protocol '{protocol}'."),
        };

        // rclone uppercases the whole variable name, so the remote name is uppercased to match.
        var prefix = "RCLONE_CONFIG_" + mapping.RemoteName.ToUpperInvariant() + "_";
        return config.ToDictionary(kv => prefix + kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    // ---------------------------------------------------------------- S3 ----------------------

    private static Dictionary<string, string> BuildS3(Account account, Credentials creds)
    {
        if (!creds.HasKeyPair)
            throw new InvalidOperationException("This account needs an access key and a secret key.");

        var descriptor = account.Descriptor;
        var endpoint = ResolveS3Endpoint(account, descriptor);

        var config = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TYPE"] = "s3",
            // Naming the vendor rather than leaving rclone on "Other" is what selects its per-dialect
            // quirk set: signature version, path vs virtual-host addressing, and which ACL and
            // storage-class headers to omit because the endpoint rejects them.
            ["PROVIDER"] = descriptor.RcloneS3Provider ?? "Other",
            ["ENV_AUTH"] = "false",
            ["ACCESS_KEY_ID"] = creds.AccessKeyId.Trim(),
            ["SECRET_ACCESS_KEY"] = creds.SecretAccessKey.Trim(),
            ["ENDPOINT"] = endpoint,
            // S3 has no real directories. Without markers, a folder the user creates on the drive
            // exists only in rclone's memory: invisible to every other tool and gone on remount.
            ["DIRECTORY_MARKERS"] = "true",
        };

        if (!string.IsNullOrWhiteSpace(creds.SessionToken))
            config["SESSION_TOKEN"] = creds.SessionToken!.Trim();

        var region = RegionFor(account, descriptor);
        if (!string.IsNullOrWhiteSpace(region)) config["REGION"] = region;

        return config;
    }

    /// <summary>
    /// The S3 endpoint host. A region-listed provider derives it from the region; a generic one
    /// takes whatever the user typed, tolerating a full URL since that is what every vendor's
    /// documentation shows.
    /// </summary>
    private static string ResolveS3Endpoint(Account account, ProviderDescriptor descriptor)
    {
        if (descriptor.Has(ProviderCapabilities.Regions) && string.IsNullOrWhiteSpace(account.HostOverride))
        {
            var region = ProviderCatalog.FindRegion(account.Provider, account.RegionCode)
                ?? throw new InvalidOperationException(
                    $"'{account.RegionCode}' is not a known {descriptor.DisplayName} region.");
            return region.Endpoint;
        }

        var host = (account.HostOverride ?? string.Empty).Trim();
        if (host.Length == 0)
            throw new InvalidOperationException("This account needs an endpoint.");

        // rclone accepts a bare host or a full URL; normalise so the scheme reflects UseTls rather
        // than whatever happened to be pasted in.
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return uri.Authority;
        return host;
    }

    private static string? RegionFor(Account account, ProviderDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(account.RegionCode)) return account.RegionCode!.Trim();
        // Some S3 implementations reject a signature with an empty region; "us-east-1" is the
        // conventional stand-in and what every SDK defaults to.
        return descriptor.Id == ProviderId.GenericS3 ? "us-east-1" : descriptor.DefaultRegion;
    }

    // ---------------------------------------------------------------- SFTP --------------------

    private static Dictionary<string, string> BuildSftp(Account account, Credentials creds)
    {
        RequireHost(account);

        var isStorageBox = account.Provider == ProviderId.HetznerStorageBox;
        var user = isStorageBox ? StorageBox.UserFor(account.Username) : account.Username.Trim();

        var config = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TYPE"] = "sftp",
            ["HOST"] = account.Host,
            ["USER"] = user,
            ["PORT"] = account.EffectivePort.ToString(),
        };

        if (isStorageBox)
        {
            // A Storage Box runs a restricted but real shell on port 23, so rclone can call
            // md5sum/sha1sum for checksums. Declaring the shell type up front skips rclone's probe,
            // which otherwise costs an extra SSH session out of a budget of about ten on every mount.
            config["SHELL_TYPE"] = "unix";
            config["MD5SUM_COMMAND"] = "md5sum";
            config["SHA1SUM_COMMAND"] = "sha1sum";
        }

        // A key beats a password when both are present: it survives password rotation and is what
        // every vendor recommends for unattended access.
        if (!string.IsNullOrWhiteSpace(creds.SshKeyFile))
        {
            config["KEY_FILE"] = creds.SshKeyFile!.Trim();
            if (!string.IsNullOrWhiteSpace(creds.SshKeyPassphrase))
                config["KEY_FILE_PASS"] = RcloneObscure.Obscure(creds.SshKeyPassphrase!);
        }
        else
        {
            config["PASS"] = RcloneObscure.Obscure(creds.Password);
        }

        return config;
    }

    // ---------------------------------------------------------------- SMB ---------------------

    private static Dictionary<string, string> BuildSmb(Account account, Credentials creds)
    {
        RequireHost(account);
        RequirePassword(creds, "SMB");

        var isStorageBox = account.Provider == ProviderId.HetznerStorageBox;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TYPE"] = "smb",
            ["HOST"] = account.Host,
            ["USER"] = isStorageBox ? StorageBox.UserFor(account.Username) : account.Username.Trim(),
            ["PASS"] = RcloneObscure.Obscure(creds.Password),
            ["PORT"] = account.EffectivePort.ToString(),
            // Samba and a Storage Box are not domain-joined; WORKGROUP is what they expect.
            ["DOMAIN"] = string.IsNullOrWhiteSpace(account.Domain) ? "WORKGROUP" : account.Domain!.Trim(),
        };
    }

    // ---------------------------------------------------------------- WebDAV ------------------

    private static Dictionary<string, string> BuildWebDav(Account account, Credentials creds)
    {
        RequireHost(account);
        RequirePassword(creds, "WebDAV");

        var scheme = account.UseTls ? "https" : "http";
        var host = account.Host;
        var url = host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                  || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? host
            : $"{scheme}://{host}";

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TYPE"] = "webdav",
            ["URL"] = url,
            // "other" rather than a named vendor: picking Nextcloud or ownCloud makes rclone use
            // chunked-upload and hash endpoints that a plain DAV server does not implement.
            ["VENDOR"] = account.Options.GetValueOrDefault("webdav_vendor", "other"),
            ["USER"] = account.Username.Trim(),
            ["PASS"] = RcloneObscure.Obscure(creds.Password),
        };
    }

    // ---------------------------------------------------------------- FTP ---------------------

    private static Dictionary<string, string> BuildFtp(Account account, Credentials creds)
    {
        RequireHost(account);
        RequirePassword(creds, "FTP");

        var config = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TYPE"] = "ftp",
            ["HOST"] = account.Host,
            ["USER"] = account.Username.Trim(),
            ["PASS"] = RcloneObscure.Obscure(creds.Password),
            ["PORT"] = account.EffectivePort.ToString(),
            // Concurrency comes from a connection pool, since FTP has no multiplexing: one command
            // channel carries one transfer at a time.
            ["CONCURRENCY"] = "8",
        };

        if (account.UseTls)
        {
            // Explicit FTPS (AUTH TLS on the control port) rather than implicit. Implicit FTPS is
            // deprecated and conventionally lives on port 990; a user who needs it sets the port and
            // the option below.
            var implicitTls = account.Options.GetValueOrDefault("ftp_implicit_tls") == "true";
            if (implicitTls) config["TLS"] = "true";
            else config["EXPLICIT_TLS"] = "true";
        }

        return config;
    }

    // ---------------------------------------------------------------- OAuth back ends ---------
    //
    // Both take a token as a JSON blob in the config rather than a password. CloudDrive owns the
    // refresh cycle (see OAuthTokenService) instead of letting rclone do it, because rclone would
    // write the rotated token back into a config file that does not exist here — the token has to
    // land in the encrypted store where the service and the on-demand engine both find it.

    private static Dictionary<string, string> BuildOneDrive(Account account, Credentials creds)
    {
        RequireOAuth(creds, "OneDrive");

        var config = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TYPE"] = "onedrive",
            ["TOKEN"] = OAuthTokenJson(creds),
            ["DRIVE_TYPE"] = account.Options.GetValueOrDefault("drive_type", "personal"),
        };

        if (!string.IsNullOrWhiteSpace(account.DriveId)) config["DRIVE_ID"] = account.DriveId!.Trim();
        if (!string.IsNullOrWhiteSpace(account.OAuthClientIdOverride))
            config["CLIENT_ID"] = account.OAuthClientIdOverride!.Trim();
        if (!string.IsNullOrWhiteSpace(creds.OAuthClientSecret))
            config["CLIENT_SECRET"] = creds.OAuthClientSecret!.Trim();
        if (!string.IsNullOrWhiteSpace(account.TenantId)) config["TENANT"] = account.TenantId!.Trim();

        return config;
    }

    private static Dictionary<string, string> BuildGoogleDrive(Account account, Credentials creds)
    {
        RequireOAuth(creds, "Google Drive");

        var config = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TYPE"] = "drive",
            ["TOKEN"] = OAuthTokenJson(creds),
            // Without this, a Google Doc downloads as a zero-byte file and every tool that reads the
            // mount sees a corrupt document. Exporting to Office formats is the least surprising
            // behaviour for a drive that Explorer will hand to Word and Excel.
            ["EXPORT_FORMATS"] = account.Options.GetValueOrDefault("export_formats", "docx,xlsx,pptx,svg"),
            // Files in the trash still appear in listings otherwise, which makes a deleted file look
            // like it came back.
            ["SKIP_GDOCS"] = "false",
            ["TRASHED_ONLY"] = "false",
        };

        if (!string.IsNullOrWhiteSpace(account.DriveId))
        {
            config["TEAM_DRIVE"] = account.DriveId!.Trim();
        }
        if (!string.IsNullOrWhiteSpace(account.OAuthClientIdOverride))
            config["CLIENT_ID"] = account.OAuthClientIdOverride!.Trim();
        if (!string.IsNullOrWhiteSpace(creds.OAuthClientSecret))
            config["CLIENT_SECRET"] = creds.OAuthClientSecret!.Trim();

        return config;
    }

    /// <summary>
    /// The token blob rclone's oauth backends expect. It is plain JSON in the config value; the
    /// protection comes from it never touching disk unencrypted and never appearing on a command
    /// line, the same as every password here.
    /// </summary>
    private static string OAuthTokenJson(Credentials creds)
    {
        var expiry = creds.AccessTokenExpiresUtc ?? DateTime.UtcNow;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            access_token = creds.AccessToken ?? string.Empty,
            token_type = "Bearer",
            refresh_token = creds.RefreshToken ?? string.Empty,
            expiry = expiry.ToUniversalTime().ToString("o"),
        });
    }

    // ---------------------------------------------------------------- Guards ------------------

    private static void RequireHost(Account account)
    {
        if (string.IsNullOrWhiteSpace(account.Host))
            throw new InvalidOperationException(
                account.Provider == ProviderId.HetznerStorageBox
                    ? "The Storage Box username is required; the hostname is derived from it."
                    : "This account needs a server hostname.");
    }

    private static void RequirePassword(Credentials creds, string protocolName)
    {
        if (string.IsNullOrWhiteSpace(creds.Password))
            throw new InvalidOperationException(
                $"{protocolName} authenticates with a password; an SSH key cannot be used for it.");
    }

    private static void RequireOAuth(Credentials creds, string providerName)
    {
        if (!creds.HasOAuth)
            throw new InvalidOperationException(
                $"This {providerName} account has not been signed in yet. Open CloudDrive and authorise it.");
    }
}
