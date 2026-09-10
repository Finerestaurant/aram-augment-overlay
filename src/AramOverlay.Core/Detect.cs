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
    /// Where 1920x1080-space coordinates land on a frame of this size.
    ///
    /// The client scales the augment screen uniformly by height and centres it
    /// horizontally -- measured on the same window captured at all fifteen
    /// fullscreen sizes this monitor offers, 1680x1050 and 1024x768 included:
    /// the three cards span 0.98 of the height at every one of them and sit
    /// around the frame's own centre line. So there is one scale, taken from
    /// the height, and a horizontal offset that is zero on 16:9 and a whole
    /// number of pixels otherwise. Whole, so that a 16:9 frame maps exactly as
    /// it always has, truncation and all; the parity fixtures are bit-exact.
    /// </summary>
    public readonly record struct Geometry(double S, int OffX)
    {
        public static Geometry Of(int w, int h)
        {
            double s = (double)h / Config.BaseH;
            return new Geometry(s, (int)Math.Round(w / 2.0 - Config.BaseW / 2.0 * s));
        }

        /// <summary>Frame x for a 1080p-space x, truncating like the Python port did.</summary>
        public int X(double xb) => (int)(xb * S) + OffX;
        public int Y(double yb) => (int)(yb * S);
        /// <summary>A frame length for a 1080p-space one.</summary>
        public int L(double len) => (int)(len * S);
        public double BackX(double x) => (x - OffX) / S;
        public double BackY(double y) => y / S;
    }

    /// <summary>
    /// A box in 1920x1080 space mapped onto this frame. Truncating rather than
    /// rounding, because that is what the Python side does and a half-pixel
    /// shift on the rarity strip changes which pixels get averaged.
    /// </summary>
    public static Box Scale(Box box, int w, int h)
    {
        var g = Geometry.Of(w, h);
        return new Box(g.X(box.X0), g.Y(box.Y0), g.X(box.X1), g.Y(box.Y1));
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

    /// <summary>
    /// Nothing on the frame at all -- what an unhooked game capture hands back.
    /// A game frame, loading screen included, always has pixels well above the
    /// level; every eighth pixel is enough to know.
    /// </summary>
    public static bool IsBlank(GrayImage gray)
    {
        for (int i = 0; i < gray.Pixels.Length; i += 8)
            if (gray.Pixels[i] > Config.BlankLevel)
                return false;
        return true;
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

    /// <summary>Where the tooltip panel is on this frame, and which way it hangs.</summary>
    public readonly record struct TooltipPanel(
        int Top, int X0, int X1, bool Flipped, Box Title);

    /// <summary>Sobel magnitude at one point -- the reroll gate's filter, one pixel at a time.</summary>
    private static double MagAt(GrayImage g, int x, int y)
    {
        if (x < 1 || y < 1 || x >= g.Width - 1 || y >= g.Height - 1)
            return 0.0;
        double gx = 0, gy = 0;
        for (int ky = -1; ky <= 1; ky++)
        {
            int row = (y + ky) * g.Width;
            for (int kx = -1; kx <= 1; kx++)
            {
                double v = g.Pixels[row + x + kx];
                gx += v * SobelX[(ky + 1) * 3 + (kx + 1)];
                gy += v * SobelY[(ky + 1) * 3 + (kx + 1)];
            }
        }
        return Math.Sqrt(gx * gx + gy * gy);
    }

    private static readonly double[] SobelX = { -1, 0, 1, -2, 0, 2, -1, 0, 1 };
    private static readonly double[] SobelY = { -1, -2, -1, 0, 0, 0, 1, 2, 1 };

    /// <summary>
    /// A column belonging to a reroll button's own side, which must not be
    /// mistaken for the panel's.
    ///
    /// The outer pair, 568 and 1372, sit 392 and 412 from the centre -- close
    /// enough to pass a symmetry check and wide enough to beat the real panel
    /// when the widest pair wins. Their positions are known exactly, so they
    /// are simply skipped rather than argued with.
    /// </summary>
    private static bool IsRerollEdge(int x, Geometry g)
    {
        int slack = g.L(5);
        foreach (var (bx, _) in Config.RerollBoxes)
        {
            if (Math.Abs(x - g.X(bx)) <= slack ||
                Math.Abs(x - g.X(bx + Config.RerollSize.W)) <= slack)
                return true;
        }
        return false;
    }

    /// <summary>How much of a vertical run has an edge at this column.</summary>
    private static double SideSupport(GrayImage g, int x, int y0, int y1)
    {
        int hit = 0, total = 0;
        for (int y = y0; y <= y1; y++)
        {
            double best = 0;
            for (int dx = -1; dx <= 1; dx++)
                best = Math.Max(best, MagAt(g, x + dx, y));
            if (best > Config.TooltipEdgeV)
                hit++;
            total++;
        }
        return total == 0 ? 0.0 : hit / (double)total;
    }

    /// <summary>
    /// The panel's sides, found by walking out from the screen's centre.
    ///
    /// Not from the horizontal run on the anchor: the reroll buttons' own
    /// bottom border sits on y=784, one pixel off the anchor, so the run merges
    /// button, panel and button into one. On a 683-wide panel that read 807 and
    /// flipped the verdict. The buttons' vertical sides are only 41 tall and
    /// live above the anchor, so scanning columns is free of them.
    /// </summary>
    private static (int X0, int X1)? Sides(GrayImage g, int y0, int y1, Geometry geo, bool skipReroll)
    {
        int cx = geo.X(Config.TooltipCentre);
        int lo = geo.L(60), hi = geo.L(Config.TooltipMaxHalfWidth);
        int tol = geo.L(30);

        // Every column that could be a side, then the widest symmetric pair --
        // not the first one found walking out from the centre. The tooltip's
        // own icon has a border 55 rows tall inside the panel, which clears the
        // support bar and sits 70px closer in on the left than the real edge is
        // on the right; taking first-found made the pair asymmetric, failed the
        // check, and sent a perfectly ordinary tooltip down the flipped branch.
        var left = new List<int>();
        var right = new List<int>();
        for (int d = lo; d <= hi; d++)
        {
            if (!(skipReroll && IsRerollEdge(cx - d, geo)) &&
                SideSupport(g, cx - d, y0, y1) >= Config.TooltipSideSupport)
                left.Add(d);
            if (!(skipReroll && IsRerollEdge(cx + d, geo)) &&
                SideSupport(g, cx + d, y0, y1) >= Config.TooltipSideSupport)
                right.Add(d);
        }
        int bestL = -1, bestR = -1;
        foreach (int dl in left)
            foreach (int dr in right)
            {
                if (Math.Abs(dl - dr) > tol)
                    continue;
                if (dl + dr > bestL + bestR)
                {
                    bestL = dl;
                    bestR = dr;
                }
            }
        return bestL < 0 ? null : (cx - bestL, cx + bestR);
    }

    /// <summary>
    /// Find the tooltip panel, if one is up.
    ///
    /// Three questions in order. Is there a long border on the anchor line --
    /// that alone says a tooltip exists, because the anchor is the panel's top
    /// when it hangs down and its bottom when it flips up. Then: do the sides
    /// run downwards from it? If so the panel is below and the title is right
    /// under the anchor. If not, do they run upwards? Then the panel is above
    /// and its top has to be found before the title can be read.
    ///
    /// Below is tried first because it is the ordinary case, and because the
    /// card art above the anchor puts vertical edges at plausible columns --
    /// asking both directions and comparing gives a tie far too often.
    /// </summary>
    public static TooltipPanel? FindTooltip(GrayImage g)
    {
        var geo = Geometry.Of(g.Width, g.Height);
        int anchor = -1;
        int a = geo.Y(Config.TooltipAnchor), tol = geo.L(Config.TooltipAnchorTol);
        int xFrom = geo.X(500), xTo = geo.X(1420);
        int bestRun = 0;
        for (int y = a - tol; y <= a + tol; y++)
        {
            int start = -1, prev = -1, r0 = 0, r1 = -1;
            for (int x = xFrom; x < xTo; x++)
            {
                if (MagAt(g, x, y) <= Config.TooltipEdgeH)
                    continue;
                if (start < 0 || x - prev > geo.L(6))
                {
                    if (start >= 0 && prev - start > r1 - r0) { r0 = start; r1 = prev; }
                    start = x;
                }
                prev = x;
            }
            if (start >= 0 && prev - start > r1 - r0) { r0 = start; r1 = prev; }
            int width = r1 - r0;
            double mid = geo.BackX((r0 + r1) / 2.0);
            if (width >= geo.L(Config.TooltipMinWidth) &&
                Math.Abs(mid - Config.TooltipCentre) <= Config.TooltipCentreTol &&
                width > bestRun)
            {
                bestRun = width;
                anchor = y;
            }
        }
        if (anchor < 0)
            return null;

        int span = geo.L(Config.TooltipTitleHeight), pad = geo.L(4);
        // The reroll buttons live at y 743..784, entirely above the anchor, so
        // they can only pollute the upward scan. Skipping their columns in the
        // downward one costs a real panel: frame_05's right edge is 1302 and a
        // button's left edge is 1304.
        var below = Sides(g, anchor + pad, anchor + span, geo, skipReroll: false);
        if (below is { } b)
            return new TooltipPanel(anchor, b.X0, b.X1, false,
                TitleBox(anchor, b.X0, b.X1, geo));

        var above = Sides(g, anchor - span, anchor - pad, geo, skipReroll: true);
        if (above is not { } u)
            return null;

        // Flipped: the anchor is the bottom, so the top is the next border up
        // with the same width. Failing that, fall back to the tallest panel
        // that can fit, which is what the anchor rule implies anyway.
        int top = anchor - geo.L(297);
        for (int y = anchor - geo.L(60); y >= geo.L(200); y--)
        {
            if (SideSupport(g, u.X0, y, y + geo.L(20)) < Config.TooltipSideSupport)
            {
                top = y;
                break;
            }
        }
        return new TooltipPanel(top, u.X0, u.X1, true, TitleBox(top, u.X0, u.X1, geo));
    }

    /// <summary>
    /// Draw the finding onto a frame: the anchor line, the panel's sides, the
    /// title box handed to OCR, and the old fixed box for comparison. Shared so
    /// the debug frame the loop writes and the offline check in SelfTest are
    /// the same picture.
    /// </summary>
    public static void Mark(Frame frame, TooltipPanel? panel)
    {
        // The panel's numbers are frame pixels; the anchor is a 1080p-space
        // constant, so it is mapped onto the frame the same way FindTooltip
        // did before it looked there.
        var geo = Geometry.Of(frame.Width, frame.Height);
        int anchor = geo.Y(Config.TooltipAnchor);
        frame.DrawBox(Config.HoverTooltip, 130, 130, 130, 2);
        frame.DrawRaw(geo.X(400), anchor, geo.X(1520), anchor + 2, 60, 170, 255, 1);
        if (panel is not { } p)
            return;
        // The panel always grows downwards from its own top -- flipping moves
        // where the top is, it does not turn the panel upside down. Drawing it
        // the other way put the outline 260px above a panel that was sitting
        // right there under it.
        int bottom = p.Flipped ? anchor : p.Top + geo.L(260);
        frame.DrawRaw(p.X0, p.Top, p.X1, Math.Max(p.Top + geo.L(20), bottom), 90, 200, 90, 2);
        frame.DrawBox(p.Title, 60, 240, 255, 4);
    }

    /// <summary>The title row in 1080p space, so the OCR frame -- a different size -- can map it back.</summary>
    private static Box TitleBox(int top, int x0, int x1, Geometry geo) => new(
        (int)geo.BackX(x0 + geo.L(Config.TooltipIconWidth)),
        (int)geo.BackY(top + geo.L(4)),
        (int)geo.BackX(x1),
        (int)geo.BackY(top + geo.L(Config.TooltipTitleHeight)));

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
        var geo = Detect.Geometry.Of(gray.Width, gray.Height);
        int tw = Math.Max(4, Round(Config.RerollSize.W * geo.S));
        int th = Math.Max(4, Round(Config.RerollSize.H * geo.S));

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
            int x0 = Round(bx * geo.S) + geo.OffX, y0 = Round(by * geo.S);
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
        var geo = Detect.Geometry.Of(gray.Width, gray.Height);
        var box = Config.HideBox;
        int x0 = TemplateGate.Round(box.X0 * geo.S) + geo.OffX, y0 = TemplateGate.Round(box.Y0 * geo.S);
        int tw = Math.Max(4, TemplateGate.Round(box.Width * geo.S));
        int th = Math.Max(4, TemplateGate.Round(box.Height * geo.S));

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
