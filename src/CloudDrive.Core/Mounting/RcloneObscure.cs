using System.Security.Cryptography;
using System.Text;

namespace CloudDrive.Core.Mounting;

/// <summary>
/// Implements rclone's <c>obscure</c> encoding for password config values.
///
/// rclone will not accept a plaintext <c>pass</c>: every backend runs the value through
/// <c>obscure.Reveal</c> first, so a raw password is read as ciphertext and authentication fails
/// with a confusing "base64 decode failed" error. The transformation is AES-CTR under a key
/// compiled into rclone, with a random IV prepended and the result base64url-encoded.
///
/// This is obfuscation, not encryption — rclone's own documentation says so, and anyone with the
/// binary can reverse it. It is reimplemented here rather than shelled out to <c>rclone obscure</c>
/// so the password never appears in a command line (visible in the process list) and never has to
/// be piped through another process's stdio.
/// </summary>
public static class RcloneObscure
{
    /// <summary>The AES-256 key rclone ships in <c>fs/config/obscure/obscure.go</c>.</summary>
    private static readonly byte[] CryptKey =
    {
        0x9c, 0x93, 0x5b, 0x48, 0x73, 0x0a, 0x55, 0x4d,
        0x6b, 0xfd, 0x7c, 0x63, 0xc8, 0x86, 0xa9, 0x2b,
        0xd3, 0x90, 0x19, 0x8e, 0xb8, 0x12, 0x8a, 0xfb,
        0xf4, 0xde, 0x16, 0x2b, 0x8b, 0x95, 0xf6, 0x38,
    };

    private const int BlockSize = 16;

    /// <summary>Encodes <paramref name="plaintext"/> the way <c>rclone obscure</c> does.</summary>
    public static string Obscure(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var iv = RandomNumberGenerator.GetBytes(BlockSize);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = ApplyCtr(data, iv);

        var buffer = new byte[iv.Length + cipher.Length];
        iv.CopyTo(buffer, 0);
        cipher.CopyTo(buffer, iv.Length);
        return Base64UrlEncode(buffer);
    }

    /// <summary>
    /// Decodes an obscured value. Provided so the round trip can be asserted in tests — the app
    /// itself only ever obscures.
    /// </summary>
    public static string Reveal(string obscured)
    {
        ArgumentNullException.ThrowIfNull(obscured);

        var buffer = Base64UrlDecode(obscured);
        if (buffer.Length < BlockSize)
            throw new FormatException("Obscured value is too short to contain an IV.");

        var iv = buffer[..BlockSize];
        var cipher = buffer[BlockSize..];
        return Encoding.UTF8.GetString(ApplyCtr(cipher, iv));
    }

    /// <summary>
    /// AES in counter mode. .NET exposes no CTR primitive, so the keystream is produced by running
    /// the raw block cipher over successive counter blocks and XORing the result with the input.
    /// CTR is symmetric, so this one routine both obscures and reveals.
    ///
    /// <para><b>On the ECB mode set below</b> — static analysis flags <c>CipherMode.ECB</c> on sight,
    /// and normally it should. It is not used to encrypt data here: the only thing it ever
    /// encrypts is the counter block, and the plaintext is never fed to the cipher at all. ECB's
    /// weakness is that equal plaintext blocks yield equal ciphertext blocks; every counter block is
    /// distinct by construction (random IV, then incremented), so that property has nothing to act
    /// on. This is how CTR is built from a block cipher, and there is no alternative on .NET.</para>
    ///
    /// <para>The weakness that <i>is</i> real here has nothing to do with the mode: the key is a
    /// constant compiled into every public rclone binary. Nothing in this file protects anything —
    /// see the type-level remarks. The secrets are protected by DPAPI in
    /// <see cref="CloudDrive.Core.Stores.CredentialStore"/>; this only satisfies rclone's config format.</para>
    /// </summary>
    private static byte[] ApplyCtr(byte[] input, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = CryptKey;
        // Keystream generation, not data encryption — see the remarks above before changing this.
        aes.Mode = CipherMode.ECB; // lgtm[cs/ecb-encryption]
        aes.Padding = PaddingMode.None;

        // ECB keeps no state between blocks, so one encryptor can be reused across the loop. A
        // chaining mode could not be, which is another reason CTR is built on this one.
        using var encryptor = aes.CreateEncryptor();

        var counter = (byte[])iv.Clone();
        var keystream = new byte[BlockSize];
        var output = new byte[input.Length];

        for (var offset = 0; offset < input.Length; offset += BlockSize)
        {
            encryptor.TransformBlock(counter, 0, BlockSize, keystream, 0);
            var count = Math.Min(BlockSize, input.Length - offset);
            for (var i = 0; i < count; i++)
                output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);
            IncrementCounter(counter);
        }

        return output;
    }

    /// <summary>Increments the counter block as one big-endian integer, matching Go's CTR mode.</summary>
    private static void IncrementCounter(byte[] counter)
    {
        for (var i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0) break;
        }
    }

    /// <summary>Base64url without padding (Go's <c>base64.RawURLEncoding</c>).</summary>
    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }
}
