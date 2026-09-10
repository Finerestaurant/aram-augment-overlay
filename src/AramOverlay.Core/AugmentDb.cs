using System.Text.Json;

namespace AramOverlay.Core;

public sealed record Augment(int Id, string NameId, string Name, string Rarity, string IconUrl);

/// <summary>
/// The augment list from CommunityDragon, and the fuzzy lookup over it.
///
/// cherry-augments.json holds 657 rows covering both Arena and ARAM Mayhem, and
/// Arena augments are deliberately kept: there is no clean field to split the
/// two, Mayhem reuses Arena icon assets, and the ARAM_ prefix misses entries
/// like 선동 that appear in Mayhem anyway. Duplicate names are disambiguated by
/// the rarity read off the card border instead.
///
/// What is dropped is the 39 rows that are not augments at all. kBronze is
/// Arena's stat shards (Stat_Movespeed 이동 속도, Stat_Armor 방어력, ...) and
/// kEventChoice its event picks; neither can ever appear on a Mayhem augment
/// card. They were pure false-match fodder, and being short names they matched
/// hard: a level 11 pick of 적응형 능력치 was published as 이동 속도. Mayhem's
/// rarity ladder is silver/gold/prismatic, which is also all the border
/// classifier can produce, so nothing legitimate is lost.
/// </summary>
public sealed class AugmentDb
{
    private static readonly Dictionary<string, string> RarityNames = new()
    {
        ["kSilver"] = "silver", ["kGold"] = "gold", ["kPrismatic"] = "prismatic",
    };

    private readonly (string Norm, string NormStripped, Augment Aug)[] _norm;
    private readonly HashSet<string> _mayhem;

    public IReadOnlyList<Augment> Augments { get; }

    /// <summary>The augmentNameIds Mayhem actually draws from, or empty if the
    /// list could not be read -- in which case nothing is ranked by it.</summary>
    public IReadOnlyCollection<string> MayhemPool => _mayhem;

    private AugmentDb(List<Augment> augments, HashSet<string> mayhem)
    {
        Augments = augments;
        _mayhem = mayhem;
        _norm = augments.Select(a =>
        {
            string squashed = Hangul.Squash(a.Name);
            return (squashed, Hangul.StripFinal(squashed), a);
        }).ToArray();
    }

