using CloudDrive.Core.Models;
using CloudDrive.Core.Providers;

namespace CloudDrive.Tests;

/// <summary>
/// The provider catalogue is data, so it is tested as data.
///
/// These are the tests that catch the class of mistake a compiler cannot: a region table that is
/// empty at runtime because of static initialisation order, an S3 brand with no rclone dialect, a
/// provider whose fallback protocol is not one it actually speaks.
/// </summary>
public class ProviderCatalogTests
{
    public static TheoryData<ProviderId> AllProviders()
    {
        var data = new TheoryData<ProviderId>();
        foreach (var id in Enum.GetValues<ProviderId>()) data.Add(id);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public void Every_provider_has_a_descriptor(ProviderId id)
    {
        var descriptor = ProviderCatalog.Get(id);
        Assert.Equal(id, descriptor.Id);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Description));
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public void Every_provider_speaks_at_least_one_protocol(ProviderId id)
    {
        var descriptor = ProviderCatalog.Get(id);
        Assert.NotEmpty(descriptor.Protocols);
        Assert.DoesNotContain(StorageProtocol.Auto, descriptor.Protocols);
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public void Fallback_protocol_is_one_the_provider_speaks(ProviderId id)
    {
        var descriptor = ProviderCatalog.Get(id);
        Assert.Contains(descriptor.FallbackProtocol, descriptor.Protocols);
    }

    /// <summary>
    /// The regression test for the static-initialisation bug this catalogue was originally written
    /// with: the region tables were declared below <c>All</c>, so they were null when the
    /// descriptors captured them and every region dropdown would have shipped empty.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProviders))]
    public void Region_capability_and_region_table_agree(ProviderId id)
    {
        var descriptor = ProviderCatalog.Get(id);

        if (descriptor.Has(ProviderCapabilities.Regions))
        {
            Assert.NotEmpty(descriptor.Regions);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.DefaultRegion),
                $"{id} offers a region list but names no default.");
            Assert.NotNull(ProviderCatalog.FindRegion(id, descriptor.DefaultRegion));
        }
        else
        {
            Assert.Empty(descriptor.Regions);
        }
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public void Region_entries_are_well_formed(ProviderId id)
    {
        foreach (var region in ProviderCatalog.Get(id).Regions)
        {
            Assert.False(string.IsNullOrWhiteSpace(region.Code));
            Assert.False(string.IsNullOrWhiteSpace(region.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(region.Endpoint));
            // An endpoint is a bare host: the scheme is decided by the client from UseTls, and a
            // pasted URL here would produce "https://https://…".
            Assert.DoesNotContain("://", region.Endpoint);
        }
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public void Region_codes_are_unique_within_a_provider(ProviderId id)
    {
        var regions = ProviderCatalog.Get(id).Regions;
        var distinct = regions.Select(r => r.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(regions.Count, distinct);
    }

    [Fact]
    public void Every_s3_brand_names_an_rclone_dialect()
    {
        foreach (var descriptor in ProviderCatalog.All.Where(p => p.IsS3))
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.RcloneS3Provider),
                $"{descriptor.Id} is an S3 dialect but names no rclone provider, so rclone would guess its quirks.");
        }
    }

    [Fact]
    public void Only_multi_protocol_providers_offer_the_benchmark()
    {
        foreach (var descriptor in ProviderCatalog.All)
        {
            Assert.Equal(descriptor.Protocols.Count > 1, descriptor.SupportsProtocolBenchmark);
        }
    }

    [Fact]
    public void Hetzner_storage_box_and_object_storage_stay_distinct()
    {
        // Conflating these is the most common Hetzner setup mistake, and the model exists to stop it.
        var box = ProviderCatalog.Get(ProviderId.HetznerStorageBox);
        var objectStorage = ProviderCatalog.Get(ProviderId.HetznerObjectStorage);

        Assert.DoesNotContain(StorageProtocol.S3, box.Protocols);
        Assert.Contains(StorageProtocol.S3, objectStorage.Protocols);
    }

    [Fact]
    public void Sftp_ssh_and_sshfs_are_one_provider()
    {
        // SSHFS is the Linux FUSE client for SFTP-over-SSH, not a separate protocol. Three entries
        // producing identical connections would be a lie in the UI.
        var sftp = ProviderCatalog.Get(ProviderId.Sftp);
        Assert.Equal([StorageProtocol.Sftp], sftp.Protocols);
        Assert.Contains("SSH", sftp.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Provider_display_names_are_unique()
    {
        var names = ProviderCatalog.All.Select(p => p.DisplayName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
