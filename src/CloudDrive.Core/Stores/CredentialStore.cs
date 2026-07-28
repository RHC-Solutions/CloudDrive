using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CloudDrive.Core.Models;

namespace CloudDrive.Core.Stores;

/// <summary>
/// Every secret CloudDrive holds — account credentials and notification tokens — encrypted at rest
/// with Windows DPAPI in one blob.
///
/// <para><b>Why the scope is CurrentUser and not LocalMachine.</b> A service-owned store has to be
/// readable by the service and by nothing else. The obvious choice, <see
/// cref="DataProtectionScope.LocalMachine"/>, is what both source projects used, and it is weak:
/// <i>any</i> process on the box can call <c>Unprotect</c> on a LocalMachine blob, so the file ACL
/// ends up being the entire defence and the encryption is decoration. Because this store is only
/// ever opened from inside the service, which runs as LocalSystem, it can use <see
/// cref="DataProtectionScope.CurrentUser"/> instead — binding the blob to SYSTEM's own profile.
/// Decryption then genuinely requires running as SYSTEM, and the ACL becomes defence in depth rather
/// than the only line.</para>
///
/// <para>The consequence is that nothing outside the service can read secrets. That is deliberate:
/// the tray app asks the service for what it needs over the IPC pipe, and the CLI does the same. It
/// also means the blob does not survive a Windows reinstall or a restore onto different hardware,
/// which is why export deliberately omits secrets.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialStore
{
    /// <summary>
    /// Extra entropy mixed into DPAPI so the blob is scoped to this application rather than to
    /// anything else running as SYSTEM.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudDrive.v1.secrets");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly DataProtectionScope _scope;
    private readonly Lock _gate = new();
    private Vault _vault = new();
    private bool _loaded;

    /// <param name="path">Blob location. Defaults to the machine store.</param>
    /// <param name="scope">
    /// Protection scope. Leave at <see cref="DataProtectionScope.CurrentUser"/>; it is a parameter
    /// only so tests can exercise the store without running as SYSTEM.
    /// </param>
    public CredentialStore(string? path = null, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        _path = path ?? AppPaths.CredentialsFile;
        _scope = scope;
    }

    public string Path => _path;

    /// <summary>
    /// Reads and decrypts the store. Safe to call when the file does not exist yet.
    /// </summary>
    /// <exception cref="CryptographicException">
    /// The blob exists but cannot be decrypted by this account — almost always because it was
    /// written by the service and something else is trying to read it, which is the boundary
    /// working as designed.
    /// </exception>
    public void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                _vault = new Vault();
                _loaded = true;
                return;
            }

            var protectedBytes = File.ReadAllBytes(_path);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, _scope);
            try
            {
                _vault = JsonSerializer.Deserialize<Vault>(plain, Json) ?? new Vault();
            }
            finally
            {
                // The decrypted JSON contains every password in the product. Overwrite the buffer
                // rather than leaving it for the GC to hand to whatever allocates next.
                CryptographicOperations.ZeroMemory(plain);
            }
            _loaded = true;
        }
    }

    private void EnsureLoaded()
    {
        if (!_loaded) Load();
    }

    // ---------------------------------------------------------------- Accounts ----------------

    public Credentials? GetAccount(Guid accountId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _vault.Accounts.TryGetValue(Key(accountId), out var c) ? c.Clone() : null;
        }
    }

    public void SetAccount(Guid accountId, Credentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        lock (_gate)
        {
            EnsureLoaded();
            _vault.Accounts[Key(accountId)] = credentials.Clone();
            Save();
        }
    }

    public bool RemoveAccount(Guid accountId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_vault.Accounts.Remove(Key(accountId))) return false;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to an account's credentials and persists the result, under
    /// the store lock. This is how a token refresh writes back without a read-modify-write race
    /// against a concurrent edit from the UI.
    /// </summary>
    public bool UpdateAccount(Guid accountId, Action<Credentials> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            EnsureLoaded();
            if (!_vault.Accounts.TryGetValue(Key(accountId), out var existing)) return false;
            mutate(existing);
            Save();
            return true;
        }
    }

    // ---------------------------------------------------------------- Notifications -----------

    public NotificationSecret? GetNotification(Guid targetId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _vault.Notifications.TryGetValue(Key(targetId), out var s) ? s.Clone() : null;
        }
    }

    public void SetNotification(Guid targetId, NotificationSecret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        lock (_gate)
        {
            EnsureLoaded();
            _vault.Notifications[Key(targetId)] = secret.Clone();
            Save();
        }
    }

    public bool RemoveNotification(Guid targetId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_vault.Notifications.Remove(Key(targetId))) return false;
            Save();
            return true;
        }
    }

    /// <summary>Ids that have stored account credentials, so orphans can be pruned.</summary>
    public IReadOnlyCollection<Guid> AccountIds()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _vault.Accounts.Keys
                .Select(k => Guid.TryParse(k, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToArray();
        }
    }

    /// <summary>Drops credentials for ids no longer present in the configuration.</summary>
    public int PruneAccounts(IReadOnlySet<Guid> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        lock (_gate)
        {
            EnsureLoaded();
            var doomed = _vault.Accounts.Keys
                .Where(k => !(Guid.TryParse(k, out var g) && keep.Contains(g)))
                .ToArray();
            foreach (var k in doomed) _vault.Accounts.Remove(k);
            if (doomed.Length > 0) Save();
            return doomed.Length;
        }
    }

    // ---------------------------------------------------------------- Persistence -------------

    private void Save()
    {
        if (AppPaths.IsInMachineStore(_path)) AppPaths.EnsureMachineStore();
        else Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

        var plain = JsonSerializer.SerializeToUtf8Bytes(_vault, Json);
        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectedData.Protect(plain, Entropy, _scope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        // Atomic, so a crash mid-write cannot leave a half-encrypted blob that no longer decrypts
        // — which would lose every stored credential at once.
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, protectedBytes);
        File.Move(tmp, _path, overwrite: true);

        // Harden after the move, not before. Stripping the writer's own access first would deny it
        // the DELETE right the rename needs. The containing directory is already restricted, so the
        // file is never broadly readable in between.
        if (AppPaths.IsInMachineStore(_path)) TryRestrictFile(_path);
    }

    /// <summary>
    /// Grants only SYSTEM and Administrators. Belt and braces alongside the DPAPI scope: SYSTEM is
    /// the only account that can decrypt, and now the only one that can read the bytes either.
    /// </summary>
    private static void TryRestrictFile(string path)
    {
        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            }
            new FileInfo(path).SetAccessControl(security);
        }
        catch
        {
            // The directory ACL and the DPAPI scope both still apply.
        }
    }

    private static string Key(Guid id) => id.ToString("N");

    /// <summary>The decrypted shape. Never leaves this class.</summary>
    private sealed class Vault
    {
        public Dictionary<string, Credentials> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, NotificationSecret> Notifications { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
