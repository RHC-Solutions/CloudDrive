using System.Windows.Media;
using CloudDrive.Core.Models;
using CloudDrive.Core.Providers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CloudDrive.App.ViewModels;

/// <summary>
/// One row in the mappings list.
///
/// A view of a <see cref="Mapping"/> plus its <see cref="Account"/> and live mount state, because
/// the row shows all three and the model deliberately keeps them apart.
/// </summary>
public sealed partial class MappingViewModel : ObservableObject
{
    public MappingViewModel(Mapping mapping, Account account)
    {
        Mapping = mapping;
        Account = account;
    }

    public Mapping Mapping { get; private set; }

    public Account Account { get; private set; }

    public Guid Id => Mapping.Id;

    public string Name => Mapping.Name;

    public string AccountName => Account.Name;

    public string ProviderName => ProviderCatalog.Get(Account.Provider).DisplayName;

    /// <summary>Brand colour for the provider chip.</summary>
    public Brush ProviderBrush
    {
        get
        {
            try
            {
                var hex = ProviderCatalog.Get(Account.Provider).AccentColor;
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            catch
            {
                return Brushes.Gray;
            }
        }
    }

    public string Location => Mapping.RemoteDescription;

    public string ModeText => Mapping.Mode == MappingMode.OnDemandFolder
        ? "On-demand"
        : Mapping.MountTarget == MountTarget.Directory ? "Folder" : "Drive";

    /// <summary>Where it appears in Explorer.</summary>
    public string Target => Mapping.Mode == MappingMode.OnDemandFolder
        ? Mapping.LocalFolderPath ?? "(user profile)"
        : Mapping.MountPoint;

    /// <summary>Who runs it, which is the thing that decides whether it survives a logoff.</summary>
    public string HostText => Mapping.Host == MountHost.Service ? "Service" : "This session";

    [ObservableProperty]
    private MountState state = MountState.Unmounted;

    [ObservableProperty]
    private string? statusMessage;

    public string StateText => State switch
    {
        MountState.Mounted => "Mounted",
        MountState.Mounting => "Mounting…",
        MountState.Unmounting => "Unmounting…",
        MountState.Error => "Error",
        _ => "Not mounted",
    };

    public Brush StateBrush => State switch
    {
        MountState.Mounted => Lookup("CD.Brush.Success", Brushes.LimeGreen),
        MountState.Mounting or MountState.Unmounting => Lookup("CD.Brush.Warning", Brushes.Orange),
        MountState.Error => Lookup("CD.Brush.Danger", Brushes.Red),
        _ => Lookup("CD.Brush.TextSecondary", Brushes.Gray),
    };

    public bool CanMount => State is MountState.Unmounted or MountState.Error;

    public bool CanUnmount => State is MountState.Mounted or MountState.Error;

    /// <summary>Tooltip: the full story, including whatever the last error was.</summary>
    public string Tooltip
    {
        get
        {
            var lines = new List<string>
            {
                $"{ProviderName} · {AccountName}",
                $"{Location} → {Target}",
                $"Hosted by: {HostText}",
            };
            if (!string.IsNullOrWhiteSpace(StatusMessage)) lines.Add(StatusMessage!);
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Refreshes the row against new model data, keeping the same instance so selection survives.</summary>
    public void Update(Mapping mapping, Account account)
    {
        Mapping = mapping;
        Account = account;
        OnPropertyChanged(string.Empty); // every property is derived
    }

    partial void OnStateChanged(MountState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(CanMount));
        OnPropertyChanged(nameof(CanUnmount));
        OnPropertyChanged(nameof(Tooltip));
    }

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(Tooltip));

    /// <summary>
    /// Resolves a theme brush by key, falling back to a literal.
    ///
    /// The fallback is not defensive padding: these properties are also read by the headless
    /// self-test, which constructs view models without an Application and therefore without the
    /// merged resource dictionaries.
    /// </summary>
    private static Brush Lookup(string key, Brush fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? fallback;
}
