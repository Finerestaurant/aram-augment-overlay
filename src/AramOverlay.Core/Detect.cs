namespace AramOverlay.Core;

/// <summary>
/// Augment window detection: is it open, which card is hovered, which was taken.
///
/// Calibrated against labelled frames; the thresholds live in <see cref="Config"/>
/// with the measurements that produced them. The rule worth repeating is that
/// absolute brightness is never the gate -- an earlier version keyed on it and
/// broke completely when the map changed, because ordinary gameplay landed
/// inside the "window open" band.
/// </summary>
public static class Detect
{
    /// <summary>
    /// A box in 1920x1080 space mapped onto this frame. Truncating rather than
    /// rounding, because that is what the Python side does and a half-pixel
    /// shift on the rarity strip changes which pixels get averaged.
    /// </summary>
    public static Box Scale(Box box, int w, int h)
    {
        double sx = (double)w / Config.BaseW, sy = (double)h / Config.BaseH;
        return new Box((int)(box.X0 * sx), (int)(box.Y0 * sy),
                       (int)(box.X1 * sx), (int)(box.Y1 * sy));
    }

    public static double Mean(GrayImage gray, Box box)
    {
        var b = Scale(box, gray.Width, gray.Height);
        int x0 = Math.Max(0, b.X0), y0 = Math.Max(0, b.Y0);
        int x1 = Math.Min(gray.Width, b.X1), y1 = Math.Min(gray.Height, b.Y1);
        if (x1 <= x0 || y1 <= y0)
            return 0.0;
        long n = 0;
        double sum = 0;
        for (int y = y0; y < y1; y++)
        {
            int row = y * gray.Width;
            for (int x = x0; x < x1; x++)
            {
                sum += gray.Pixels[row + x];
                n++;
            }
        }
        return n == 0 ? 0.0 : sum / n;
    }

    public static Dictionary<string, double> CardMeans(GrayImage gray) =>
        Config.Cards.ToDictionary(kv => kv.Key, kv => Mean(gray, kv.Value));

    /// <summary>
    /// Fraction of the box, as a percentage, at or above <paramref name="level"/>.
    ///
    /// The selection flare fills a card with near-white; ordinary gameplay, the
    /// shop panel and a hovered card do not reach 200 at all. A mean cannot say
    /// that -- a large dim area and a small blazing one average the same -- and
    /// the whole point is to recognise the small blazing one.
    /// </summary>
    public static double BrightPercent(GrayImage gray, Box box, int level)
    {
        var b = Scale(box, gray.Width, gray.Height);
        int x0 = Math.Max(0, b.X0), y0 = Math.Max(0, b.Y0);
        int x1 = Math.Min(gray.Width, b.X1), y1 = Math.Min(gray.Height, b.Y1);
        if (x1 <= x0 || y1 <= y0)
            return 0.0;
        long n = 0, hit = 0;
        for (int y = y0; y < y1; y++)
        {
            int row = y * gray.Width;
            for (int x = x0; x < x1; x++)
            {
                if (gray.Pixels[row + x] >= level)
                    hit++;
                n++;
            }
        }
        return n == 0 ? 0.0 : 100.0 * hit / n;
    }

    /// <summary>What one card looked like on one frame.</summary>
    public readonly record struct CardStat(double Mean, double Inner, double Bright);

    /// <summary>
    /// All three cards measured three ways on one frame.
    ///
    /// <see cref="CardStat.Mean"/> is kept because it is what every threshold in
    /// the log has always been phrased against; the verdict is taken off
    /// <see cref="CardStat.Inner"/> and <see cref="CardStat.Bright"/>.
    /// </summary>
    public static Dictionary<string, CardStat> CardStats(GrayImage gray) =>
        Config.Cards.ToDictionary(kv => kv.Key, kv => new CardStat(
            Mean(gray, kv.Value),
            Mean(gray, Config.CardInteriors[kv.Key]),
            BrightPercent(gray, kv.Value, Config.FlareBrightLevel)));

