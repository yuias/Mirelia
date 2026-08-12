using System.Security.Cryptography;
using System.Text;

namespace Mirelia.Library.Sync;

/// <summary>
/// Turns arbitrary tag text into one path component a FAT32/exFAT card will
/// accept. Every rule here exists because some device rejects the input
/// otherwise; see docs/sync-protocol.md for the list.
/// </summary>
public static class NameSanitizer
{
    /// <summary>Per-component character budget before truncation kicks in.</summary>
    public const int MaxComponentLength = 60;

    /// <summary>Substituted when sanitization leaves nothing behind.</summary>
    public const string EmptyPlaceholder = "_";

    private const string ReservedChars = "\\/:*?\"<>|";
    private const string FullWidthChars = "＼／：＊？＂＜＞｜";

    // Lowercase, and without i/l/o/u, so short ids cannot collide on a
    // case-insensitive filesystem or be misread off a device screen.
    private const string Base32Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    private static readonly string[] DeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Sanitizes one path component. <paramref name="extension"/> (with its
    /// leading dot, or empty) is preserved intact and excluded from the
    /// length budget, so truncation never eats the file type.
    /// </summary>
    public static string Component(string raw, ReservedCharMode mode, string extension = "")
    {
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            int reserved = ReservedChars.IndexOf(c);
            if (reserved >= 0)
            {
                sb.Append(mode == ReservedCharMode.FullWidth ? FullWidthChars[reserved] : '_');
            }
            else if (char.IsControl(c))
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        // Trailing dots and spaces are silently dropped by Windows, which
        // would make the stored path disagree with what is on the card. A
        // leading space survives but comes only from a token that expanded to
        // nothing, so it is noise either way.
        string name = sb.ToString().TrimEnd('.', ' ').TrimStart(' ');

        if (name.Length > MaxComponentLength)
        {
            // The hash is over the pre-truncation text, so two long names
            // sharing a prefix stay distinct.
            name = string.Concat(
                name.AsSpan(0, MaxComponentLength).TrimEnd(" ."),
                " ~",
                ShortHash(name, 4));
        }

        if (name.Length == 0)
        {
            name = EmptyPlaceholder;
        }
        else if (IsDeviceName(name))
        {
            name += "_";
        }

        return name + extension;
    }

    /// <summary>
    /// Stable short identifier in the sanitizer's base32 alphabet. Used for
    /// truncation suffixes and for collision suffixes derived from row ids.
    /// </summary>
    public static string ShortId(long value, int length)
    {
        // Unsigned so negative ids (which cannot occur, but cost nothing to
        // handle) do not produce a shorter or sign-dependent string.
        ulong v = unchecked((ulong)value);
        var chars = new char[length];
        for (int i = length - 1; i >= 0; i--)
        {
            chars[i] = Base32Alphabet[(int)(v & 31)];
            v >>= 5;
        }

        return new string(chars);
    }

    private static string ShortHash(string text, int length)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = Base32Alphabet[digest[i] & 31];
        }

        return new string(chars);
    }

    private static bool IsDeviceName(string name)
    {
        // A reserved name is still reserved with an extension ("NUL.mp3").
        int dot = name.IndexOf('.');
        ReadOnlySpan<char> stem = dot < 0 ? name : name.AsSpan(0, dot);
        foreach (string device in DeviceNames)
        {
            if (stem.Equals(device, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
