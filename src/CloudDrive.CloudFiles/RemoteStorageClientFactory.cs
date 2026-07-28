using System.Runtime.Versioning;
using CloudDrive.Core.Models;

namespace CloudDrive.CloudFiles;

/// <summary>
/// Builds the right <see cref="IRemoteStorageClient"/> for an account's protocol.
///
/// The switch is on the protocol, not the brand. Five S3 brands share one client because they share
/// one protocol; the provider catalogue supplies the endpoint and quirks. That is the property that
/// keeps adding a sixth S3 vendor a table entry rather than a new class.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RemoteStorageClientFactory
{
    public static IRemoteStorageClient Create(
        Mapping mapping, Account account, Credentials credentials, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(credentials);

        var protocol = account.EffectiveProtocol;

        if (!credentials.SupportsProtocol(protocol))
        {
            throw new InvalidOperationException(
                $"The stored credentials cannot authenticate over {protocol}. "
                + (protocol is StorageProtocol.Smb or StorageProtocol.WebDav or StorageProtocol.Ftp
                    ? "These protocols need a password, not an SSH key."
                    : "Check the account's credentials."));
        }

        return protocol switch
        {
            StorageProtocol.S3 => S3StorageClient.ForMapping(mapping, account, credentials),
            StorageProtocol.Sftp => SftpStorageClient.ForMapping(mapping, account, credentials, log),
            StorageProtocol.Smb => SmbStorageClient.ForMapping(mapping, account, credentials, log),
            StorageProtocol.WebDav => WebDavStorageClient.ForMapping(mapping, account, credentials, log),
            StorageProtocol.Ftp => FtpStorageClient.ForMapping(mapping, account, credentials, log),

            // Deliberately explicit rather than falling into the default. These are implemented in
            // phase 2, and a message naming the provider is far more use than "unsupported protocol".
            StorageProtocol.Graph or StorageProtocol.GoogleDrive => throw new NotSupportedException(
                $"Files On-Demand for {account.Descriptor.DisplayName} is not available yet. "
                + "Use a drive-letter mapping, which goes through rclone and does support it."),

            _ => throw new NotSupportedException($"Unsupported protocol '{protocol}'."),
        };
    }
}
