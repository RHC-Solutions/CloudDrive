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

        var verb = IsInstalled() ? "config" : "create";
        RunSc([.. BuildInstallArguments(verb, ServiceName, serviceExePath, DisplayName)]);

        // Cosmetic; a failure here should not fail the install.
        try { RunSc("description", ServiceName, Description); } catch { /* ignore */ }

        // Restart after a crash rather than leaving every mount down until someone notices.
        try
        {
            RunSc("failure", ServiceName,
                "reset=", "86400",
                "actions=", "restart/5000/restart/15000/restart/60000");
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Builds the <c>sc.exe</c> token list for creating or reconfiguring the service.
    ///
    /// <para><b>Each option name and its value must be separate tokens.</b> sc.exe parses its arguments
    /// as pairs: a token ending in <c>=</c> names an option and the <i>next</i> token is its value. The
    /// familiar "a space is required after the equals sign" advice is describing exactly that, not a
    /// quirk of formatting.</para>
    ///
    /// <para>Passing <c>"start= auto"</c> as one argument therefore fails, and fails obscurely: .NET sees
    /// a value containing a space, quotes it, and sc.exe receives the single token <c>start= auto</c> and
    /// answers <c>1639, Invalid start= field</c>. That is what happened — the service could never be
    /// installed at all, and because setup ignored the exit code, it looked like it had worked.</para>
    ///
    /// <para>Values are also passed raw rather than pre-quoted, because
    /// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> quotes what needs quoting. Adding
    /// quotes by hand would embed them literally, which matters here: the path is normally under
    /// <c>C:\Program Files\</c> and contains a space.</para>
    ///
    /// <para>Kept pure and internal so the token shape can be unit-tested without registering anything.</para>
    /// </summary>
    internal static IReadOnlyList<string> BuildInstallArguments(
        string verb, string serviceName, string exePath, string displayName) =>
    [
        verb,
        serviceName,
        "binPath=", exePath,
        // delayed-auto rather than auto: at boot the network stack is often not ready, and a mount that
        // fails because DNS was not up yet burns the restart budget before the machine has finished
        // starting. The reconciler would recover, but starting late avoids the noise entirely.
        //
        // Set here rather than by a follow-up "sc config start= delayed-auto" call, which is what this
        // used to do — one command that gets it right beats two where the second can fail silently.
        "start=", "delayed-auto",
        "obj=", "LocalSystem",
        "DisplayName=", displayName,
    ];

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
