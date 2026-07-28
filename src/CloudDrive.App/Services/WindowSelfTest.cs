using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Windows;
using CloudDrive.App.Views;
using CloudDrive.Core.Models;

namespace CloudDrive.App.Services;

/// <summary>
/// Constructs every window headlessly and reports which ones fail to load.
///
/// <para><b>Why this exists.</b> A green build proves almost nothing about WPF. XAML resource lookups,
/// style targets, converter references and binding expressions are resolved when a window loads, not
/// when it compiles — so a typo in a resource key, or a framework setting that breaks binding, produces
/// an application that builds cleanly, passes every unit test, and then dies the instant it opens a
/// window.</para>
///
/// <para>That is not hypothetical. Setting <c>InvariantGlobalization</c> made every window throw
/// <c>Cannot find non-neutral culture related to 'en-us'</c> from inside WPF's binding engine, and it
/// shipped in two releases because nothing between the compiler and a human double-clicking the icon
/// ever opened a window. <c>scripts\build-installer.ps1</c> now runs this and refuses to package a
/// build that fails it.</para>
///
/// <para>Windows are constructed but never shown, so this works over RDP and in CI. It deliberately
/// does <b>not</b> require the service: the controller is left unconnected, which also checks that the
/// UI copes with the service being unavailable — a state a user hits whenever it is stopped.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowSelfTest
{
    /// <summary>Runs the test, writing a report to <paramref name="reportPath"/> when given.</summary>
    /// <returns>0 when every window loaded, otherwise the number that failed.</returns>
    public static int Run(string? reportPath)
    {
        var report = new StringBuilder();
        var failures = 0;

        report.AppendLine($"CloudDrive window self-test — {Core.Tooling.UpdateService.CurrentVersion}");
        report.AppendLine($"culture: {System.Globalization.CultureInfo.CurrentCulture.Name}");
        report.AppendLine();

        // An unconnected controller. Every window has to tolerate this, because it is exactly the state
        // when the service is stopped.
        var controller = new AppController();

        var account = new Account { Name = "Self-test", Provider = ProviderId.Wasabi, RegionCode = "us-east-1" };
        var mapping = new Mapping { Name = "Self-test", AccountId = account.Id, Container = "bucket" };

        // Each entry is constructed inside its own guard, so one broken window does not hide the rest.
        var cases = new (string Name, Func<Window> Create)[]
        {
            ("MainWindow", () => new MainWindow(controller)),
            ("AboutWindow", () => new AboutWindow(controller)),
            ("SettingsWindow", () => new SettingsWindow(controller)),
            ("AccountsWindow", () => new AccountsWindow(controller)),
            ("AccountEditWindow (new)", () => new AccountEditWindow(controller, null)),
            ("AccountEditWindow (edit)", () => new AccountEditWindow(controller, account)),
            ("MappingEditWindow (new)", () => new MappingEditWindow(controller, null)),
            ("MappingEditWindow (edit)", () => new MappingEditWindow(controller, mapping)),
            ("NotificationTargetWindow (new)", () => new NotificationTargetWindow(controller, null)),
            ("NotificationTargetWindow (edit)", () => new NotificationTargetWindow(controller,
                new NotificationTarget { Name = "Ops", Kind = NotificationChannelKind.Telegram })),
        };

        foreach (var (name, create) in cases)
        {
            try
            {
                var window = create();

                // Constructing is not enough on its own: some binding and template failures only
                // surface once the visual tree is built. Measure forces that without showing anything.
                window.Measure(new Size(1024, 768));

                window.Close();
                report.AppendLine($"  ok    {name}");
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine($"  FAIL  {name}");
                report.AppendLine($"        {ex.GetType().Name}: {ex.Message}");

                // The inner exception is where the real cause lives for a XamlParseException.
                for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                    report.AppendLine($"        --> {inner.GetType().Name}: {inner.Message}");
            }
        }

        report.AppendLine();
        report.AppendLine(failures == 0
            ? $"All {cases.Length} windows loaded."
            : $"{failures} of {cases.Length} windows failed to load.");

        var text = report.ToString();
        Console.Write(text);

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            try { File.WriteAllText(reportPath!, text); }
            catch (Exception ex) { Console.Error.WriteLine($"Could not write the report: {ex.Message}"); }
        }

        return failures;
    }
}
