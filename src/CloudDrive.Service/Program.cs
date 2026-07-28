using CloudDrive.Core.Platform;
using CloudDrive.Core.Stores;
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

var host = builder.Build();

// Creating the Event Log source needs administrator rights the first time. The installer does it,
// but a developer running this from a console has not, and the resulting exception would be thrown
// from inside the logging infrastructure with no useful message.
try
{
    AppPaths.EnsureMachineStore();
}
catch (UnauthorizedAccessException)
{
    Console.Error.WriteLine(
        $"CloudDrive cannot write to {AppPaths.MachineDir}. Run this elevated, or install the service "
        + "with 'cdrive service install' from an elevated prompt.");
    return 1;
}

await host.RunAsync();
return 0;
