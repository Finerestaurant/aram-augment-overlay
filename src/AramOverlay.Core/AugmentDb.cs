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

    public IReadOnlyList<Augment> Augments { get; }

    private AugmentDb(List<Augment> augments)
    {
        Augments = augments;
        _norm = augments.Select(a =>
        {
            string squashed = Hangul.Squash(a.Name);
            return (squashed, Hangul.StripFinal(squashed), a);
        }).ToArray();
    }

    public static async Task<AugmentDb> LoadAsync(bool refresh = false, int maxAgeDays = 7)
    {
        // One cache per language, or switching languages reads back the previous
        // language's names for a week.
        string cache = Path.Combine(Config.Data, $"augments_{Config.Locale}.json");
        string json = await CDragon.FetchAsync(Config.CDragonUrl, cache, refresh, maxAgeDays);

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
        return new AugmentDb(augments);
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
        if (string.IsNullOrEmpty(text))
            return (null, 0.0);

        string q = Hangul.Squash(text);
        string qs = Hangul.StripFinal(q);

        Augment? best = null;
        double bestRanked = 0.0, bestScore = 0.0;
        foreach (var (norm, normStripped, aug) in _norm)
        {
            double score = Math.Max(Difflib.Ratio(q, norm), Difflib.Ratio(qs, normStripped));
            double ranked = score + (rarity is not null && aug.Rarity == rarity ? Config.RarityBonus : 0.0);
            if (ranked > bestRanked)
            {
                best = aug;
                bestRanked = ranked;
                bestScore = score;
            }
        }
        return (best, bestScore);
    }
}
