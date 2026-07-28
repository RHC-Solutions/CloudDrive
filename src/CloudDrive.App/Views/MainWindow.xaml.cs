using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudDrive.App.Services;
using CloudDrive.App.ViewModels;
using CloudDrive.Core.Models;
using CloudDrive.Core.Platform;

namespace CloudDrive.App.Views;

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly AppController _controller;
    private bool _logPinnedToBottom = true;

    public MainWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        DataContext = this;

        _controller.StateRefreshed += OnStateRefreshed;
        _controller.ConnectionChanged += _ => Dispatcher.BeginInvoke(RefreshBindings);
        _controller.LogLines.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(RefreshLog);
    }

    public ObservableCollection<MappingViewModel> Mappings => _controller.Mappings;

    public IReadOnlyList<string> Warnings => _controller.Warnings;

    public bool IsAdministrator => _controller.IsAdministrator;

    /// <summary>Offer to start the service only when that is actually the problem.</summary>
    public bool ShowStartServiceButton =>
        !_controller.IsConnected && ServiceControl.GetState() != ServiceState.Running;

    private MappingViewModel? _selectedMapping;

    public MappingViewModel? SelectedMapping
    {
        get => _selectedMapping;
        set { _selectedMapping = value; OnPropertyChanged(); }
    }

    public string StatusLine
    {
        get
        {
            if (!_controller.IsConnected) return "Not connected to the CloudDrive service.";
            var mounted = Mappings.Count(m => m.State == MountState.Mounted);
            var failed = Mappings.Count(m => m.State == MountState.Error);
            var text = $"{mounted} of {Mappings.Count} mounted";
            if (failed > 0) text += $" · {failed} failed";
            if (!_controller.Capabilities.SupportsFilesOnDemand) text += " · Files On-Demand unavailable on this OS";
            return text;
        }
    }

    public string VersionLine
    {
        get
        {
            var app = Core.Tooling.UpdateService.CurrentVersion;
            var service = _controller.ServiceVersion;
            return service is null || service == app
                ? $"CloudDrive {app}"
                : $"CloudDrive {app} · service {service}";
        }
    }

    private void OnStateRefreshed() => Dispatcher.BeginInvoke(RefreshBindings);

    private void RefreshBindings()
    {
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(IsAdministrator));
        OnPropertyChanged(nameof(ShowStartServiceButton));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(VersionLine));
    }

    private void RefreshLog()
    {
        LogBox.Text = _controller.LogText;
        OnPropertyChanged(nameof(StatusLine));
    }

    // ---------------------------------------------------------------- Commands ----------------

    private async void OnMount(object sender, RoutedEventArgs e)
    {
        var row = RowFor(sender);
        if (row is null) return;
        try { await _controller.MountAsync(row); }
        catch (Exception ex) { Fail($"Mounting '{row.Name}' failed", ex); }
    }

    private async void OnUnmount(object sender, RoutedEventArgs e)
    {
        var row = RowFor(sender);
        if (row is null) return;
        try { await _controller.UnmountAsync(row); }
        catch (Exception ex) { Fail($"Unmounting '{row.Name}' failed", ex); }
    }

    private void OnAddMapping(object sender, RoutedEventArgs e) => EditMapping(null);

    private void OnEditMapping(object sender, RoutedEventArgs e)
    {
        // The handler is shared by the toolbar button, the row menu and a double-click, so the row
        // comes from the sender when there is one and from the selection otherwise.
        var row = RowFor(sender) ?? SelectedMapping;
        if (row is not null) EditMapping(row.Mapping);
    }

    private void EditMapping(Mapping? existing)
    {
        if (!Guard()) return;

        if (_controller.Accounts.Count == 0)
        {
            var answer = MessageBox.Show(
                "A mapping needs an account to mount. Add one now?",
                "CloudDrive", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) OnAccounts(this, new RoutedEventArgs());
            return;
        }

        var dialog = new MappingEditWindow(_controller, existing) { Owner = this };
        if (dialog.ShowDialog() == true) _ = _controller.RefreshAsync();
    }

    private async void OnDeleteMapping(object sender, RoutedEventArgs e)
    {
        var row = RowFor(sender) ?? SelectedMapping;
        if (row is null || !Guard()) return;

        var answer = MessageBox.Show(
            $"Delete the mapping '{row.Name}'?\n\nThis unmounts it. Nothing on the remote storage is touched.",
            "CloudDrive", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            await _controller.DeleteMappingAsync(row.Id);
            await _controller.RefreshAsync();
        }
        catch (Exception ex) { Fail("Deleting the mapping failed", ex); }
    }

    private void OnAccounts(object sender, RoutedEventArgs e)
    {
        if (!Guard()) return;
        var dialog = new AccountsWindow(_controller) { Owner = this };
        dialog.ShowDialog();
        _ = _controller.RefreshAsync();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_controller) { Owner = this };
        dialog.ShowDialog();
        _ = _controller.RefreshAsync();
    }

    private void OnAbout(object sender, RoutedEventArgs e) =>
        new AboutWindow(_controller) { Owner = this }.ShowDialog();

    private async void OnStartService(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ServiceControl.GetState() == ServiceState.NotInstalled)
            {
                if (!AppController.InstallService()) return;
            }
            else if (AppController.IsElevated())
            {
                ServiceControl.Start(TimeSpan.FromSeconds(45));
            }
            else
            {
                ServiceControl.RelaunchElevated("--install-service");
                return;
            }

            // The service needs a moment to open its pipe after reporting Running.
            await Task.Delay(TimeSpan.FromSeconds(2));
            await _controller.ConnectAsync();
        }
        catch (Exception ex) { Fail("Starting the CloudDrive service failed", ex); }
    }

    private void OnOpenInExplorer(object sender, RoutedEventArgs e)
    {
        var row = RowFor(sender) ?? SelectedMapping;
        if (row is null) return;

        var target = row.Target;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
        {
            MessageBox.Show($"'{row.Name}' is not mounted.", "CloudDrive",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        var row = RowFor(sender) ?? SelectedMapping;
        if (row is not null) TrySetClipboard(row.Target);
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_controller.UserLogDirectory}\"")
        { UseShellExecute = true });

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        _controller.LogLines.Clear();
        LogBox.Clear();
    }

    // ---------------------------------------------------------------- Log pane ----------------

    /// <summary>
    /// Auto-scrolls only while the user is already at the bottom.
    ///
    /// Scrolling unconditionally would yank the view away every time a line arrived, which makes it
    /// impossible to read back through a mount failure while rclone is still logging.
    /// </summary>
    private void OnLogTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_logPinnedToBottom) LogBox.ScrollToEnd();
    }

    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0) return; // a content change, not the user scrolling
        _logPinnedToBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 4;
    }

    // ---------------------------------------------------------------- Plumbing ----------------

    /// <summary>
    /// Selects the row under the cursor before its context menu opens.
    ///
    /// WPF does not select on right-click, so without this the menu would act on whichever row was
    /// last left-clicked — which is a reliable way to unmount the wrong drive.
    /// </summary>
    private void OnListPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null and not ListViewItem)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);

        if (source is ListViewItem item) item.IsSelected = true;
    }

    /// <summary>The row a control belongs to, from its DataContext.</summary>
    private static MappingViewModel? RowFor(object sender) =>
        (sender as FrameworkElement)?.DataContext as MappingViewModel;

    /// <summary>Blocks a configuration change by a standard user, with an explanation.</summary>
    private bool Guard()
    {
        if (_controller.IsAdministrator) return true;

        MessageBox.Show(
            "Changing CloudDrive's configuration needs administrator rights, because these settings "
            + "control what the machine-wide service mounts.",
            "CloudDrive", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private static void Fail(string title, Exception ex) =>
        MessageBox.Show($"{title}.\n\n{ex.Message}", "CloudDrive",
            MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>
    /// Clipboard writes fail when another process is holding it open, which is common enough that
    /// throwing an unhandled exception over a copy is not acceptable.
    /// </summary>
    internal static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show("Another application is holding the clipboard. Try again.",
                "CloudDrive", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Close means hide: the app lives in the tray and its mounts keep running.
        if (_controller.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
