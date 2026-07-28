using CloudDrive.Core.Models;
using CloudDrive.Core.Platform;
using CloudDrive.Core.Stores;
using CloudDrive.Core.Tooling;

namespace CloudDrive.Tests;

public class MappingTests
{
    private static Account S3Account() => new()
    {
        Provider = ProviderId.Wasabi,
        Name = "Wasabi",
        RegionCode = "us-east-1",
    };

    [Fact]
    public void Remote_target_includes_the_bucket_for_s3()
    {
        var mapping = new Mapping { Container = "my-bucket", SubPath = "photos/2026" };
        Assert.Equal($"{mapping.RemoteName}:my-bucket/photos/2026",
            mapping.RemoteTargetFor(StorageProtocol.S3));
    }

    [Fact]
    public void Remote_target_omits_the_container_for_sftp()
    {
        // SFTP, FTP and WebDAV land in the account's own directory, so the container is not part of
        // the path the way an S3 bucket or an SMB share is.
        var mapping = new Mapping { Container = "ignored", SubPath = "backups" };
        Assert.Equal($"{mapping.RemoteName}:backups", mapping.RemoteTargetFor(StorageProtocol.Sftp));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("/a/b/", "a/b")]
    [InlineData(@"a\b", "a/b")]
    public void Sub_paths_are_normalised(string? input, string expected) =>
        Assert.Equal(expected, new Mapping { SubPath = input }.NormalizedSubPath);

    [Fact]
    public void Serviceable_requires_a_drive_mapping_hosted_by_the_service()
    {
        Assert.True(new Mapping { Mode = MappingMode.DriveLetter, Host = MountHost.Service }.IsServiceable);
        Assert.False(new Mapping { Mode = MappingMode.DriveLetter, Host = MountHost.UserSession }.IsServiceable);
        // A Cloud Files sync root lives in a user profile and calls back into that user's session;
        // there is no session-0 equivalent, so this can never be serviced.
        Assert.False(new Mapping { Mode = MappingMode.OnDemandFolder, Host = MountHost.Service }.IsServiceable);
    }

