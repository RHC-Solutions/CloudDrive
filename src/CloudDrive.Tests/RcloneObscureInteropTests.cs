using System.Diagnostics;
using CloudDrive.Core.Mounting;

namespace CloudDrive.Tests;

/// <summary>
/// Verifies <see cref="RcloneObscure"/> against the real <c>rclone obscure</c>, in both directions.
///
/// <para>This is the test that matters for the AES-CTR implementation. CodeQL flags it as
/// <c>cs/ecb-encryption</c> because the code sets <see cref="System.Security.Cryptography.CipherMode.ECB"/>,
/// and reasoning about why that is sound only goes so far — what settles it is that the output is
/// byte-compatible with the tool that has to consume it. If the counter arithmetic, the block ordering,
/// the IV placement or the base64url variant were wrong, these round trips would fail.</para>
///
/// <para>Skipped when <c>third_party\rclone\rclone.exe</c> is absent, so a clone that has not run
/// <c>scripts\fetch-tools.ps1</c> still gets a green suite. Run the script to get the real coverage.</para>
/// </summary>
public class RcloneObscureInteropTests
{
    private static string? FindRclone()
    {
        // Walk up from the test binary to the repository root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "third_party", "rclone", "rclone.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static string RunRclone(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // rclone writes UTF-8. Without forcing it, .NET decodes with the console code page and a
            // revealed non-ASCII password comes back as mojibake — which failed this test while the
            // crypto was perfectly correct. RcloneProcess sets the same two properties for the same
            // reason; the test has to match the product.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("rclone could not be started.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(20_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("rclone did not exit.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"rclone exited {process.ExitCode}: {stderr.Trim()}");

        return stdout.Trim();
    }

    public static TheoryData<string> Passwords() =>
    [
        "hunter2",
        "a",
        "with spaces and symbols !@#$%^&*()_+-=[]{}|;':\",./<>?",
        // Multi-block, to exercise the counter increment across block boundaries.
        new string('x', 64),
        // Non-ASCII, to pin the UTF-8 handling on both sides.
        "пароль-סיסמה-密码-🔐",
    ];

    /// <summary>
    /// Our obscured value must be something real rclone can reveal. This is the direction that
    /// actually matters at runtime: CloudDrive writes the value and rclone reads it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Passwords))]
    public void Rclone_can_reveal_what_we_obscure(string password)
    {
        var rclone = FindRclone();
        if (rclone is null) return; // see the class remarks

        var obscured = RcloneObscure.Obscure(password);
        var revealed = RunRclone(rclone, "reveal", obscured);

        Assert.Equal(password, revealed);
    }

    /// <summary>And we must be able to read what rclone produces, which pins the format both ways.</summary>
    [Theory]
    [MemberData(nameof(Passwords))]
    public void We_can_reveal_what_rclone_obscures(string password)
    {
        var rclone = FindRclone();
        if (rclone is null) return;

        var obscured = RunRclone(rclone, "obscure", password);

        Assert.Equal(password, RcloneObscure.Reveal(obscured));
    }

    /// <summary>
    /// Two obscurings of the same password differ, and rclone reveals both. This is the property the
    /// random IV provides, and the reason the ECB primitive underneath is safe here: the block cipher
    /// only ever sees counter blocks, which are distinct by construction, never the plaintext.
    /// </summary>
    [Fact]
    public void A_fresh_iv_makes_each_obscuring_unique_and_still_valid()
    {
        var rclone = FindRclone();
        if (rclone is null) return;

        var first = RcloneObscure.Obscure("hunter2");
        var second = RcloneObscure.Obscure("hunter2");

        Assert.NotEqual(first, second);
        Assert.Equal("hunter2", RunRclone(rclone, "reveal", first));
        Assert.Equal("hunter2", RunRclone(rclone, "reveal", second));
    }
}
