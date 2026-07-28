using CloudDrive.Core.Platform;
using CloudDrive.Core.Stores;
using CloudDrive.Ipc;
using CloudDrive.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Host for the CloudDrive Windows service.
//
// Runs as LocalSystem for two reasons. Its mounts must exist before anyone signs in, and a mount
// point created by SYSTEM lands in the global namespace, so both drive letters and directory
// mountpoints are visible from every interactive session rather than only from session 0.
//
// It can also be run straight from a console for troubleshooting, which is considerably easier than
// attaching a debugger to a service.

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = ServiceControl.ServiceName);
builder.Services.AddHostedService<ServiceWorker>();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// The Event Log is where an administrator actually looks when mounts are missing, and it is the only
// sink that works before the machine store exists.
builder.Logging.AddEventLog(settings => settings.SourceName = ServiceControl.ServiceName);

builder.Logging.SetMinimumLevel(LogLevel.Information);

// Preflight before the host starts. Everything below depends on the machine store, and a failure
// here surfaces from deep inside a BackgroundService as an unhandled exception and a stack trace —
// which is what happened before this check existed. Reported once, in plain language, and the
// worker's own EnsureMachineStore is left to do the actual creation.
if (AppPaths.DescribeMachineStoreProblem() is { } problem)
{
    Console.Error.WriteLine(problem);
    return 1;
}

if (AppPaths.MachineDirIsRedirected)
{
    Console.WriteLine(
        $"Using a redirected configuration directory: {AppPaths.MachineDir}\n"
        + $"({AppPaths.DataDirVariable} is set. Unset it to use the real machine store.)");
}

// Prove the IPC wire format actually round-trips before opening the pipe.
//
// This exists because of a failure that was very hard to diagnose from its symptoms: enum values are
// sent as names, parsing one goes through a System.Text.Json path that pulls in
// System.Text.RegularExpressions, and when that assembly could not be loaded the service accepted
// every connection and then dropped it with nothing useful logged. Every client reported only "the
// connection was lost". Failing loudly here, once, beats being mysteriously unreachable.
if (IpcSelfCheck.Describe() is { } wireProblem)
{
    Console.Error.WriteLine(wireProblem);
    return 1;
}

var host = builder.Build();
await host.RunAsync();
return 0;
