using CloudDrive.Core.Models;

namespace CloudDrive.Tests;

/// <summary>
/// Ownership, which is what lets a standard user use CloudDrive without elevation.
///
/// <para>Every write used to require administrator rights. That closed a real hole — the service runs as
/// LocalSystem, so editing a serviced mapping means directing a SYSTEM process at a path of your
/// choosing — but it took the ordinary case with it, and both predecessors needed no privilege at all
/// because they only ever mounted into the caller's own session.</para>
///
/// <para>These assert the model half of the replacement. The authorisation decisions themselves live in
/// <c>IpcDispatcher</c>, which needs a live pipe and a real caller token to exercise.</para>
/// </summary>
public class OwnershipTests
{
    [Fact]
    public void An_account_with_no_owner_is_shared()
    {
        // Administrator-created accounts are machine-wide: other users' mappings may depend on them,
        // so changing one needs elevation.
        Assert.True(new Account { Name = "Shared" }.IsMachineWide);
        Assert.True(new Account { Name = "Shared", OwnerSid = string.Empty }.IsMachineWide);
    }

    [Fact]
    public void An_account_with_an_owner_belongs_to_that_user()
    {
        var account = new Account { Name = "Mine", OwnerSid = "S-1-5-21-1-2-3-1001" };
        Assert.False(account.IsMachineWide);
    }

    [Fact]
    public void Ownership_survives_a_clone()
    {
        // Clone is used by every edit dialog; losing the owner would silently convert a personal account
        // into a shared one on save.
        var account = new Account { Name = "Mine", OwnerSid = "S-1-5-21-1-2-3-1001" };
        Assert.Equal(account.OwnerSid, account.Clone().OwnerSid);

        var mapping = new Mapping { Name = "Mine", OwnerSid = "S-1-5-21-1-2-3-1001" };
        Assert.Equal(mapping.OwnerSid, mapping.Clone().OwnerSid);
    }

    /// <summary>
    /// A session-hosted mapping is not serviceable, whatever else is set. This is the invariant that
    /// keeps a standard user's mapping out of the LocalSystem service entirely.
    /// </summary>
    [Fact]
    public void A_session_hosted_mapping_is_never_serviceable()
    {
        var mapping = new Mapping
        {
            Name = "Mine",
            Mode = MappingMode.DriveLetter,
            Host = MountHost.UserSession,
            DriveLetter = "X",
        };

        Assert.False(mapping.IsServiceable);
    }

    [Fact]
    public void A_service_hosted_drive_mapping_is_serviceable()
    {
        var mapping = new Mapping
        {
            Name = "Shared",
            Mode = MappingMode.DriveLetter,
            Host = MountHost.Service,
            DriveLetter = "X",
        };

        Assert.True(mapping.IsServiceable);
    }

    [Fact]
    public void An_on_demand_mapping_is_never_serviceable()
    {
        // A Cloud Files sync root lives in a user profile and calls back into that user's session; there
        // is no session-0 equivalent, so the service can never host one.
        var mapping = new Mapping
        {
            Name = "Docs",
            Mode = MappingMode.OnDemandFolder,
            Host = MountHost.Service,
        };

        Assert.False(mapping.IsServiceable);
    }

    [Fact]
    public void Validation_rejects_an_on_demand_mapping_hosted_by_the_service()
    {
        var account = new Account { Name = "W", Provider = ProviderId.Wasabi, RegionCode = "us-east-1" };
        var mapping = new Mapping
        {
            Name = "Docs",
            AccountId = account.Id,
            Container = "bucket",
            Mode = MappingMode.OnDemandFolder,
            Host = MountHost.Service,
        };

        var problems = mapping.Validate(account);
        Assert.Contains(problems, p => p.Contains("session-0", StringComparison.OrdinalIgnoreCase)
                                       || p.Contains("user profile", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_session_hosted_on_demand_mapping_validates()
    {
        var account = new Account { Name = "W", Provider = ProviderId.Wasabi, RegionCode = "us-east-1" };
        var mapping = new Mapping
        {
            Name = "Docs",
            AccountId = account.Id,
            Container = "bucket",
            Mode = MappingMode.OnDemandFolder,
            Host = MountHost.UserSession,
        };

        Assert.Empty(mapping.Validate(account));
    }
}
