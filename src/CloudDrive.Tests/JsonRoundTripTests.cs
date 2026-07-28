using CloudDrive.Core.Models;
using CloudDrive.Core.Stores;

namespace CloudDrive.Tests;

/// <summary>
/// Round-trips the persisted and wire formats through real serialisation.
///
/// These exist because of a failure that no other test could have caught: every enum in CloudDrive is
/// written as a *name* rather than a number, and reading one back goes through a code path in
/// System.Text.Json that needs System.Text.RegularExpressions. Nothing else in the suite parsed an
/// enum out of JSON, so a service that could not load that assembly still passed 180 tests and then
/// dropped every IPC connection at runtime.
/// </summary>
public class JsonRoundTripTests
{
    /// <summary>
    /// The dependency, asserted directly. If this fails, the environment is missing part of the
    /// framework and the enum-parsing failures below are a symptom rather than the cause.
    /// </summary>
    [Fact]
    public void Regular_expressions_are_available() =>
        Assert.Matches("^cloud", "clouddrive");

    [Fact]
    public void A_mapping_survives_a_round_trip_through_the_file_store()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clouddrive-rt-{Guid.NewGuid():N}.json");
        var store = new JsonFileStore<ConfigDocument>(path, machineScope: false);

        var account = new Account
        {
            Name = "Wasabi",
            Provider = ProviderId.Wasabi,
            RegionCode = "eu-central-1",
            Protocol = StorageProtocol.Auto,
        };
        var mapping = new Mapping
        {
            Name = "Backups",
            AccountId = account.Id,
            Container = "my-bucket",
            Mode = MappingMode.DriveLetter,
            MountTarget = MountTarget.Directory,
            Host = MountHost.Service,
            Cache = new CacheSettings { CacheMode = VfsCacheMode.Writes },
        };

        try
        {
            store.Save(new ConfigDocument { Accounts = [account], Mappings = [mapping] });

            // Enums must be on disk as names: these files are meant to be readable and hand-editable,
            // and a renumbered enum must never silently repoint an account at a different provider.
            var json = File.ReadAllText(path);
            Assert.Contains("\"Wasabi\"", json);
            Assert.Contains("\"DriveLetter\"", json);
            Assert.Contains("\"Writes\"", json);

            // And parsing them back has to work, which is the half that was broken.
            var loaded = store.Load();

            Assert.Equal(ProviderId.Wasabi, loaded.Accounts[0].Provider);
            Assert.Equal(StorageProtocol.Auto, loaded.Accounts[0].Protocol);
            Assert.Equal(MappingMode.DriveLetter, loaded.Mappings[0].Mode);
            Assert.Equal(MountTarget.Directory, loaded.Mappings[0].MountTarget);
            Assert.Equal(MountHost.Service, loaded.Mappings[0].Host);
            Assert.Equal(VfsCacheMode.Writes, loaded.Mappings[0].Cache.CacheMode);
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Settings_with_every_enum_kind_survive_a_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clouddrive-rt-{Guid.NewGuid():N}.json");
        var store = new JsonFileStore<AppSettings>(path, machineScope: false);

        var settings = new AppSettings();
        settings.Notifications.Targets.Add(new NotificationTarget
        {
            Name = "Ops",
            Kind = NotificationChannelKind.Telegram,
            MinimumSeverity = AlertSeverity.Error,
            EventFilter = [AlertKind.MountFailed, AlertKind.ReauthRequired],
        });

        try
        {
            store.Save(settings);
            var loaded = store.Load();

            var target = Assert.Single(loaded.Notifications.Targets);
            Assert.Equal(NotificationChannelKind.Telegram, target.Kind);
            Assert.Equal(AlertSeverity.Error, target.MinimumSeverity);
            // A list of enums is a separate converter path from a scalar one.
            Assert.Equal([AlertKind.MountFailed, AlertKind.ReauthRequired], target.EventFilter);
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp */ }
        }
    }
}
