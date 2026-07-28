using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using CloudDrive.App.Services;
using CloudDrive.Core.Models;

namespace CloudDrive.App.Views;

[SupportedOSPlatform("windows")]
public partial class SettingsWindow : Window
{
    private readonly AppController _controller;
    private readonly AppSettings _settings;

    public SettingsWindow(AppController controller)
    {
        _controller = controller;
        // Edit a copy. Cancelling has to leave the live settings untouched, and the dialog writes to
        // several fields before the user commits to any of them.
        _settings = Clone(controller.Settings);

        InitializeComponent();
        LoadFields();
        _ = LoadToolsAsync();
    }

    private static AppSettings Clone(AppSettings source)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(source);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    private void LoadFields()
    {
        StartAtLoginCheck.IsChecked = _settings.StartAtLogin;
        MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
        VerboseCheck.IsChecked = _settings.VerboseLogging;
        LogDaysBox.Text = _settings.LogRetentionDays.ToString(CultureInfo.InvariantCulture);
        BenchmarkSizeBox.Text = _settings.BenchmarkPayloadMiB.ToString(CultureInfo.InvariantCulture);
        ProtocolCacheDaysBox.Text = _settings.ProtocolCacheDays.ToString(CultureInfo.InvariantCulture);
        AlwaysBenchmarkCheck.IsChecked = _settings.AlwaysReBenchmark;

        var u = _settings.Updates;
        CheckUpdatesCheck.IsChecked = u.CheckForUpdates;
        AutoInstallCheck.IsChecked = u.AutoInstallWhenIdle;
        PrereleaseCheck.IsChecked = u.IncludePrereleases;
        CheckHoursBox.Text = u.CheckIntervalHours.ToString(CultureInfo.InvariantCulture);
        IdleMinutesBox.Text = u.IdleMinutesBeforeInstall.ToString(CultureInfo.InvariantCulture);
        WindowStartBox.Text = u.MaintenanceWindowStart ?? string.Empty;
        WindowEndBox.Text = u.MaintenanceWindowEnd ?? string.Empty;
        NotifyAvailableCheck.IsChecked = u.NotifyOnAvailable;
        NotifyBeforeCheck.IsChecked = u.NotifyBeforeInstall;
        NotifyAfterCheck.IsChecked = u.NotifyAfterInstall;

        AddToPathCheck.IsChecked = _settings.Tools.AddToSystemPath;
        ToolAutoUpdateCheck.IsChecked = _settings.Tools.AutoInstallWhenIdle;

        var n = _settings.Notifications;
        AlertsEnabledCheck.IsChecked = n.Enabled;
        CooldownBox.Text = n.DedupeCooldownMinutes.ToString(CultureInfo.InvariantCulture);
        SpoolHoursBox.Text = n.SpoolRetentionHours.ToString(CultureInfo.InvariantCulture);
        RefreshTargets();
    }

    private void RefreshTargets() => TargetList.ItemsSource = _settings.Notifications.Targets.ToList();

    private async Task LoadToolsAsync()
    {
        try
        {
            var state = await _controller.GetToolStateAsync();
            if (state is null) return;
            ToolList.ItemsSource = state.Tools;
            ToolsPathText.Text = $"Tools directory: {state.ToolsDirectory}"
                                 + (state.OnSystemPath ? " (on PATH)" : " (not on PATH)");
        }
        catch (Exception ex)
        {
            ToolsPathText.Text = $"Could not read the tools directory: {ex.Message}";
        }
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        UpdateStatus.Text = "Checking…";
        try
        {
            var result = await _controller.CheckForUpdateAsync();
            UpdateStatus.Text = result switch
            {
                null => "No answer from the service.",
                { UpdateAvailable: false } => $"Up to date: version {result.CurrentVersion}.",
                { DeferredReason: not null } =>
                    $"Version {result.AvailableVersion} is downloaded and waiting. {result.DeferredReason}",
                _ => $"Version {result.AvailableVersion} is ready and will install once this machine is idle.",
            };
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = ex.Message;
        }
    }

    private async void OnCheckTools(object sender, RoutedEventArgs e)
    {
        ToolsPathText.Text = "Checking each vendor…";
        try
        {
            await _controller.GetToolStateAsync();
            await LoadToolsAsync();
        }
        catch (Exception ex)
        {
            ToolsPathText.Text = ex.Message;
        }
    }

    private void OnRollbackTool(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            "Rolling a tool back is available from the command line: clouddrive tools rollback <name>.",
            "CloudDrive", MessageBoxButton.OK, MessageBoxImage.Information);

    // ---------------------------------------------------------------- Alert targets -----------

    private void OnAddTarget(object sender, RoutedEventArgs e)
    {
        var dialog = new NotificationTargetWindow(_controller, null) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Target is null) return;

