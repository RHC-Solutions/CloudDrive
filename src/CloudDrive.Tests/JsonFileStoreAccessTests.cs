using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using CloudDrive.Core.Models;
using CloudDrive.Core.Stores;

namespace CloudDrive.Tests;

/// <summary>
/// The distinction between "this file is not there" and "this file cannot be read".
///
/// Conflating them caused the worst bug in the project. <see cref="File.Exists"/> returns false on
/// access-denied rather than throwing, so an unreadable configuration file was indistinguishable from
/// a missing one and <c>Load()</c> returned a blank document. A caller would then read nothing, add one
/// entry, and save it back — deleting every account and mapping on disk. Silent data loss from a
/// permissions problem.
/// </summary>
[SupportedOSPlatform("windows")]
public class JsonFileStoreAccessTests
{
    [Fact]
    public void A_missing_file_loads_as_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clouddrive-missing-{Guid.NewGuid():N}.json");
        var store = new JsonFileStore<ConfigDocument>(path, machineScope: false);

        // This is the normal state before anything is configured and must stay silent.
        Assert.Empty(store.Load().Accounts);
    }

    [Fact]
    public void A_missing_directory_loads_as_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clouddrive-nodir-{Guid.NewGuid():N}", "config.json");
        var store = new JsonFileStore<ConfigDocument>(path, machineScope: false);

        Assert.Empty(store.Load().Accounts);
    }

    [Fact]
    public void An_unreadable_file_throws_rather_than_loading_as_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clouddrive-denied-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"Accounts":[{"Name":"Real","Provider":"Wasabi"}],"Mappings":[]}""");

        var user = WindowsIdentity.GetCurrent().User!;
        try
        {
            // Deny this account read access to a file it can still see.
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                user, FileSystemRights.Read, AccessControlType.Deny));
            new FileInfo(path).SetAccessControl(security);

            var store = new JsonFileStore<ConfigDocument>(path, machineScope: false);

            var ex = Assert.Throws<InvalidOperationException>(() => store.Load());
            Assert.Contains("cannot be read", ex.Message, StringComparison.OrdinalIgnoreCase);
            // The message has to say why it refuses, because the alternative looks like a no-op.
            Assert.Contains("discard", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                var reset = new FileSecurity();
                reset.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                reset.AddAccessRule(new FileSystemAccessRule(
                    user, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(reset);
                File.Delete(path);
            }
            catch { /* temp */ }
        }
    }
}
