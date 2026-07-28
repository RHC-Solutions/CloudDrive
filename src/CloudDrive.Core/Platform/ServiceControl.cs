using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace CloudDrive.Core.Platform;

public enum ServiceState
{
    NotInstalled,
    Stopped,
    Running,
    Pending,
    Unknown,
}

/// <summary>
/// Installs, removes and controls the CloudDrive Windows service.
///
/// Creation and deletion go through <c>sc.exe</c> because .NET has no supported in-process
/// equivalent — <c>ServiceInstaller</c> did not come across to modern .NET, and P/Invoking
/// <c>CreateService</c> buys nothing over the tool Windows already ships. Querying and start/stop
/// use <see cref="ServiceController"/>, which does have a clean managed API.
///
/// Every mutating call needs an elevated process.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceControl
{
    public const string ServiceName = "CloudDrive";

    public const string DisplayName = "CloudDrive mount service";

    private const string Description =
        "Mounts CloudDrive storage as Windows drives, independently of any signed-in user, and sends alerts.";

    public const string ServiceExeName = "CloudDrive.Service.exe";

    /// <summary>Finds the service host next to the running app, or null when it is not deployed.</summary>
    public static string? ResolveServiceExe(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;

        var appDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDir, ServiceExeName),
            Path.Combine(appDir, "service", ServiceExeName),
            // Development layout: the service project's own build output.
            Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..",
                "CloudDrive.Service", "bin", "Debug", "net10.0-windows", ServiceExeName)),
            Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..",
                "CloudDrive.Service", "bin", "Release", "net10.0-windows", ServiceExeName)),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static ServiceState GetState()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return controller.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.StopPending
                    or ServiceControllerStatus.ContinuePending or ServiceControllerStatus.PausePending
                    => ServiceState.Pending,
                _ => ServiceState.Unknown,
            };
        }
        catch (InvalidOperationException)
        {
            // ServiceController wraps ERROR_SERVICE_DOES_NOT_EXIST in this rather than returning a status.
            return ServiceState.NotInstalled;
        }
    }

    public static bool IsInstalled() => GetState() != ServiceState.NotInstalled;

    /// <summary>
    /// Registers the service to run as LocalSystem at boot. Re-running against an existing
    /// installation reconfigures it rather than failing, so an upgrade that moved the executable
    /// repairs itself.
    /// </summary>
    public static void Install(string serviceExePath)
    {
        if (string.IsNullOrWhiteSpace(serviceExePath) || !File.Exists(serviceExePath))
            throw new FileNotFoundException("The service executable was not found.", serviceExePath);

        // sc.exe is fussy: binPath= needs the space after '=' and the value quoted.
        var verb = IsInstalled() ? "config" : "create";
        RunSc(verb, ServiceName,
            $"binPath= \"{serviceExePath}\"",
            "start= auto",
            "obj= LocalSystem",
            $"DisplayName= \"{DisplayName}\"");

        // Cosmetic; a failure here should not fail the install.
        try { RunSc("description", ServiceName, $"\"{Description}\""); } catch { /* ignore */ }

        // Restart after a crash rather than leaving every mount down until someone notices.
        try
        {
            RunSc("failure", ServiceName,
                "reset= 86400", "actions= restart/5000/restart/15000/restart/60000");
        }
        catch { /* ignore */ }

        // Delayed auto-start: at boot the network stack is often not ready, and a mount that fails
        // because DNS was not up yet burns the restart budget before the machine has finished
        // starting. The reconciler would recover, but starting late avoids the noise entirely.
        try { RunSc("config", ServiceName, "start= delayed-auto"); } catch { /* ignore */ }
    }

    public static void Uninstall()
    {
        if (!IsInstalled()) return;
        try { Stop(TimeSpan.FromSeconds(30)); } catch { /* delete anyway */ }
        RunSc("delete", ServiceName);
    }

    public static void Start(TimeSpan timeout)
    {
        using var controller = new ServiceController(ServiceName);
        if (controller.Status == ServiceControllerStatus.Running) return;
        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    public static void Stop(TimeSpan timeout)
    {
        using var controller = new ServiceController(ServiceName);
        if (controller.Status == ServiceControllerStatus.Stopped) return;
        controller.Stop();
        controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    public static void Restart(TimeSpan timeout)
    {
        try { Stop(timeout); } catch { /* may already be stopped */ }
        Start(timeout);
    }

    /// <summary>Relaunches the current executable elevated, to run one administrative verb.</summary>
    public static bool RelaunchElevated(string arguments)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return false;

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                // "runas" is what raises the UAC prompt, and it requires ShellExecute.
                UseShellExecute = true,
                Verb = "runas",
            });
            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user dismissed the UAC prompt. Not an error worth throwing over.
            return false;
        }
    }

    private static void RunSc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("sc.exe could not be started.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode == 0) return;

        var detail = string.Join(" ", new[] { stdout, stderr }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim()));
        throw new InvalidOperationException($"sc.exe {args[0]} failed (exit {process.ExitCode}). {detail}".Trim());
    }
}
