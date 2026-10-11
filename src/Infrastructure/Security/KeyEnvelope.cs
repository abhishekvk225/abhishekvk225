using System.Text;

namespace NexaVerify.Infrastructure.Security;

/// <summary>
/// Self-describing header for platform-protected payloads: <c>magic(4) | idLength(1) | keyId(ascii) | body</c>. The key id is also part of
/// the authenticated data of the body, so the header cannot be edited to point a ciphertext at a different key. Payloads written before
/// the keyring existed have no header and belong to the legacy key.
/// </summary>
public static class KeyEnvelope
{
    private static readonly byte[] Magic = [0x4E, 0x58, 0x45, 0x01]; // "NXE" + format version 1

    public const int MaxKeyIdLength = KeyringConfiguration.MaxKeyIdLength;

    public static int HeaderLength(string keyId) => Magic.Length + 1 + keyId.Length;

    /// <summary>Writes the header for <paramref name="keyId"/> at the start of <paramref name="destination"/>; returns the header length.</summary>
    public static int WriteHeader(string keyId, Span<byte> destination)
    {
        if (keyId.Length is 0 or > MaxKeyIdLength || !keyId.All(IsIdChar))
        {
            throw new ArgumentException("The key id is not a valid keyring id.", nameof(keyId));
        }

        Magic.CopyTo(destination);
        destination[Magic.Length] = (byte)keyId.Length;
        Encoding.ASCII.GetBytes(keyId, destination[(Magic.Length + 1)..]);
        return HeaderLength(keyId);
    }

    /// <summary>True when the payload starts with a well-formed header; <paramref name="bodyOffset"/> is where the sealed body begins.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> payload, out string keyId, out int bodyOffset)
    {
        keyId = string.Empty;
        bodyOffset = 0;
        if (payload.Length < Magic.Length + 2 || !payload[..Magic.Length].SequenceEqual(Magic))
        {
            return false;
        }

        var length = payload[Magic.Length];
        if (length is 0 or > MaxKeyIdLength || payload.Length < Magic.Length + 1 + length)
        {
            return false;
        }

        var idBytes = payload.Slice(Magic.Length + 1, length);
        foreach (var b in idBytes)
        {
            if (!IsIdChar((char)b) || b > 0x7F)
            {
                return false;
            }
        }

        keyId = Encoding.ASCII.GetString(idBytes);
        bodyOffset = Magic.Length + 1 + length;
        return true;
    }

    private static bool IsIdChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';
}
