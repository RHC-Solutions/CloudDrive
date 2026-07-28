using System.Runtime.Versioning;
using CloudDrive.Core.Models;

namespace CloudDrive.Core.Stores;

/// <summary>Accounts and mappings as one document, so a save is one atomic write.</summary>
public sealed class ConfigDocument
{
    public List<Account> Accounts { get; set; } = [];

    public List<Mapping> Mappings { get; set; } = [];
}

/// <summary>
/// The machine configuration: accounts, mappings and settings.
///
/// Accounts and mappings share one file rather than two. They reference each other, and two files
/// mean a window in which a mapping points at an account that has not been written yet — which the
/// service, watching for changes, would read and reject. One atomic write has no such window.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConfigStore
{
    private readonly JsonFileStore<ConfigDocument> _config;
    private readonly JsonFileStore<AppSettings> _settings;
    private readonly Lock _gate = new();

    public ConfigStore(string? configPath = null, string? settingsPath = null)
    {
        var config = configPath ?? AppPaths.MappingsFile;
        var settings = settingsPath ?? AppPaths.SettingsFile;
        var machine = AppPaths.IsInMachineStore(config);
        _config = new JsonFileStore<ConfigDocument>(config, machine);
        _settings = new JsonFileStore<AppSettings>(settings, AppPaths.IsInMachineStore(settings));
    }

    public string ConfigPath => _config.Path;

    public string SettingsPath => _settings.Path;

    /// <summary>Newest write across both files, so a watcher can tell whether anything changed.</summary>
    public DateTime? LastWriteUtc
    {
        get
        {
            var a = _config.LastWriteUtc;
            var b = _settings.LastWriteUtc;
            return a is null ? b : b is null ? a : a > b ? a : b;
        }
    }

    public ConfigDocument Load() => _config.Load();

    public void Save(ConfigDocument document) => _config.Save(document);

    public AppSettings LoadSettings() => _settings.Load();

    public void SaveSettings(AppSettings settings) => _settings.Save(settings);

    /// <summary>
    /// Reads, mutates and writes under one lock. Every mutation goes through here so two concurrent
    /// IPC requests cannot each read the same document, apply one change, and have the second write
    /// discard the first.
    /// </summary>
    public T Mutate<T>(Func<ConfigDocument, T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var document = _config.Load();
            var result = mutate(document);
            _config.Save(document);
            return result;
        }
    }

    public void Mutate(Action<ConfigDocument> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        Mutate<object?>(d => { mutate(d); return null; });
    }

    public T MutateSettings<T>(Func<AppSettings, T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var settings = _settings.Load();
            var result = mutate(settings);
            _settings.Save(settings);
            return result;
        }
    }
}

/// <summary>
/// Views over a <see cref="ConfigDocument"/> that every caller would otherwise re-implement, plus
/// the referential-integrity rules that keep a mapping from outliving its account.
/// </summary>
public static class ConfigDocumentExtensions
{
    public static Account? FindAccount(this ConfigDocument document, Guid id) =>
        document.Accounts.FirstOrDefault(a => a.Id == id);

    public static Mapping? FindMapping(this ConfigDocument document, Guid id) =>
        document.Mappings.FirstOrDefault(m => m.Id == id);

    /// <summary>Mappings that use <paramref name="accountId"/>.</summary>
    public static IEnumerable<Mapping> MappingsFor(this ConfigDocument document, Guid accountId) =>
        document.Mappings.Where(m => m.AccountId == accountId);

    /// <summary>
    /// Mappings the Windows service should mount, paired with their accounts. A mapping whose
    /// account has been deleted is skipped rather than throwing: the service must keep the other
    /// mounts up, and the orphan is reported separately by <see cref="OrphanedMappings"/>.
    /// </summary>
    public static IEnumerable<(Mapping Mapping, Account Account)> ServiceableMappings(
        this ConfigDocument document)
    {
        var accounts = document.Accounts.ToDictionary(a => a.Id);
        foreach (var mapping in document.Mappings.Where(m => m.IsServiceable))
        {
            if (accounts.TryGetValue(mapping.AccountId, out var account))
                yield return (mapping, account);
        }
    }

    /// <summary>Mappings pointing at an account that no longer exists.</summary>
    public static IEnumerable<Mapping> OrphanedMappings(this ConfigDocument document)
    {
        var ids = document.Accounts.Select(a => a.Id).ToHashSet();
        return document.Mappings.Where(m => !ids.Contains(m.AccountId));
    }

    /// <summary>
    /// Removes an account and every mapping that used it, returning the mappings removed.
    ///
    /// Cascading rather than refusing, because the alternative — an account that cannot be deleted
    /// until the user hunts down each mapping — is worse, and leaving orphans behind would give the
    /// service mounts it can never satisfy.
    /// </summary>
    public static IReadOnlyList<Mapping> RemoveAccountCascade(this ConfigDocument document, Guid accountId)
    {
        var removed = document.Mappings.Where(m => m.AccountId == accountId).ToList();
        document.Mappings.RemoveAll(m => m.AccountId == accountId);
        document.Accounts.RemoveAll(a => a.Id == accountId);
        return removed;
    }

    /// <summary>
    /// A drive letter or directory already claimed by a different mapping. Two mappings on one
    /// mount point is a configuration error that would otherwise surface as whichever mounted second
    /// failing at random.
    /// </summary>
    public static Mapping? FindMountPointConflict(this ConfigDocument document, Mapping candidate)
    {
        if (candidate.Mode != MappingMode.DriveLetter) return null;

        return document.Mappings.FirstOrDefault(m =>
            m.Id != candidate.Id
            && m.Mode == MappingMode.DriveLetter
            && !string.IsNullOrWhiteSpace(m.MountPoint)
            && string.Equals(m.MountPoint, candidate.MountPoint, StringComparison.OrdinalIgnoreCase));
    }
}
