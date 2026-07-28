using CloudDrive.Core.Models;
using CloudDrive.Core.Mounting;

namespace CloudDrive.Tests;

/// <summary>
/// The remote definition handed to rclone. These assert the things that silently break a mount:
/// a password sent in plaintext (rclone reads it as ciphertext and fails with a base64 error), a
/// missing directory-markers flag (empty folders vanish on remount), a Storage Box username sent as
/// the full hostname.
/// </summary>
public class RcloneConfigTests
{
    private static Mapping Mapping(string container = "bucket") => new()
    {
        Name = "Test",
        Container = container,
    };

    private static string Value(IReadOnlyDictionary<string, string> config, Mapping mapping, string key) =>
        config[$"RCLONE_CONFIG_{mapping.RemoteName.ToUpperInvariant()}_{key}"];

    // ---------------------------------------------------------------- S3 ----------------------

    [Theory]
    [InlineData(ProviderId.Wasabi, "us-east-1", "Wasabi", "s3.us-east-1.wasabisys.com")]
    [InlineData(ProviderId.AwsS3, "eu-west-1", "AWS", "s3.eu-west-1.amazonaws.com")]
    [InlineData(ProviderId.BackblazeB2, "us-west-004", "Other", "s3.us-west-004.backblazeb2.com")]
    [InlineData(ProviderId.HetznerObjectStorage, "fsn1", "Hetzner", "fsn1.your-objectstorage.com")]
    public void S3_brands_resolve_their_dialect_and_endpoint(
        ProviderId provider, string region, string expectedDialect, string expectedEndpoint)
    {
        var account = new Account { Provider = provider, Name = "acct", RegionCode = region };
        var creds = new Credentials { AccessKeyId = "AK", SecretAccessKey = "SK" };
        var mapping = Mapping();

        var config = RcloneConfig.Build(mapping, account, creds, StorageProtocol.S3);

        Assert.Equal("s3", Value(config, mapping, "TYPE"));
        Assert.Equal(expectedDialect, Value(config, mapping, "PROVIDER"));
        Assert.Equal(expectedEndpoint, Value(config, mapping, "ENDPOINT"));
        Assert.Equal(region, Value(config, mapping, "REGION"));
        // Without markers an empty folder lives only in rclone's memory: invisible to other tools
        // and gone on remount.
        Assert.Equal("true", Value(config, mapping, "DIRECTORY_MARKERS"));
    }

    [Fact]
    public void Generic_s3_accepts_a_pasted_url_and_reduces_it_to_a_host()
    {
        var account = new Account
        {
            Provider = ProviderId.GenericS3,
            Name = "minio",
            HostOverride = "https://s3.example.com",
        };
        var creds = new Credentials { AccessKeyId = "AK", SecretAccessKey = "SK" };
        var mapping = Mapping();

        var config = RcloneConfig.Build(mapping, account, creds, StorageProtocol.S3);

        Assert.Equal("s3.example.com", Value(config, mapping, "ENDPOINT"));
    }

    [Fact]
    public void S3_without_a_key_pair_is_refused()
    {
        var account = new Account { Provider = ProviderId.Wasabi, RegionCode = "us-east-1" };

        Assert.Throws<InvalidOperationException>(
            () => RcloneConfig.Build(Mapping(), account, new Credentials(), StorageProtocol.S3));
    }

    // ---------------------------------------------------------------- SFTP --------------------

    [Fact]
    public void Storage_box_username_is_reduced_to_the_bare_account()
    {
        // The hostname is derived from the username; sending the full host as the login fails.
        var account = new Account
        {
            Provider = ProviderId.HetznerStorageBox,
            Username = "u123456.your-storagebox.de",
        };
        var creds = new Credentials { Password = "hunter2" };
        var mapping = Mapping();

        var config = RcloneConfig.Build(mapping, account, creds, StorageProtocol.Sftp);

        Assert.Equal("u123456", Value(config, mapping, "USER"));
        Assert.Equal("u123456.your-storagebox.de", Value(config, mapping, "HOST"));
        Assert.Equal("23", Value(config, mapping, "PORT"));
    }

    [Fact]
    public void Passwords_are_obscured_never_plaintext()
    {
        var account = new Account { Provider = ProviderId.Sftp, Username = "user", HostOverride = "host" };
        var creds = new Credentials { Password = "hunter2" };
        var mapping = Mapping();

        var config = RcloneConfig.Build(mapping, account, creds, StorageProtocol.Sftp);
        var stored = Value(config, mapping, "PASS");

        // rclone runs every pass value through obscure.Reveal, so a plaintext password is read as
        // ciphertext and authentication fails with a confusing base64 error.
        Assert.NotEqual("hunter2", stored);
        Assert.Equal("hunter2", RcloneObscure.Reveal(stored));
    }

    [Fact]
    public void An_ssh_key_is_used_in_preference_to_a_password()
    {
        var account = new Account { Provider = ProviderId.Sftp, Username = "user", HostOverride = "host" };
        var creds = new Credentials { Password = "hunter2", SshKeyFile = @"C:\keys\id_ed25519" };
        var mapping = Mapping();

        var config = RcloneConfig.Build(mapping, account, creds, StorageProtocol.Sftp);

        Assert.Equal(@"C:\keys\id_ed25519", Value(config, mapping, "KEY_FILE"));
        Assert.False(config.ContainsKey($"RCLONE_CONFIG_{mapping.RemoteName.ToUpperInvariant()}_PASS"));
    }