        _settings.Notifications.Targets.Add(dialog.Target);
        RefreshTargets();
    }

    private void OnEditTarget(object sender, RoutedEventArgs e)
    {
        if (TargetList.SelectedItem is not NotificationTarget selected) return;

        var dialog = new NotificationTargetWindow(_controller, selected) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Target is null) return;

        var index = _settings.Notifications.Targets.FindIndex(t => t.Id == dialog.Target.Id);
        if (index >= 0) _settings.Notifications.Targets[index] = dialog.Target;
        RefreshTargets();
    }

    private async void OnDeleteTarget(object sender, RoutedEventArgs e)
    {
        if (TargetList.SelectedItem is not NotificationTarget selected) return;

        if (MessageBox.Show($"Delete the alert target '{selected.Name}'?", "CloudDrive",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _settings.Notifications.Targets.RemoveAll(t => t.Id == selected.Id);
        RefreshTargets();

        // Delete the stored token immediately rather than at Save: leaving a bot token behind for a
        // target the user believes they removed is not acceptable, even briefly.
        try { await _controller.DeleteNotificationTargetAsync(selected.Id); }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }

    private async void OnTestTarget(object sender, RoutedEventArgs e)
    {
        if (TargetList.SelectedItem is not NotificationTarget selected) return;

        ErrorText.Text = string.Empty;
        try
        {
            await _controller.SendTestAlertAsync(selected, null);
            MessageBox.Show($"A test message was sent to '{selected.Name}'.", "CloudDrive",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Test failed: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- Save --------------------

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        try
        {
            Collect();

            var problem = Validate();
            if (problem is not null) { ErrorText.Text = problem; return; }

            SaveButton.IsEnabled = false;
            await _controller.SaveSettingsAsync(_settings);
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
        _settings.StartAtLogin = StartAtLoginCheck.IsChecked == true;
        _settings.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
        _settings.VerboseLogging = VerboseCheck.IsChecked == true;
        _settings.LogRetentionDays = ParseInt(LogDaysBox.Text, _settings.LogRetentionDays, 1, 3650);
        _settings.BenchmarkPayloadMiB = ParseInt(BenchmarkSizeBox.Text, _settings.BenchmarkPayloadMiB, 1, 1024);
        _settings.ProtocolCacheDays = ParseInt(ProtocolCacheDaysBox.Text, _settings.ProtocolCacheDays, 1, 365);
        _settings.AlwaysReBenchmark = AlwaysBenchmarkCheck.IsChecked == true;

        var u = _settings.Updates;
        u.CheckForUpdates = CheckUpdatesCheck.IsChecked == true;
        u.AutoInstallWhenIdle = AutoInstallCheck.IsChecked == true;
        u.IncludePrereleases = PrereleaseCheck.IsChecked == true;
        u.CheckIntervalHours = ParseInt(CheckHoursBox.Text, u.CheckIntervalHours, 1, 168);
        u.IdleMinutesBeforeInstall = ParseInt(IdleMinutesBox.Text, u.IdleMinutesBeforeInstall, 1, 1440);
        u.MaintenanceWindowStart = Blank(WindowStartBox.Text);
        u.MaintenanceWindowEnd = Blank(WindowEndBox.Text);
        u.NotifyOnAvailable = NotifyAvailableCheck.IsChecked == true;
        u.NotifyBeforeInstall = NotifyBeforeCheck.IsChecked == true;
        u.NotifyAfterInstall = NotifyAfterCheck.IsChecked == true;

        _settings.Tools.AddToSystemPath = AddToPathCheck.IsChecked == true;
        _settings.Tools.AutoInstallWhenIdle = ToolAutoUpdateCheck.IsChecked == true;

        var n = _settings.Notifications;
        n.Enabled = AlertsEnabledCheck.IsChecked == true;
        n.DedupeCooldownMinutes = ParseInt(CooldownBox.Text, n.DedupeCooldownMinutes, 0, 1440);
        n.SpoolRetentionHours = ParseInt(SpoolHoursBox.Text, n.SpoolRetentionHours, 1, 720);
    }

    private string? Validate()
    {
        var u = _settings.Updates;

        // Half a window is worse than none: it would silently never match, and updates would appear
        // to be broken rather than deferred.
        var hasStart = !string.IsNullOrWhiteSpace(u.MaintenanceWindowStart);
        var hasEnd = !string.IsNullOrWhiteSpace(u.MaintenanceWindowEnd);
        if (hasStart != hasEnd) return "Set both ends of the maintenance window, or neither.";

        if (hasStart && (!TimeSpan.TryParse(u.MaintenanceWindowStart, CultureInfo.InvariantCulture, out _)
                         || !TimeSpan.TryParse(u.MaintenanceWindowEnd, CultureInfo.InvariantCulture, out _)))
        {
            return "The maintenance window must be times of day, such as 22:00 and 04:00.";
        }

        if (_settings.Notifications.Enabled && _settings.Notifications.Targets.Count == 0)
            return "Alerts are switched on but there is nowhere to send them. Add a target, or switch them off.";

        return null;
    }

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static string? Blank(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
