using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows;
using CloudDrive.App.Services;
using CloudDrive.Core.Platform;
using CloudDrive.Core.Tooling;
using CloudDrive.Ipc;

namespace CloudDrive.App.Views;

[SupportedOSPlatform("windows")]
public partial class AboutWindow : Window
{
    private readonly AppController _controller;
    private UpdateCheckResult? _pending;

    public AboutWindow(AppController controller)
    {
        _controller = controller;

        InitializeComponent();

        VersionText.Text = $"Version {UpdateService.CurrentVersion}";

        var capabilities = controller.Capabilities;

        ServiceText.Text = controller.IsConnected
            ? $"Service: running, version {controller.ServiceVersion}"
            : "Service: not connected";

        SystemText.Text = $"System: {capabilities.EditionName} (build {capabilities.BuildNumber})";

        RcloneText.Text = capabilities.RclonePath is null
            ? "rclone: not found"
            : $"rclone: {capabilities.RcloneVersion ?? "installed"} — {capabilities.RclonePath}";

        WinFspText.Text = capabilities.WinFspInstalled
            ? "WinFsp: installed"
            : "WinFsp: not installed — drive-letter mounts will fail";

        // Say why, not just no. On Server 2016 this is the single most likely question a user has.
        OnDemandText.Text = capabilities.SupportsFilesOnDemand
            ? "Files On-Demand: available"
            : $"Files On-Demand: unavailable. {capabilities.FilesOnDemandUnavailableReason}";

        UpdateStatusText.Text = controller.IsConnected
            ? "Choose Check now to look for a newer release."
            : "The service is not running, so updates cannot be checked.";
        CheckUpdateButton.IsEnabled = controller.IsConnected;
        ShowUpdatePolicy();
    }

    // ---------------------------------------------------------------- Updates -----------------

    /// <summary>
    /// Describes the update policy the service is running under.
    ///
    /// Worth stating rather than leaving implicit: a user who sees "Check now" reasonably assumes that
    /// is the only way updates happen, when in fact the service polls on its own and installs while the
    /// machine is quiet.
    /// </summary>
    private void ShowUpdatePolicy()
    {
        var updates = _controller.Settings.Updates;

        if (!updates.CheckForUpdates)
        {
            UpdatePolicyText.Text =
                "Automatic checking is off. Turn it on in Settings, or use Check now.";
            return;
        }

        var cadence = $"Checked automatically every {updates.CheckIntervalHours} hour"
                      + (updates.CheckIntervalHours == 1 ? string.Empty : "s") + ".";

        UpdatePolicyText.Text = updates.AutoInstallWhenIdle
            ? cadence + $" A new version installs on its own once the machine has been idle for "
                      + $"{updates.IdleMinutesBeforeInstall} minutes, so nothing is interrupted mid-transfer."
            : cadence + " Installing is left to you.";
    }

    private async void OnCheckForUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        ReleaseNotesButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Checking…";

        try
        {
            _pending = await _controller.CheckForUpdateAsync();
            ShowUpdateResult(_pending);
        }
        catch (Exception ex)
        {
            // A failed check is routine — no network, a rate limit, a private repository — and must not
            // look like a broken application.
            UpdateStatusText.Text = $"Could not check for updates: {ex.Message}";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void ShowUpdateResult(UpdateCheckResult? result)
    {
        if (result is null)
        {
            UpdateStatusText.Text = "The service did not answer. It may be stopped.";
            return;
        }

        if (!result.UpdateAvailable)
        {
            UpdateStatusText.Text = $"Up to date — version {result.CurrentVersion} is the newest release.";
            return;
        }

        var size = result.SizeBytes > 0
            ? $" ({result.SizeBytes / 1024 / 1024} MB)"
            : string.Empty;

        UpdateStatusText.Text = result.DeferredReason is { } reason
            ? $"Version {result.AvailableVersion} is downloaded and waiting{size}. {reason}"
            : $"Version {result.AvailableVersion} is available{size}.";

        // Shown to everyone, including a standard user.
        //
        // It used to be hidden unless the caller was an administrator, which left a standard user told
        // that an update was ready and given no way to apply it. Hiding the control does not remove the
        // need for elevation, it just removes the explanation -- and the update installs on its own once
        // the machine is idle regardless, so withholding the button only takes away the choice of when.
        // A standard user gets a UAC prompt instead.
        InstallUpdateButton.Visibility = Visibility.Visible;
        InstallUpdateButton.Content = _controller.IsAdministrator ? "Install now" : "Install now…";

        ReleaseNotesButton.Visibility = string.IsNullOrWhiteSpace(result.ReleaseUrl)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        var elevationNote = _controller.IsAdministrator
            ? string.Empty
            : "\n\nWindows will ask for administrator approval, because CloudDrive is installed for the "
              + "whole machine.";

        if (MessageBox.Show(
                $"Install version {_pending?.AvailableVersion} now?\n\n"
                + "Every mounted drive is unmounted while CloudDrive is replaced, then remounted."
                + elevationNote,
                "CloudDrive", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        InstallUpdateButton.IsEnabled = false;

        // A standard user cannot ask the service to install -- the service runs as LocalSystem and
        // replacing machine-wide files is exactly what elevation is for -- so the app relaunches itself
        // elevated to make the request, the same way installing the service works.
        if (!_controller.IsAdministrator)
        {
            UpdateStatusText.Text = "Waiting for administrator approval…";
            if (ServiceControl.RelaunchElevated("--install-update"))
            {
                UpdateStatusText.Text = "Installing… CloudDrive will restart.";
                Close();
            }
            else
            {
                UpdateStatusText.Text = "Administrator approval was declined, so nothing was installed.";
                InstallUpdateButton.IsEnabled = true;
            }
            return;
        }

        UpdateStatusText.Text = "Installing… CloudDrive will restart.";
        try
        {
            await _controller.InstallUpdateAsync();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"The update could not be installed: {ex.Message}";
            InstallUpdateButton.IsEnabled = true;
        }
    }

    private void OnReleaseNotes(object sender, RoutedEventArgs e)
    {
        var url = _pending?.ReleaseUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OnGitHub(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo($"https://github.com/{UpdateService.Repository}")
        {
            UseShellExecute = true,
        });

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
