using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using CloudDrive.App.Services;
using CloudDrive.Core.Models;
using CloudDrive.Core.Providers;

namespace CloudDrive.App.Views;

[SupportedOSPlatform("windows")]
public partial class MappingEditWindow : Window
{
    private readonly AppController _controller;
    private readonly Mapping _mapping;
    private readonly bool _isNew;

    public MappingEditWindow(AppController controller, Mapping? existing)
    {
        _controller = controller;
        _isNew = existing is null;
        _mapping = existing?.Clone() ?? new Mapping
        {
            Cache = controller.Settings.DefaultCache.Clone(),
        };

        InitializeComponent();

        HeaderText.Text = _isNew ? "Add mapping" : $"Edit '{_mapping.Name}'";
        BuildChoices();
        LoadFields();
    }

    public Mapping? Result { get; private set; }

    private Account? SelectedAccount => (AccountBox.SelectedItem as AccountChoice)?.Account;

    private void BuildChoices()
    {
        AccountBox.ItemsSource = _controller.Accounts
            .Select(a => new AccountChoice(a))
            .OrderBy(c => c.ToString(), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Only offer Files On-Demand where the API actually exists. On Server 2016 there is no
        // cldapi.dll, and offering a mode that cannot work is worse than not offering it.
        var modes = new List<ModeChoice> { new(MappingMode.DriveLetter, "Drive or folder mountpoint") };
        if (_controller.Capabilities.SupportsFilesOnDemand)
            modes.Insert(0, new ModeChoice(MappingMode.OnDemandFolder, "Files On-Demand folder"));
        ModeBox.ItemsSource = modes;

        HostBox.ItemsSource = new[]
        {
            new HostChoice(MountHost.Service, "The CloudDrive service"),
            new HostChoice(MountHost.UserSession, "This sign-in session"),
        };

        // Letters already taken by a real volume are excluded; C and below never offered.
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();
        var free = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(c => ((char)c).ToString())
            .Where(l => !used.Contains(l[0]) || l == _mapping.DriveLetter)
            .ToList();
        LetterBox.ItemsSource = free;
    }

    private void LoadFields()
    {
        AccountBox.SelectedItem = AccountBox.Items
            .OfType<AccountChoice>()
            .FirstOrDefault(c => c.Account.Id == _mapping.AccountId)
            ?? AccountBox.Items.OfType<AccountChoice>().FirstOrDefault();

        NameBox.Text = _mapping.Name;
        ContainerBox.Text = _mapping.Container;
        SubPathBox.Text = _mapping.SubPath ?? string.Empty;

        ModeBox.SelectedItem = ModeBox.Items.OfType<ModeChoice>()
            .FirstOrDefault(m => m.Mode == _mapping.Mode)
            ?? ModeBox.Items.OfType<ModeChoice>().First();

        HostBox.SelectedItem = HostBox.Items.OfType<HostChoice>()
            .First(h => h.Host == _mapping.Host);

        LetterRadio.IsChecked = _mapping.MountTarget == MountTarget.DriveLetter;
        DirectoryRadio.IsChecked = _mapping.MountTarget == MountTarget.Directory;
        LetterBox.SelectedItem = LetterBox.Items.OfType<string>()
            .FirstOrDefault(l => l == _mapping.DriveLetter) ?? LetterBox.Items.OfType<string>().FirstOrDefault();
        DirectoryBox.Text = _mapping.MountDirectory ?? string.Empty;
        LocalFolderBox.Text = _mapping.LocalFolderPath ?? string.Empty;

        NetworkDriveCheck.IsChecked = _mapping.PresentAsNetworkDrive;
        DriveIconCheck.IsChecked = _mapping.UseCustomDriveIcon;
        AutoMountCheck.IsChecked = _mapping.AutoMount;
        ReadOnlyCheck.IsChecked = _mapping.ReadOnly;
        NoUpdateCheck.IsChecked = _mapping.BlockAutoUpdateWhileMounted;

        ApplyAccount();
        ApplyMode();
        ApplyTarget();
        ApplyHost();
    }

    private void OnAccountChanged(object sender, SelectionChangedEventArgs e) => ApplyAccount();

    private void ApplyAccount()
    {
        if (SelectedAccount is null) return;
        var d = ProviderCatalog.Get(SelectedAccount.Provider);

        // The container is labelled in the provider's own vocabulary — a Wasabi user looking for
        // "Bucket" should not have to work out that we mean it by "Container".
        ContainerLabel.Text = d.ContainerLabel;
        ContainerPanel.Visibility = d.Has(ProviderCapabilities.Container)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (NameBox.Text.Length == 0 && _isNew) NameBox.Text = SelectedAccount.Name;
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e) => ApplyMode();

    private void ApplyMode()
    {
        var mode = (ModeBox.SelectedItem as ModeChoice)?.Mode ?? MappingMode.DriveLetter;
        var isOnDemand = mode == MappingMode.OnDemandFolder;

        DrivePanel.Visibility = isOnDemand ? Visibility.Collapsed : Visibility.Visible;
        OnDemandPanel.Visibility = isOnDemand ? Visibility.Visible : Visibility.Collapsed;

        ModeHint.Text = isOnDemand
            ? "Files appear in Explorer as placeholders and download only when opened, with the native "
              + "Status column and \"Free up space\". Runs in your session, so it needs you signed in."
            : "A drive backed by rclone and WinFsp. Can be hosted by the service, so it exists before "
              + "anyone signs in.";
    }

    private void OnTargetChanged(object sender, RoutedEventArgs e) => ApplyTarget();

    private void ApplyTarget()
    {
        var isLetter = LetterRadio.IsChecked == true;
        LetterPanel.Visibility = isLetter ? Visibility.Visible : Visibility.Collapsed;
        DirectoryPanel.Visibility = isLetter ? Visibility.Collapsed : Visibility.Visible;

        // Windows will not point a junction at a network device, so rclone ignores --network-mode on
        // a directory mountpoint. Disabling the box says so instead of letting it look effective.
        NetworkDriveCheck.IsEnabled = isLetter;
        if (!isLetter) NetworkDriveCheck.IsChecked = false;
        DriveIconCheck.IsEnabled = isLetter;

        if (!isLetter && DirectoryBox.Text.Length == 0)
            DirectoryBox.Text = _mapping.DefaultMountDirectory;
    }

    private void OnHostChanged(object sender, SelectionChangedEventArgs e) => ApplyHost();

    private void ApplyHost()
    {
        var host = (HostBox.SelectedItem as HostChoice)?.Host ?? MountHost.Service;
        HostHint.Text = host == MountHost.Service
            ? "Mounted at boot by the LocalSystem service, visible from every session, and there "
              + "before anyone signs in. Also visible to every other user on this machine."
            : "Mounted by this app in your session only. Disappears when you sign out.";
    }

    private void OnBrowseDirectory(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the parent folder",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;

        // WinFsp needs the leaf itself to be absent, so a subfolder of the chosen parent is
        // proposed rather than the parent, which will certainly exist.
        var leaf = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : "CloudDrive";
        foreach (var c in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(c, '_');
        DirectoryBox.Text = Path.Combine(dialog.FolderName, leaf);
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the local folder" };
        if (dialog.ShowDialog(this) == true) LocalFolderBox.Text = dialog.FolderName;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        try
        {
            if (SelectedAccount is null) { ErrorText.Text = "Choose an account."; return; }

            Collect();

            var problems = _mapping.Validate(SelectedAccount);
            if (problems.Count > 0) { ErrorText.Text = string.Join(" ", problems); return; }

            SaveButton.IsEnabled = false;
            Result = await _controller.SaveMappingAsync(_mapping);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void Collect()
    {
        _mapping.AccountId = SelectedAccount!.Id;
        _mapping.Name = NameBox.Text.Trim();
        _mapping.Container = ContainerBox.Text.Trim();
        _mapping.SubPath = string.IsNullOrWhiteSpace(SubPathBox.Text) ? null : SubPathBox.Text.Trim();
        _mapping.Mode = (ModeBox.SelectedItem as ModeChoice)?.Mode ?? MappingMode.DriveLetter;

        if (_mapping.Mode == MappingMode.OnDemandFolder)
        {
            // A sync root has to live in the user's session; there is no session-0 cfapi.
            _mapping.Host = MountHost.UserSession;
            _mapping.LocalFolderPath = string.IsNullOrWhiteSpace(LocalFolderBox.Text)
                ? null
                : LocalFolderBox.Text.Trim();
        }
        else
        {
            _mapping.Host = (HostBox.SelectedItem as HostChoice)?.Host ?? MountHost.Service;
            _mapping.MountTarget = LetterRadio.IsChecked == true
                ? MountTarget.DriveLetter
                : MountTarget.Directory;
            _mapping.DriveLetter = LetterBox.SelectedItem as string ?? "H";
            _mapping.MountDirectory = string.IsNullOrWhiteSpace(DirectoryBox.Text)
                ? null
                : DirectoryBox.Text.Trim();
            _mapping.PresentAsNetworkDrive = NetworkDriveCheck.IsChecked == true;
            _mapping.UseCustomDriveIcon = DriveIconCheck.IsChecked == true;
        }

        _mapping.AutoMount = AutoMountCheck.IsChecked == true;
        _mapping.ReadOnly = ReadOnlyCheck.IsChecked == true;
        _mapping.BlockAutoUpdateWhileMounted = NoUpdateCheck.IsChecked == true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    internal sealed class AccountChoice(Account account)
    {
        public Account Account { get; } = account;

        public override string ToString() =>
            $"{Account.Name} — {ProviderCatalog.Get(Account.Provider).DisplayName}";
    }

    internal sealed record ModeChoice(MappingMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    internal sealed record HostChoice(MountHost Host, string Label)
    {
        public override string ToString() => Label;
    }
}