    /// <summary>
    /// Is this one frame the selection flare, and if so on which card.
    ///
    /// Taking a card lights it up: over about five frames at 60 fps the chosen
    /// card fills with near-white while the other two dissolve. Two independent
    /// measurements are required, because each one alone has a false positive on
    /// record. The interior ratio alone is cleared by a map light beam falling
    /// through a card box after the window has gone -- 14.5% of that box was
    /// over 200 and nothing had been selected. The bright bar alone is what that
    /// beam clears. Together, across 213 measured frames -- one 60 fps capture
    /// of a pick plus 44 frames dumped from 17 real windows -- nothing but an
    /// actual selection has satisfied both.
    ///
    /// Returns the slot and ratio whether or not the ratio passed, so a caller
    /// writing a trace can record how close a frame came. The second test --
    /// the same card against its own earlier self -- needs the window's history
    /// and is applied by the caller.
    /// </summary>
    public static (string? Slot, string Top, double Ratio) Flare(
        Dictionary<string, CardStat> stats)
    {
        var order = stats.OrderByDescending(kv => kv.Value.Inner).ToArray();
        if (order.Length < 2 || order[1].Value.Inner <= 0)
            return (null, order.Length > 0 ? order[0].Key : "", 0);
        double ratio = order[0].Value.Inner / order[1].Value.Inner;
        return (ratio >= Config.FlareInnerRatio ? order[0].Key : null, order[0].Key, ratio);
    }

    /// <summary>
    /// Median card-interior brightness. Low means the cards have settled.
    ///
    /// During the entry animation the cards are washed out and bright while the
    /// reroll buttons already render normally, so the template score is high and
    /// misleading, and rarity read from such a frame is wrong.
    /// </summary>
    public static double InteriorDarkness(GrayImage gray) =>
        Median(Config.CardInteriors.Values.Select(b => Mean(gray, b)).ToArray());

    public static (string? Slot, double Spread) HoveredCard(Dictionary<string, double> means)
    {
        double spread = means.Values.Max() - means.Values.Min();
        if (spread <= Config.HoverSpread)
            return (null, spread);
        return (means.MaxBy(kv => kv.Value).Key, spread);
    }

    /// <summary>
    /// Rarity from the card border colour.
    ///
    /// Gold and prismatic separate on hue; silver is simply darker, so it
    /// separates on value. Saturation does not work -- a prismatic frame
    /// measured 18.8, below an observed silver at 35.4. With a slot given it
    /// reads that one card: the three routinely differ, so a median over all
    /// three answers a question nobody asked.
    /// </summary>
    public static string RarityOf(Frame frame, string? slot = null)
    {
        var boxes = slot is not null && Config.CardBorders.TryGetValue(slot, out var one)
            ? new[] { one }
            : Config.CardBorders.Values.ToArray();

        var hues = new List<double>();
        var vals = new List<double>();
        foreach (var box in boxes)
        {
            var b = Scale(box, frame.Width, frame.Height);
            int x0 = Math.Max(0, b.X0), y0 = Math.Max(0, b.Y0);
            int x1 = Math.Min(frame.Width, b.X1), y1 = Math.Min(frame.Height, b.Y1);
            if (x1 <= x0 || y1 <= y0)
                continue;

            // Hue is an angle, so it is averaged as one: a plain mean puts the
            // average of 179 and 1 in the middle of the colour wheel.
            double sumSin = 0, sumCos = 0, sumVal = 0;
            long n = 0;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int i = frame.Index(x, y);
                    var (hue, _, val) = Cv.ToHsv(frame.Bgra[i], frame.Bgra[i + 1], frame.Bgra[i + 2]);
                    double angle = Math.Round(hue) * 2.0 * Math.PI / 180.0;
                    sumSin += Math.Sin(angle);
                    sumCos += Math.Cos(angle);
                    sumVal += val;
                    n++;
                }
            }
            if (n == 0)
                continue;
            double mean = Math.Atan2(sumSin / n, sumCos / n) * 180.0 / Math.PI / 2.0;
            hues.Add(((mean % 180) + 180) % 180);
            vals.Add(sumVal / n);
        }

        if (hues.Count == 0)
            return "unknown";
        double h = Median(hues.ToArray()), v = Median(vals.ToArray());
        if (v < Config.RarityValueSilver)
            return "silver";
        if (h >= Config.RarityHuePrism.Lo && h <= Config.RarityHuePrism.Hi)
            return "prismatic";
        if (h >= Config.RarityHueGold.Lo && h <= Config.RarityHueGold.Hi)
            return "gold";
        return "unknown";
    }

    public static double Median(double[] values)
    {
        if (values.Length == 0)
            return 0.0;
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}