    /// <summary>
    /// The augment list, in the game's language or in one asked for.
    ///
    /// <paramref name="locale"/> exists for the settings window, which shows
    /// sample augments in the language the window is speaking rather than the
    /// one the client is in. Everything else leaves it null and gets the game's.
    /// </summary>
    public static async Task<AugmentDb> LoadAsync(bool refresh = false, int maxAgeDays = 7,
                                                  string? locale = null)
    {
        locale ??= Config.Locale;
        // One cache per language, or switching languages reads back the previous
        // language's names for a week.
        string cache = Path.Combine(Config.Data, $"augments_{locale}.json");
        string json = await CDragon.FetchAsync(Config.AugmentsUrlFor(locale), cache,
                                               refresh, maxAgeDays);

        var augments = new List<Augment>();
        using var doc = JsonDocument.Parse(json);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            string name = (row.TryGetProperty("nameTRA", out var n) ? n.GetString() : null)?.Trim() ?? "";
            if (name.Length == 0)
                continue;
            string rarityRaw = row.TryGetProperty("rarity", out var r) ? r.GetString() ?? "" : "";
            if (!RarityNames.TryGetValue(rarityRaw, out string? rarity))
                continue;                     // stat shards and event picks -- see above
            augments.Add(new Augment(
                Id: row.TryGetProperty("id", out var id) ? id.GetInt32() : -1,
                NameId: row.TryGetProperty("augmentNameId", out var ni) ? ni.GetString() ?? "" : "",
                Name: name,
                Rarity: rarity,
                IconUrl: IconUrl(row.TryGetProperty("augmentSmallIconPath", out var ip)
                                 ? ip.GetString() ?? "" : "")));
        }
        return new AugmentDb(augments, await MayhemPoolAsync(refresh, maxAgeDays));
    }

    /// <summary>
    /// The augmentNameIds Mayhem draws from, off CommunityDragon's own mode
    /// lists.
    ///
    /// The entries are asset paths and the last segment is the augmentNameId,
    /// which is what the augment rows are keyed by. One cache for every
    /// language: there is nothing here to translate.
    ///
    /// A pool that cannot be read comes back empty and the bonus simply never
    /// applies, which is the behaviour this replaces. Ranking must not be the
    /// reason the tool fails to start.
    /// </summary>
    private static async Task<HashSet<string>> MayhemPoolAsync(bool refresh, int maxAgeDays)
    {
        var pool = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            string cache = Path.Combine(Config.Data, "augment_lists.json");
            string json = await CDragon.FetchAsync(Config.CDragonListsUrl, cache, refresh, maxAgeDays);
            using var doc = JsonDocument.Parse(json);
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                string mode = entry.TryGetProperty("modeName", out var m) ? m.GetString() ?? "" : "";
                if (!Config.MayhemModes.Contains(mode) ||
                    !entry.TryGetProperty("augmentList", out var list))
                    continue;
                foreach (var item in list.EnumerateArray())
                {
                    string path = item.GetString() ?? "";
                    if (path.Length == 0)
                        continue;
                    int slash = path.LastIndexOf('/');
                    pool.Add(slash >= 0 ? path[(slash + 1)..] : path);
                }
            }
        }
        catch
        {
            pool.Clear();
        }
        return pool;
    }

    private static string IconUrl(string path) =>
        path.Length == 0 ? "" :
        Config.CDragonAssetBase + path.ToLowerInvariant().Replace("/lol-game-data/assets", "");

    /// <summary>
    /// Best fuzzy match for OCR text, preferring the rarity read off the screen.
    ///
    /// Rarity ranks, it does not filter. Filtering put the right answer out of
    /// reach when a border was misread: 믿음직한 무기 read exactly, but with the
    /// pool restricted to gold the pick came back 환영 무기.
    /// </summary>
    public (Augment? Aug, double Score) Match(string text, string? rarity = null)
    {
        var (aug, score, _) = MatchDetailed(text, rarity);
        return (aug, score);
    }

    /// <summary>
    /// As <see cref="Match"/>, and says which other name tied when the answer
    /// was thrown away for being ambiguous.
    ///
    /// A tie is not a near miss, it is two names the reading fits equally well,
    /// and picking one is picking whichever the data file happened to list
    /// first. A cut-off 'Drop' scores identically against Dropkick and DropBear;
    /// the tool published DropBear, and nothing in the score said it was a coin
    /// toss. Names that repeat in the data are not a tie -- the same augment
    /// appears twice, and the rarity read off the border separates those.
    /// </summary>
    public (Augment? Aug, double Score, string? TiedWith) MatchDetailed(
        string text, string? rarity = null)
    {
        if (string.IsNullOrEmpty(text))
            return (null, 0.0, null);

        string q = Hangul.Squash(text);
        string qs = Hangul.StripFinal(q);

        Augment? best = null;
        double bestRanked = 0.0, bestScore = 0.0;
        string? tied = null;
        foreach (var (norm, normStripped, aug) in _norm)
        {
            double score = Math.Max(Difflib.Ratio(q, norm), Difflib.Ratio(qs, normStripped));
            double ranked = score
                + (rarity is not null && aug.Rarity == rarity ? Config.RarityBonus : 0.0)
                + (_mayhem.Contains(aug.NameId) ? Config.MayhemBonus : 0.0);
            if (ranked > bestRanked + TieEpsilon)
            {
                best = aug;
                bestRanked = ranked;
                bestScore = score;
                tied = null;                  // beaten outright; any earlier tie is moot
            }
            else if (best is not null && aug.Name != best.Name &&
                     Math.Abs(ranked - bestRanked) <= TieEpsilon)
            {
                tied = aug.Name;
            }
        }
        return tied is null ? (best, bestScore, null) : (null, bestScore, tied);
    }

    /// <summary>Difflib ratios are exact rationals in practice, so this only has
    /// to absorb the last bit of the division.</summary>
    private const double TieEpsilon = 1e-9;
}
