using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NexaVerify.Application.Identity;

/// <summary>RFC 4648 base32 (upper-case, unpadded) for authenticator-app secrets.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    public static byte[]? Decode(string text)
    {
        var result = new List<byte>(text.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var raw in text)
        {
            if (raw is ' ' or '-' or '=')
            {
                continue;
            }

            var index = Alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (index < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                result.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. result];
    }
}

/// <summary>
/// TOTP (RFC 6238) with the parameters every authenticator app supports: HMAC-SHA1, 30-second steps, 6 digits. The window is
/// ±1 step (clock drift). <see cref="TryMatch"/> returns WHICH step matched so the caller can refuse a replay of the same code.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int SecretBytes = 20; // 160 bits, the RFC 4226 recommendation

    public static long StepAt(DateTime utcNow) => new DateTimeOffset(utcNow, TimeSpan.Zero).ToUnixTimeSeconds() / StepSeconds;

    public static string Compute(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
#pragma warning disable CA5350 // HMAC-SHA1 is mandated by RFC 4226/6238 and by authenticator apps; the key is secret so collision attacks do not apply.
        HMACSHA1.HashData(secret, counter, hash);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The matching step within ±<paramref name="window"/> steps of <paramref name="now"/>, or null. All candidates are always evaluated (no early exit on the first hit).</summary>
    public static long? TryMatch(ReadOnlySpan<byte> secret, string code, DateTime now, int window = 1)
    {
        if (code.Length != Digits || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        var current = StepAt(now);
        long? matched = null;
        var supplied = Encoding.ASCII.GetBytes(code);
        for (var offset = -window; offset <= window; offset++)
        {
            var step = current + offset;
            if (CryptographicOperations.FixedTimeEquals(supplied, Encoding.ASCII.GetBytes(Compute(secret, step))))
            {
                matched = step;
            }
        }

        return matched;
    }
}