/// <summary>
/// Matches the three reroll buttons.
///
/// All three must match to open. A single button reached 0.894 on an ordinary
/// gameplay frame while the other two sat at 0.559 and 0.263, so any one-button
/// rule false-positives. Staying open uses a looser threshold, because the
/// buttons have three visual states and the templates only cover some of them,
/// so min-of-three dips hard mid-window.
/// </summary>
public sealed class TemplateGate
{
    private readonly GrayImage[] _templates;
    private readonly Dictionary<(int, int), byte[][]> _prepped = new();

    public TemplateGate(IEnumerable<GrayImage> templates)
    {
        _templates = templates.ToArray();
        if (_templates.Length == 0)
            throw new FileNotFoundException("no reroll templates");
    }

    /// <summary>
    /// Correlation at each of the three fixed boxes, one score each. No spatial
    /// search: the boxes are at fixed screen coordinates, and a search window
    /// only lets a negative frame find something button-shaped nearby and report
    /// that instead.
    /// </summary>
    public double[] Scores(GrayImage gray)
    {
        double sx = (double)gray.Width / Config.BaseW, sy = (double)gray.Height / Config.BaseH;
        int tw = Math.Max(4, Round(Config.RerollSize.W * sx));
        int th = Math.Max(4, Round(Config.RerollSize.H * sy));

        if (!_prepped.TryGetValue((tw, th), out var prepped))
        {
            prepped = _templates
                .Select(t => Cv.Edges(t.Width == tw && t.Height == th ? t : Cv.ResizeArea(t, tw, th)))
                .ToArray();
            _prepped[(tw, th)] = prepped;
        }

        var scores = new double[Config.RerollBoxes.Length];
        for (int i = 0; i < Config.RerollBoxes.Length; i++)
        {
            var (bx, by) = Config.RerollBoxes[i];
            int x0 = Round(bx * sx), y0 = Round(by * sy);
            if (x0 < 0 || y0 < 0 || x0 + tw > gray.Width || y0 + th > gray.Height)
            {
                scores[i] = 0.0;
                continue;
            }
            var edge = Cv.Edges(gray.Crop(x0, y0, tw, th));
            scores[i] = prepped.Max(t => Cv.MatchCcoeffNormed(edge, t));
        }
        return scores;
    }

    // Python's round() is banker's rounding, and 41 * 0.5 lands exactly on .5.
    internal static int Round(double v) => (int)Math.Round(v, MidpointRounding.ToEven);
}

/// <summary>
/// The button that tucks the augment screen away.
///
/// Pressing it hides the cards and the reroll buttons while leaving itself on
/// screen, which the reroll gate alone reads as "window closed". One augment was
/// recorded three times that way, once per card the cursor passed over. The
/// screen is only really finished when this goes too.
/// </summary>
public sealed class HideButton
{
    private readonly GrayImage _template;
    private readonly Dictionary<(int, int), byte[]> _prepped = new();

    public HideButton(GrayImage template) => _template = template;

    public double Score(GrayImage gray)
    {
        double sx = (double)gray.Width / Config.BaseW, sy = (double)gray.Height / Config.BaseH;
        var box = Config.HideBox;
        int x0 = TemplateGate.Round(box.X0 * sx), y0 = TemplateGate.Round(box.Y0 * sy);
        int tw = Math.Max(4, TemplateGate.Round(box.Width * sx));
        int th = Math.Max(4, TemplateGate.Round(box.Height * sy));

        if (!_prepped.TryGetValue((tw, th), out var template))
        {
            var based = _template.Width == tw && _template.Height == th
                ? _template : Cv.ResizeArea(_template, tw, th);
            _prepped[(tw, th)] = template = Cv.Edges(based);
        }

        if (x0 < 0 || y0 < 0 || x0 + tw > gray.Width || y0 + th > gray.Height)
            return 0.0;
        return Cv.MatchCcoeffNormed(Cv.Edges(gray.Crop(x0, y0, tw, th)), template);
    }
}

public sealed class AugmentWindowState
{
    public bool Open;
    public double OpenedAt;
    public int OpenStreak;
    public int MissStreak;
    public List<string> HoverHistory { get; } = new();
    public string? LastHover;
    public double Baseline;
}
