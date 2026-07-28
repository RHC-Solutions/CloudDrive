using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows;
using CloudDrive.App.Services;
using CloudDrive.Core.Tooling;

namespace CloudDrive.App.Views;

[SupportedOSPlatform("windows")]
public partial class AboutWindow : Window
{
    public AboutWindow(AppController controller)
    {
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
    }

    private void OnGitHub(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo($"https://github.com/{UpdateService.Repository}")
        {
            UseShellExecute = true,
        });

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
