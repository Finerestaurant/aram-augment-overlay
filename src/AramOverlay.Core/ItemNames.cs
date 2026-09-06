using System.Text.Json;

namespace AramOverlay.Core;

/// <summary>
/// Item names, used only to tell an anvil apart from an augment.
///
/// Mayhem hands out item anvils too, and the reroll-button gate opens for both.
/// An item name fuzzy-matched against the augment list alone still clears the
/// threshold and publishes a wrong augment: 밴시의 장막 scores 0.80 against
/// 감시의 장막. Matching against both closed sets settles it -- whichever set the
/// text is closer to is the set it came from.
/// </summary>
public sealed class ItemNames
{
    private readonly (string Norm, string NormStripped)[] _norm;

    private ItemNames(IEnumerable<string> names) =>
        _norm = names.Select(n =>
        {
            string squashed = Hangul.Squash(n);
            return (squashed, Hangul.StripFinal(squashed));
        }).ToArray();

    public static async Task<ItemNames> LoadAsync(bool refresh = false, int maxAgeDays = 7)
    {
        string cache = Path.Combine(Config.Data, $"items_{Config.Locale}.json");
        string json = await CDragon.FetchAsync(Config.CDragonItemsUrl, cache, refresh, maxAgeDays);

        var names = new List<string>();
        using var doc = JsonDocument.Parse(json);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            string name = (row.TryGetProperty("name", out var n) ? n.GetString() : null)?.Trim() ?? "";
            if (name.Length > 0)
                names.Add(name);
        }
        return new ItemNames(names);
    }

    /// <summary>How closely the text matches any item name, 0..1.</summary>
    public double BestScore(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0.0;
        string q = Hangul.Squash(text);
        string qs = Hangul.StripFinal(q);
        double best = 0.0;
        foreach (var (norm, normStripped) in _norm)
        {
            double score = Math.Max(Difflib.Ratio(q, norm), Difflib.Ratio(qs, normStripped));
            if (score > best)
                best = score;
        }
        return best;
    }
}