    [Fact]
    public void An_on_demand_mapping_hosted_by_the_service_fails_validation()
    {
        var mapping = new Mapping
        {
            Name = "Docs",
            Container = "bucket",
            Mode = MappingMode.OnDemandFolder,
            Host = MountHost.Service,
        };

        var problems = mapping.Validate(S3Account());
        Assert.Contains(problems, p => p.Contains("session-0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_missing_container_fails_validation_for_a_provider_that_needs_one()
    {
        var mapping = new Mapping { Name = "Docs", Mode = MappingMode.DriveLetter, DriveLetter = "H" };

        var problems = mapping.Validate(S3Account());
        Assert.Contains(problems, p => p.Contains("bucket", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_valid_drive_mapping_has_no_problems()
    {
        var mapping = new Mapping
        {
            Name = "Backups",
            Container = "my-bucket",
            Mode = MappingMode.DriveLetter,
            MountTarget = MountTarget.DriveLetter,
            DriveLetter = "H",
        };

        Assert.Empty(mapping.Validate(S3Account()));
    }

    [Fact]
    public void The_fingerprint_changes_when_something_requiring_a_remount_changes()
    {
        var account = S3Account();
        var mapping = new Mapping { Name = "Backups", Container = "bucket", DriveLetter = "H" };

        var before = mapping.MountFingerprint(account);

        // A rename does not require a remount.
        mapping.Name = "Renamed";
        Assert.Equal(before, mapping.MountFingerprint(account));

        // A different bucket does.
        mapping.Container = "other";
        Assert.NotEqual(before, mapping.MountFingerprint(account));
    }
}

public class AccountTests
{
    [Fact]
    public void Storage_box_hostname_is_derived_from_the_username()
    {
        var account = new Account { Provider = ProviderId.HetznerStorageBox, Username = "u123456" };
        Assert.Equal("u123456.your-storagebox.de", account.Host);
    }

    [Fact]
    public void A_sub_account_gets_its_own_hostname_and_share()
    {
        // Sub-accounts are not reduced to the parent: they have their own host and their own share.
        Assert.Equal("u123456-sub1.your-storagebox.de", StorageBox.HostFor("u123456-sub1"));
        Assert.Equal("u123456-sub1", StorageBox.ShareFor("u123456-sub1"));
        Assert.Equal("backup", StorageBox.ShareFor("u123456"));
    }

    [Fact]
    public void An_explicit_host_override_wins()
    {
        var account = new Account
        {
            Provider = ProviderId.HetznerStorageBox,
            Username = "u123456",
            HostOverride = "custom.example.com",
        };
        Assert.Equal("custom.example.com", account.Host);
    }

    [Fact]
    public void Effective_protocol_never_returns_auto()
    {
        var account = new Account { Provider = ProviderId.HetznerStorageBox, Protocol = StorageProtocol.Auto };
        Assert.NotEqual(StorageProtocol.Auto, account.EffectiveProtocol);
        Assert.Equal(StorageProtocol.Sftp, account.EffectiveProtocol);
    }

    [Fact]
    public void A_measured_winner_is_used_until_the_user_overrides_it()
    {
        var account = new Account
        {
            Provider = ProviderId.HetznerStorageBox,
            Protocol = StorageProtocol.Auto,
            ResolvedProtocol = StorageProtocol.Smb,
        };
        Assert.Equal(StorageProtocol.Smb, account.EffectiveProtocol);

        account.Protocol = StorageProtocol.WebDav;
        Assert.Equal(StorageProtocol.WebDav, account.EffectiveProtocol);
    }

    [Fact]
    public void A_single_protocol_provider_ignores_a_stale_choice()
    {
        var account = new Account { Provider = ProviderId.Wasabi, Protocol = StorageProtocol.Sftp };
        Assert.Equal(StorageProtocol.S3, account.EffectiveProtocol);
    }

    [Fact]
    public void Effective_port_falls_back_to_the_provider_default()
    {
        Assert.Equal(23, new Account { Provider = ProviderId.HetznerStorageBox }.EffectivePort);
        Assert.Equal(22, new Account { Provider = ProviderId.Sftp }.EffectivePort);
        Assert.Equal(2222, new Account { Provider = ProviderId.Sftp, Port = 2222 }.EffectivePort);
    }
}

public class CredentialsTests
{
    [Fact]
    public void Key_only_credentials_support_sftp_but_not_smb()
    {
        var creds = new Credentials { SshKeyFile = @"C:\id_ed25519" };

        Assert.True(creds.SupportsProtocol(StorageProtocol.Sftp));
        Assert.False(creds.SupportsProtocol(StorageProtocol.Smb));
        Assert.False(creds.SupportsProtocol(StorageProtocol.WebDav));
        Assert.False(creds.SupportsProtocol(StorageProtocol.Ftp));
    }

    [Fact]
    public void An_access_token_is_stale_within_its_refresh_margin()
    {
        var creds = new Credentials
        {
            AccessToken = "token",
            AccessTokenExpiresUtc = DateTime.UtcNow.AddMinutes(2),
        };

        // Refreshing slightly early is free; a token that expires mid-request fails the request.
        Assert.True(creds.HasFreshAccessToken(TimeSpan.FromMinutes(1)));
        Assert.False(creds.HasFreshAccessToken(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Completeness_follows_the_auth_kind()
    {
        Assert.True(new Credentials { AccessKeyId = "a", SecretAccessKey = "b" }.IsCompleteFor(AuthKind.KeyPair));
        Assert.False(new Credentials { AccessKeyId = "a" }.IsCompleteFor(AuthKind.KeyPair));
        Assert.True(new Credentials { RefreshToken = "r" }.IsCompleteFor(AuthKind.OAuth));
        Assert.True(new Credentials { Password = "p" }.IsCompleteFor(AuthKind.Password));
    }
}

public class IdleDetectorTests
{
    private static UpdateSettings Window(string? start, string? end) => new()
    {
        MaintenanceWindowStart = start,
        MaintenanceWindowEnd = end,
    };

    [Fact]
    public void No_window_configured_means_any_time_is_fine()
    {
        Assert.True(IdleDetector.InMaintenanceWindow(Window(null, null), DateTime.Now, out _));
    }

    [Theory]
    [InlineData("01:00", "05:00", 3, true)]
    [InlineData("01:00", "05:00", 6, false)]
    [InlineData("01:00", "05:00", 0, false)]
    public void A_same_day_window_is_honoured(string start, string end, int hour, bool expected)
    {
        var now = new DateTime(2026, 7, 28, hour, 0, 0, DateTimeKind.Local);
        Assert.Equal(expected, IdleDetector.InMaintenanceWindow(Window(start, end), now, out _));
    }

    [Theory]
    [InlineData(23, true)]  // after the start
    [InlineData(2, true)]   // after midnight, before the end
    [InlineData(12, false)] // the middle of the day
    public void A_window_crossing_midnight_is_handled(int hour, bool expected)
    {
        var now = new DateTime(2026, 7, 28, hour, 0, 0, DateTimeKind.Local);
        Assert.Equal(expected, IdleDetector.InMaintenanceWindow(Window("22:00", "04:00"), now, out _));
    }

    [Fact]
    public void A_mapping_marked_never_interrupt_blocks_an_update()
    {
        var verdict = IdleDetector.Evaluate(
            new UpdateSettings(), mountPoints: [], protectedMappings: ["Nightly backup"], DateTime.Now);

        Assert.False(verdict.IsIdle);
        Assert.Contains("Nightly backup", verdict.Reason);
    }

    [Fact]
    public void Nothing_mounted_and_nothing_protected_is_idle()
    {
        var verdict = IdleDetector.Evaluate(
            new UpdateSettings { IdleMinutesBeforeInstall = 10 },
            mountPoints: [], protectedMappings: [], DateTime.Now);

        // With no interactive session and no mounts there is nothing to wait for. A server with
        // nobody signed in must not be blocked from updating forever.
        if (!IdleDetector.HasInteractiveSession) Assert.True(verdict.IsIdle);
    }
}

public class ToolVersionTests
{
    [Theory]
    [InlineData("1.71.1", "1.71.0", true)]
    [InlineData("1.71.0", "1.71.1", false)]
    [InlineData("1.71.0", "1.71.0", false)]
    [InlineData("v1.72.0", "1.71.9", true)]
    [InlineData("2.0", "1.99.99", true)]
    // String comparison would order 1.10 before 1.9, which for an updater means silently never
    // installing a release once the minor version reaches double digits.
    [InlineData("1.10.0", "1.9.0", true)]
    [InlineData("1.9.0", "1.10.0", false)]
    [InlineData("2023.1.0-beta", "2022.5.0", true)]
    public void Versions_compare_numerically(string candidate, string installed, bool expected) =>
        Assert.Equal(expected, ToolManager.IsNewer(candidate, installed));

    [Fact]
    public void Every_managed_tool_has_a_vendor_source()
    {
        foreach (var tool in ToolCatalog.All)
        {
            Assert.Contains('/', tool.GitHubRepo);
            Assert.NotEmpty(tool.AssetNameContains);
            Assert.False(string.IsNullOrWhiteSpace(tool.Purpose));
        }
    }

    [Fact]
    public void An_installer_package_is_never_put_on_path()
    {
        // An MSI installs a driver; dropping it in a folder on PATH would do nothing useful.
        foreach (var tool in ToolCatalog.All.Where(t => t.PackageKind == ToolPackageKind.Installer))
            Assert.Null(tool.ExecutableName);
    }

    [Fact]
    public void The_update_interval_is_jittered_per_machine()
    {
        var interval = UpdateService.JitteredInterval(6);

        // A fleet cloned from one image would otherwise poll GitHub in lockstep, which looks like an
        // attack from GitHub's side and gets rate-limited from ours.
        Assert.True(interval >= TimeSpan.FromHours(6));
        Assert.True(interval < TimeSpan.FromHours(7));
    }
}

public class ConfigDocumentTests
{
    [Fact]
    public void Deleting_an_account_cascades_to_its_mappings()
    {
        var account = new Account { Name = "AWS" };
        var document = new ConfigDocument
        {
            Accounts = [account, new Account { Name = "Other" }],
            Mappings =
            [
                new Mapping { Name = "A", AccountId = account.Id },
                new Mapping { Name = "B", AccountId = account.Id },
                new Mapping { Name = "C", AccountId = Guid.NewGuid() },
            ],
        };

        var removed = document.RemoveAccountCascade(account.Id);

        Assert.Equal(2, removed.Count);
        Assert.Single(document.Accounts);
        Assert.Single(document.Mappings);
    }

    [Fact]
    public void An_orphaned_mapping_is_reported_rather_than_mounted()
    {
        var document = new ConfigDocument
        {
            Mappings = [new Mapping { Name = "Orphan", AccountId = Guid.NewGuid(), Host = MountHost.Service }],
        };

        Assert.Single(document.OrphanedMappings());
        // The service must keep the other mounts up rather than throwing over one bad row.
        Assert.Empty(document.ServiceableMappings());
    }

    [Fact]
    public void Two_mappings_on_one_mount_point_are_detected()
    {
        var existing = new Mapping { Name = "First", DriveLetter = "H", Mode = MappingMode.DriveLetter };
        var document = new ConfigDocument { Mappings = [existing] };

        var candidate = new Mapping { Name = "Second", DriveLetter = "H", Mode = MappingMode.DriveLetter };

        Assert.Equal(existing, document.FindMountPointConflict(candidate));
    }

    [Fact]
    public void A_mapping_does_not_conflict_with_itself()
    {
        var mapping = new Mapping { Name = "First", DriveLetter = "H", Mode = MappingMode.DriveLetter };
        var document = new ConfigDocument { Mappings = [mapping] };

        Assert.Null(document.FindMountPointConflict(mapping));
    }
}
