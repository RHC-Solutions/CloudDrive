using System.Diagnostics;
using CloudDrive.Core.Platform;

namespace CloudDrive.Tests;

/// <summary>
/// The <c>sc.exe</c> argument shape, which is fiddlier than it looks and broke service installation
/// completely.
///
/// <para>sc.exe parses its arguments as pairs: a token ending in <c>=</c> names an option and the
/// <i>next</i> token is its value. Its own help says so — "the option name includes the equal sign; a
/// space is required between the equal sign and the value" — but it reads like formatting advice, and
/// writing <c>"start= auto"</c> as a single argument looks like it satisfies it.</para>
///
/// <para>It does not. <see cref="ProcessStartInfo.ArgumentList"/> quotes any argument containing a
/// space, so sc.exe received the single token <c>start= auto</c>, which does not end in <c>=</c>, and
/// answered <c>1639: Invalid start= field</c>. The service could never be registered on any machine.</para>
/// </summary>
public class ServiceControlArgumentTests
{
    private static IReadOnlyList<string> Create() => ServiceControl.BuildInstallArguments(
        "create", "CloudDrive", @"C:\Program Files\CloudDrive\CloudDrive.Service.exe", "CloudDrive mount service");

    [Fact]
    public void The_verb_and_service_name_come_first()
    {
        var args = Create();
        Assert.Equal("create", args[0]);
        Assert.Equal("CloudDrive", args[1]);
    }

    /// <summary>
    /// Every option is its own token and every value is the token after it. This is the assertion that
    /// would have caught the bug.
    /// </summary>
    [Theory]
    [InlineData("binPath=", @"C:\Program Files\CloudDrive\CloudDrive.Service.exe")]
    [InlineData("start=", "delayed-auto")]
    [InlineData("obj=", "LocalSystem")]
    [InlineData("DisplayName=", "CloudDrive mount service")]
    public void Each_option_is_followed_by_its_value_as_a_separate_token(string option, string value)
    {
        var args = Create();

        var index = args.ToList().IndexOf(option);
        Assert.True(index >= 0, $"'{option}' should be a token of its own");
        Assert.Equal(value, args[index + 1]);
    }

    [Fact]
    public void No_token_combines_an_option_with_its_value()
    {
        foreach (var token in Create())
        {
            // "start= auto" or "start=auto" both fail, for different reasons. Neither may appear.
            Assert.False(
                token.Contains("= ", StringComparison.Ordinal),
                $"'{token}' packs an option and value into one argument; ArgumentList will quote it and "
                + "sc.exe will reject it.");

            if (token.EndsWith('=')) continue;
            Assert.False(
                token.Contains('=') && !token.Contains(':') && !token.Contains('\\'),
                $"'{token}' looks like an option glued to its value.");
        }
    }

    /// <summary>
    /// The path is passed raw. Pre-quoting it would embed literal quote characters, and the path is
    /// normally under <c>C:\Program Files\</c> — the one place a mistake here is guaranteed to bite.
    /// </summary>
    [Fact]
    public void The_executable_path_is_not_pre_quoted()
    {
        var path = Create()[Create().ToList().IndexOf("binPath=") + 1];

        Assert.DoesNotContain('"', path);
        Assert.StartsWith(@"C:\Program Files\", path, StringComparison.Ordinal);
    }

    /// <summary>
    /// Delayed start, not plain auto: at boot the network stack is frequently not ready, and a mount
    /// that fails because DNS was not up burns the restart budget before the machine has finished
    /// starting.
    /// </summary>
    [Fact]
    public void The_service_is_registered_for_delayed_auto_start()
    {
        var args = Create();
        Assert.Equal("delayed-auto", args[args.ToList().IndexOf("start=") + 1]);
    }

    [Fact]
    public void It_runs_as_local_system()
    {
        // Anything else cannot create a global drive letter or read the machine credential store.
        var args = Create();
        Assert.Equal("LocalSystem", args[args.ToList().IndexOf("obj=") + 1]);
    }

    /// <summary>
    /// Demonstrates the quoting behaviour that caused the failure, rather than asserting it from
    /// memory: a value with a space really is wrapped in quotes and really does arrive as one token.
    /// </summary>
    [Fact]
    public void ArgumentList_quotes_a_token_containing_a_space()
    {
        var joined = Join("start= auto");
        Assert.Equal("\"start= auto\"", joined);

        // Whereas the two-token form passes through untouched.
        Assert.Equal("start= delayed-auto", Join("start=", "delayed-auto"));
    }

    /// <summary>
    /// Renders arguments the way .NET will hand them to a child process, by asking cmd.exe to echo
    /// them back.
    /// </summary>
    private static string Join(params string[] arguments)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("echo");
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(10_000);
        return output;
    }
}
