using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using CloudDrive.App.Services;
using CloudDrive.Core.Models;
using CloudDrive.Ipc;
using Hardcodet.Wpf.TaskbarNotification;

namespace CloudDrive.App.Views;

/// <summary>
/// The notification-area icon and its menu.
///
/// The tray is where CloudDrive lives: the window is a thing you open occasionally, and the icon is
/// what tells you at a glance whether your drives are up. It is also the only surface that can
/// report an update or a failed mount while the window is closed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    private readonly AppController _controller;
    private readonly TaskbarIcon _icon;

    public TrayIcon(AppController controller)
    {
        _controller = controller;

        _icon = new TaskbarIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "CloudDrive",
            ContextMenu = BuildMenu(),
        };
        _icon.TrayMouseDoubleClick += (_, _) => ShowWindow();

        _controller.StateRefreshed += OnStateRefreshed;
        _controller.ConnectionChanged += OnConnectionChanged;
        _controller.UpdateAnnounced += OnUpdateAnnounced;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        // The .ico is an embedded WPF resource; System.Drawing.Icon needs a stream, and it picks the
        // frame matching the current DPI out of the multi-resolution file.
        var uri = new Uri("pack://application:,,,/Assets/clouddrive.ico", UriKind.Absolute);
        var stream = Application.GetResourceStream(uri)?.Stream;
        return stream is not null
            ? new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize)
            : System.Drawing.SystemIcons.Application;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "Open CloudDrive", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => ShowWindow();
        menu.Items.Add(open);

        menu.Items.Add(new Separator());

        var mountAll = new MenuItem { Header = "Mount all" };
        mountAll.Click += async (_, _) => await MountAllAsync(mount: true).ConfigureAwait(false);
        menu.Items.Add(mountAll);

        var unmountAll = new MenuItem { Header = "Unmount all" };
        unmountAll.Click += async (_, _) => await MountAllAsync(mount: false).ConfigureAwait(false);
        menu.Items.Add(unmountAll);

        menu.Items.Add(new Separator());

        var check = new MenuItem { Header = "Check for updates" };
        check.Click += async (_, _) => await CheckForUpdatesAsync().ConfigureAwait(false);
        menu.Items.Add(check);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exit);

        return menu;
    }

    private static void ShowWindow()
    {
        var window = Application.Current.MainWindow;
        if (window is null) return;

        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private async Task MountAllAsync(bool mount)
    {
        foreach (var row in _controller.Mappings.ToList())
        {
            try
            {
                if (mount && row.CanMount) await _controller.MountAsync(row).ConfigureAwait(true);
                else if (!mount && row.CanUnmount) await _controller.UnmountAsync(row).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // One failure must not stop the rest; the row itself shows the error.
                Notify($"'{row.Name}' failed", ex.Message, BalloonIcon.Error);
            }
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var result = await _controller.CheckForUpdateAsync().ConfigureAwait(true);
            if (result is null) return;

            if (!result.UpdateAvailable)
            {
                Notify("CloudDrive is up to date", $"Running version {result.CurrentVersion}.", BalloonIcon.Info);
                return;
            }

            var detail = result.DeferredReason is null
                ? "It will be installed automatically once this machine is idle."
                : result.DeferredReason;
            Notify($"CloudDrive {result.AvailableVersion} is available", detail, BalloonIcon.Info);
        }
        catch (Exception ex)
        {
            Notify("Could not check for updates", ex.Message, BalloonIcon.Warning);
        }
    }

    private void OnStateRefreshed()
    {
        var mounted = _controller.Mappings.Count(m => m.State == MountState.Mounted);
        var failed = _controller.Mappings.Count(m => m.State == MountState.Error);
        var total = _controller.Mappings.Count;

        // The tooltip is the only status indicator visible with the window closed, so it carries the
        // counts rather than just the product name.
        _icon.ToolTipText = total == 0
            ? "CloudDrive — nothing configured"
            : failed > 0
                ? $"CloudDrive — {mounted} of {total} mounted, {failed} failed"
                : $"CloudDrive — {mounted} of {total} mounted";
    }

    private void OnConnectionChanged(bool connected)
    {
        if (!connected)
            _icon.ToolTipText = "CloudDrive — not connected to the service";
    }

    private void OnUpdateAnnounced(UpdateEvent update)
    {
        var (title, message, icon) = update.Stage switch
        {
            "available" => ($"CloudDrive {update.Version} is available",
                "It will be installed automatically once this machine is idle.", BalloonIcon.Info),
            "installing" => ($"Installing CloudDrive {update.Version}",
                "Drives will be briefly unavailable and will come back automatically.", BalloonIcon.Info),
            "failed" => ($"Updating to {update.Version} failed",
                update.Message ?? "The current version is still installed.", BalloonIcon.Error),
            _ => (string.Empty, string.Empty, BalloonIcon.Info),
        };

        if (title.Length > 0) Notify(title, message, icon);
    }

    /// <summary>Shows a balloon, on the UI thread wherever it was raised from.</summary>
    public void Notify(string title, string message, BalloonIcon icon) =>
        Application.Current?.Dispatcher.BeginInvoke(() => _icon.ShowBalloonTip(title, message, icon));

    public void Dispose()
    {
        _controller.StateRefreshed -= OnStateRefreshed;
        _controller.ConnectionChanged -= OnConnectionChanged;
        _controller.UpdateAnnounced -= OnUpdateAnnounced;
        _icon.Dispose();
    }
}
