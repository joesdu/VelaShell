namespace PulseTerm.Terminal.Emulation;

/// <summary>
/// Determines the display width (in terminal cells) of a Unicode scalar value.
/// Follows the Unicode East Asian Width conventions used by wcwidth: wide and fullwidth
/// characters occupy two cells, combining marks and zero-width characters occupy none,
/// everything else occupies one.
/// </summary>
public static class CharWidth
{
    // Ranges of wide (W) and fullwidth (F) code points. Kept compact but covers the
    // scripts a terminal realistically renders: CJK, Hangul, Kana, fullwidth forms, emoji.
    private static readonly (int Lo, int Hi)[] Wide =
    {
        (0x1100, 0x115F),   // Hangul Jamo
        (0x2329, 0x232A),   // angle brackets
        (0x2E80, 0x303E),   // CJK radicals, Kangxi, symbols
        (0x3041, 0x33FF),   // Hiragana .. CJK compatibility
        (0x3400, 0x4DBF),   // CJK Ext A
        (0x4E00, 0x9FFF),   // CJK Unified
        (0xA000, 0xA4CF),   // Yi
        (0xAC00, 0xD7A3),   // Hangul Syllables
        (0xF900, 0xFAFF),   // CJK compatibility ideographs
        (0xFE10, 0xFE19),   // vertical forms
        (0xFE30, 0xFE6F),   // CJK compatibility / small forms
        (0xFF00, 0xFF60),   // fullwidth forms
        (0xFFE0, 0xFFE6),   // fullwidth signs
        (0x1F300, 0x1F64F),  // emoji / pictographs
        (0x1F900, 0x1F9FF),  // supplemental symbols
        (0x20000, 0x3FFFD),  // CJK Ext B+ / supplementary ideographic plane
    };

    // Zero-width: combining marks, format chars, variation selectors.
    private static readonly (int Lo, int Hi)[] ZeroWidth =
    {
        (0x0300, 0x036F),   // combining diacriticals
        (0x0483, 0x0489),
        (0x0591, 0x05BD),
        (0x0610, 0x061A),
        (0x064B, 0x065F),
        (0x0670, 0x0670),
        (0x06D6, 0x06DC),
        (0x0E31, 0x0E31),
        (0x0E34, 0x0E3A),
        (0x1AB0, 0x1AFF),
        (0x1DC0, 0x1DFF),
        (0x200B, 0x200F),   // ZWSP .. RLM
        (0x20D0, 0x20FF),   // combining marks for symbols
        (0xFE00, 0xFE0F),   // variation selectors
        (0xFE20, 0xFE2F),   // combining half marks
    };

    public static int Of(int rune)
    {
        if (rune == 0)
            return 0;

        // C0/C1 control characters have no printable width.
        if (rune < 0x20 || (rune >= 0x7F && rune < 0xA0))
            return 0;

        if (InRanges(rune, ZeroWidth))
            return 0;

        if (InRanges(rune, Wide))
            return 2;

        return 1;
    }

    public static bool IsCombining(int rune) => rune != 0 && InRanges(rune, ZeroWidth);

    private static bool InRanges(int rune, (int Lo, int Hi)[] ranges)
    {
        int lo = 0, hi = ranges.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            ref readonly var r = ref ranges[mid];
            if (rune < r.Lo)
                hi = mid - 1;
            else if (rune > r.Hi)
                lo = mid + 1;
            else
                return true;
        }
        return false;
    }
}
