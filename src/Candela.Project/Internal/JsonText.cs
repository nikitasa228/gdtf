using System.Text;
using System.Text.Unicode;

namespace Candela.Project.Internal;

/// <summary>
/// Repairs JSON text that System.Text.Json accepts when parsing but cannot turn into .NET strings: byte sequences that are
/// not valid UTF-8, and escaped UTF-16 surrogates that do not form a pair (<c>"\ud800"</c>). Such text parses without an
/// error; the failure comes later, as an <see cref="InvalidOperationException"/> from whatever reads a string or property
/// name (or writes the value again), far from the code that expects <c>JsonException</c>. Candela's own writer never
/// produces either (the serializer writes U+FFFD for a lone surrogate), so they only come from damaged, hand-edited or
/// foreign files and from raw JSON handed to <see cref="ProjectSettings"/>. Both are replaced by U+FFFD, which is exactly
/// what the serializer would have written.
/// </summary>
internal static class JsonText
{
    private const char Replacement = '�';

    /// <summary>
    /// Returns <paramref name="utf8"/> itself when it is clean, otherwise a repaired copy; <paramref name="repairs"/> is
    /// the number of characters that were replaced. One pass over the bytes; nothing is allocated for clean text.
    /// </summary>
    public static byte[] Repair(byte[] utf8, out int repairs)
    {
        repairs = 0;
        var bytes = utf8;
        if (!Utf8.IsValid(bytes))
        {
            // The UTF-8 decoder replaces every invalid sequence with U+FFFD; count the ones it introduced.
            var text = Encoding.UTF8.GetString(bytes);
            repairs += System.Math.Max(1, Count(text, Replacement) - CountEncodedReplacements(bytes));
            bytes = Encoding.UTF8.GetBytes(text);
        }

        bool copied = !ReferenceEquals(bytes, utf8);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\\') continue;
            if (i + 1 >= bytes.Length) break;
            if (bytes[i + 1] != (byte)'u' || !TryHex4(bytes, i + 2, out int unit))
            {
                i++; // a two-character escape such as \\ or \" (skip the escaped character)
                continue;
            }
            if (unit is >= 0xD800 and <= 0xDBFF && TryHex4Escape(bytes, i + 6, out int low) && low is >= 0xDC00 and <= 0xDFFF)
            {
                i += 11; // a valid pair: 😀
                continue;
            }
            if (unit is >= 0xD800 and <= 0xDFFF)
            {
                if (!copied)
                {
                    bytes = (byte[])bytes.Clone();
                    copied = true;
                }
                // "�" has the same length as the escape it replaces, so offsets stay valid.
                bytes[i + 2] = (byte)'F';
                bytes[i + 3] = (byte)'F';
                bytes[i + 4] = (byte)'F';
                bytes[i + 5] = (byte)'D';
                repairs++;
            }
            i += 5;
        }
        return bytes;
    }

    private static bool TryHex4Escape(byte[] bytes, int start, out int value)
    {
        value = 0;
        return start + 6 <= bytes.Length && bytes[start] == (byte)'\\' && bytes[start + 1] == (byte)'u' && TryHex4(bytes, start + 2, out value);
    }

    private static bool TryHex4(byte[] bytes, int start, out int value)
    {
        value = 0;
        if (start + 4 > bytes.Length) return false;
        for (int k = 0; k < 4; k++)
        {
            int b = bytes[start + k];
            int digit = b switch
            {
                >= '0' and <= '9' => b - '0',
                >= 'a' and <= 'f' => b - 'a' + 10,
                >= 'A' and <= 'F' => b - 'A' + 10,
                _ => -1,
            };
            if (digit < 0) return false;
            value = (value << 4) | digit;
        }
        return true;
    }

    private static int Count(string text, char c)
    {
        int n = 0;
        foreach (var ch in text)
            if (ch == c) n++;
        return n;
    }

    /// <summary>Number of correctly encoded U+FFFD characters (EF BF BD) already present in the input.</summary>
    private static int CountEncodedReplacements(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> pattern = [0xEF, 0xBF, 0xBD];
        int n = 0;
        int i;
        while ((i = bytes.IndexOf(pattern)) >= 0)
        {
            n++;
            bytes = bytes[(i + pattern.Length)..];
        }
        return n;
    }
}
