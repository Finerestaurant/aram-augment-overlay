namespace AramOverlay.Core;

/// <summary>
/// Python's <c>difflib.SequenceMatcher.ratio()</c>, reimplemented exactly.
///
/// This is not a place to substitute a nicer string metric. Every OCR threshold
/// in <see cref="Config"/> was measured against this specific algorithm --
/// OCR_MIN_SCORE 0.60 sits in a gap between a confirmed 0.67 read and a wrong
/// 0.50 one -- so a different similarity function silently invalidates all of
/// them. SelfTest checks this against the Python implementation directly.
///
/// Ratcliff/Obershelp: find the longest matching block, then recurse into what
/// is left either side of it, and score 2*matched/(len(a)+len(b)). The autojunk
/// heuristic Python applies to sequences of 200 or more elements is not
/// implemented, because augment names never come close to that.
/// </summary>
public static class Difflib
{
    public static double Ratio(string a, string b)
    {
        int total = a.Length + b.Length;
        if (total == 0)
            return 1.0;                       // Python's _calculate_ratio, same case
        return 2.0 * MatchedCount(a, b) / total;
    }

    private static int MatchedCount(string a, string b)
    {
        // b2j: where each character occurs in b, in order -- Python's index of
        // the second sequence.
        var b2j = new Dictionary<char, List<int>>();
        for (int j = 0; j < b.Length; j++)
        {
            if (!b2j.TryGetValue(b[j], out var list))
                b2j[b[j]] = list = new List<int>();
            list.Add(j);
        }

        int matched = 0;
        var queue = new Stack<(int alo, int ahi, int blo, int bhi)>();
        queue.Push((0, a.Length, 0, b.Length));
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var (i, j, k) = LongestMatch(a, b, b2j, alo, ahi, blo, bhi);
            if (k == 0)
                continue;
            matched += k;
            if (alo < i && blo < j)
                queue.Push((alo, i, blo, j));
            if (i + k < ahi && j + k < bhi)
                queue.Push((i + k, ahi, j + k, bhi));
        }
        return matched;
    }

    /// <summary>
    /// Longest block matching a[alo:ahi] against b[blo:bhi], earliest in a and
    /// then earliest in b when tied -- the tie-break Python guarantees.
    /// </summary>
    private static (int i, int j, int size) LongestMatch(
        string a, string b, Dictionary<char, List<int>> b2j,
        int alo, int ahi, int blo, int bhi)
    {
        int besti = alo, bestj = blo, bestsize = 0;
        // j2len[j] is the length of the match ending at a[i-1], b[j-1].
        var j2len = new Dictionary<int, int>();
        for (int i = alo; i < ahi; i++)
        {
            var newj2len = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out var positions))
            {
                foreach (int j in positions)
                {
                    if (j < blo)
                        continue;
                    if (j >= bhi)
                        break;
                    int k = (j2len.TryGetValue(j - 1, out int prev) ? prev : 0) + 1;
                    newj2len[j] = k;
                    if (k > bestsize)
                    {
                        besti = i - k + 1;
                        bestj = j - k + 1;
                        bestsize = k;
                    }
                }
            }
            j2len = newj2len;
        }
        return (besti, bestj, bestsize);
    }
}
