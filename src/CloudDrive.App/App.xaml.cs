using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using CloudDrive.App.Services;
using CloudDrive.App.Views;
using CloudDrive.Core.Platform;

namespace CloudDrive.App;

[SupportedOSPlatform("windows")]
public partial class App : Application
{
    /// <summary>
    /// Only one tray app per session. A second instance would register the same Cloud Files sync
    /// roots and fight the first for them, which ends with placeholders that hydrate from whichever
    /// process won the race. Scoped to the session (Local\) rather than globally, because a second
    /// signed-in user running their own copy is perfectly legitimate.
    /// </summary>
    private const string InstanceMutexName = @"Local\CloudDrive.App.SingleInstance";

    private Mutex? _instanceMutex;
    private AppController? _controller;
    private TrayIcon? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Administrative verbs the unelevated UI relaunches itself to perform.
        if (e.Args.Contains("--install-service", StringComparer.OrdinalIgnoreCase))
        {
            RunServiceInstall();
            Shutdown();
            return;
        }

        // The elevated half of "Install now" in the About window. A standard user cannot ask the
        // service to replace machine-wide files, so the app relaunches itself with this switch and the
        // elevated instance makes the request.
        if (e.Args.Contains("--install-update", StringComparer.OrdinalIgnoreCase))
        {
            await RunUpdateInstallAsync().ConfigureAwait(true);
            Shutdown();
            return;
        }

        // Loads every window and exits with the number that failed. Run by the installer build, because
        // XAML faults appear at load time and a compiling build says nothing about them.
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            var index = Array.FindIndex(e.Args, a =>
                string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));
            var reportPath = index >= 0 && index + 1 < e.Args.Length && !e.Args[index + 1].StartsWith('-')
                ? e.Args[index + 1]
                : null;

            Shutdown(WindowSelfTest.Run(reportPath));
            return;
        }

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            // A second launch means the user clicked the shortcut again expecting the window; the
            // running instance cannot be signalled without more plumbing than this warrants, so say
            // so rather than silently doing nothing.
            MessageBox.Show(
                "CloudDrive is already running. Look for the CloudDrive icon in the notification area.",
                "CloudDrive", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        if (OsCapabilities.IsServerCore)
        {
            MessageBox.Show(
                "This is Windows Server Core, which has no desktop shell, so the CloudDrive window "
                + "cannot run here. Use the command line instead: cdrive --help.",
                "CloudDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        _controller = new AppController();
        _tray = new TrayIcon(_controller);

        var window = new MainWindow(_controller);
        MainWindow = window;
        window.Show();

        var failure = await _controller.ConnectAsync().ConfigureAwait(true);
        if (failure is null)
        {
            await _controller.LoadLogTailAsync().ConfigureAwait(true);
            await _controller.AutoMountAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Asks the service to apply the pending update, from an elevated process.
    ///
    /// Runs without a main window: this instance exists only to carry the elevated request, and the
    /// service restarts the tray app itself once the installer has finished.
    /// </summary>
    private static async Task RunUpdateInstallAsync()
    {
        var controller = new AppController();
        try
        {
            var failure = await controller.ConnectAsync().ConfigureAwait(true);
            if (failure is not null)
            {
                MessageBox.Show($"CloudDrive could not reach its service to install the update.\n\n{failure}",
                    "CloudDrive", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            await controller.InstallUpdateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Installing the update failed.\n\n{ex.Message}",
                "CloudDrive", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            await controller.DisposeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The elevated half of "install the service": the process was relaunched with runas purely to
    /// perform this one step, and exits immediately afterwards.
    /// </summary>
    private static void RunServiceInstall()
    {
        try
        {
            var exe = ServiceControl.ResolveServiceExe()
                ?? throw new FileNotFoundException(
                    "CloudDrive.Service.exe was not found next to the application.");

            ServiceControl.Install(exe);
            ServiceControl.Start(TimeSpan.FromSeconds(45));

            MessageBox.Show(
                "The CloudDrive service is installed and running. It will start automatically at boot.",
                "CloudDrive", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Installing the CloudDrive service failed.\n\n{ex.Message}",
                "CloudDrive", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();

        if (_controller is not null)
        {
            // Block briefly on teardown so on-demand sync roots are disconnected before the process
            // goes. A sync root left connected shows placeholders that can never hydrate.
            try { _controller.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)); }
            catch { /* exiting anyway */ }
        }

        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}