    [Fact]
    public void A_key_only_account_cannot_use_a_password_only_protocol()
    {
        var account = new Account
        {
            Provider = ProviderId.HetznerStorageBox,
            Username = "u123456",
        };
        var creds = new Credentials { SshKeyFile = @"C:\keys\id_ed25519" };

        // SMB and WebDAV are password-only. Saying so up front beats letting Auto benchmark a
        // protocol it can never log in to.
        var ex = Assert.Throws<InvalidOperationException>(
            () => RcloneConfig.Build(Mapping(), account, creds, StorageProtocol.Smb));
        Assert.Contains("password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- Other protocols ---------

    [Fact]
    public void Ftps_uses_explicit_tls_by_default()
    {
        var account = new Account
        {
            Provider = ProviderId.Ftp, Username = "user", HostOverride = "ftp.example.com", UseTls = true,
        };
        var mapping = Mapping();

        var config = RcloneConfig.Build(mapping, account, new Credentials { Password = "p" }, StorageProtocol.Ftp);

        // Implicit FTPS is deprecated; explicit AUTH TLS on the control port is the modern default.
        Assert.Equal("true", Value(config, mapping, "EXPLICIT_TLS"));
        Assert.False(config.ContainsKey($"RCLONE_CONFIG_{mapping.RemoteName.ToUpperInvariant()}_TLS"));
    }

    [Fact]
    public void Webdav_defaults_to_the_plain_vendor()
    {
        var account = new Account
        {
            Provider = ProviderId.WebDav, Username = "user", HostOverride = "dav.example.com",
        };
        var mapping = Mapping();

        var config = RcloneConfig.Build(mapping, account, new Credentials { Password = "p" }, StorageProtocol.WebDav);

        // Naming Nextcloud or ownCloud makes rclone use chunked-upload and hash endpoints a plain
        // DAV server does not implement.
        Assert.Equal("other", Value(config, mapping, "VENDOR"));
        Assert.Equal("https://dav.example.com", Value(config, mapping, "URL"));
    }

    [Fact]
    public void Smb_falls_back_to_workgroup_when_no_domain_is_set()
    {
        var account = new Account { Provider = ProviderId.Smb, Username = "user", HostOverride = "nas" };
        var mapping = Mapping("share");

        var config = RcloneConfig.Build(mapping, account, new Credentials { Password = "p" }, StorageProtocol.Smb);

        Assert.Equal("WORKGROUP", Value(config, mapping, "DOMAIN"));
    }

    // ---------------------------------------------------------------- Guards ------------------

    [Fact]
    public void Auto_must_be_resolved_before_building_a_config()
    {
        var account = new Account { Provider = ProviderId.HetznerStorageBox, Username = "u1" };

        Assert.Throws<ArgumentException>(
            () => RcloneConfig.Build(Mapping(), account, new Credentials { Password = "p" }, StorageProtocol.Auto));
    }

    [Fact]
    public void A_protocol_the_provider_does_not_speak_is_refused()
    {
        var account = new Account { Provider = ProviderId.Wasabi, RegionCode = "us-east-1" };
        var creds = new Credentials { AccessKeyId = "AK", SecretAccessKey = "SK" };

        Assert.Throws<InvalidOperationException>(
            () => RcloneConfig.Build(Mapping(), account, creds, StorageProtocol.Sftp));
    }

    [Fact]
    public void Every_variable_is_scoped_to_this_mappings_remote()
    {
        // Two mappings must never share config, or editing one would silently repoint the other.
        var account = new Account { Provider = ProviderId.Wasabi, RegionCode = "us-east-1" };
        var creds = new Credentials { AccessKeyId = "AK", SecretAccessKey = "SK" };
        var mapping = Mapping();

        var config = RcloneConfig.Build(mapping, account, creds, StorageProtocol.S3);
        var prefix = $"RCLONE_CONFIG_{mapping.RemoteName.ToUpperInvariant()}_";

        Assert.All(config.Keys, key => Assert.StartsWith(prefix, key, StringComparison.Ordinal));
    }
}

/// <summary>
/// rclone's obscure transform, verified as a round trip. It is reimplemented in-process rather than
/// shelled out to so the password never appears in a command line.
/// </summary>
public class RcloneObscureTests
{
    [Theory]
    [InlineData("hunter2")]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("with spaces and symbols !@#$%^&*()")]
    [InlineData("пароль-סיסמה-密码")]
    public void Round_trips(string plaintext) =>
        Assert.Equal(plaintext, RcloneObscure.Reveal(RcloneObscure.Obscure(plaintext)));

    [Fact]
    public void Uses_a_fresh_iv_each_time()
    {
        // A fixed IV would make identical passwords produce identical ciphertext, which leaks that
        // two accounts share a password to anyone who reads the process environment.
        var a = RcloneObscure.Obscure("hunter2");
        var b = RcloneObscure.Obscure("hunter2");

        Assert.NotEqual(a, b);
        Assert.Equal(RcloneObscure.Reveal(a), RcloneObscure.Reveal(b));
    }

    [Fact]
    public void Rejects_a_value_too_short_to_hold_an_iv() =>
        Assert.Throws<FormatException>(() => RcloneObscure.Reveal("AAAA"));
}
