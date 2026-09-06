namespace AramOverlay.Core;

/// <summary>Text shaping shared by the augment and item name matchers.</summary>
public static class Hangul
{
    /// <summary>OCR drops and mangles spaces constantly, so they never count.</summary>
    public static string Squash(string s)
    {
        Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        int n = 0;
        foreach (char c in s)
        {
            if (!char.IsWhiteSpace(c))
                buf[n++] = c;
        }
        return new string(buf[..n]);
    }

    /// <summary>
    /// Drop the final consonant from every Hangul syllable.
    ///
    /// The Windows recogniser sometimes loses every 받침 in a line at once,
    /// keeping the syllable count and order: 끝없는 학살 comes back as 끄어느하사,
    /// 범람 as 버라. Compared as written those score 0.25 and 0.40 and match the
    /// wrong augment; compared with finals removed on both sides they are exact.
    /// Used as a second opinion, never a replacement -- it collapses real
    /// distinctions too, so it may only ever raise a score.
    /// </summary>
    public static string StripFinal(string s)
    {
        Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            int code = s[i];
            buf[i] = code is >= 0xAC00 and <= 0xD7A3
                ? (char)(0xAC00 + (code - 0xAC00) / 28 * 28)
                : s[i];
        }
        return new string(buf[..s.Length]);
    }
}
