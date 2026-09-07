namespace AramOverlay.Core;

/// <summary>One box on the 1920x1080 screen, in pixels.</summary>
public readonly record struct Box(int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0;
    public int Height => Y1 - Y0;

    public Box Scaled(double sx, double sy) => new(
        (int)Math.Round(X0 * sx), (int)Math.Round(Y0 * sy),
        (int)Math.Round(X1 * sx), (int)Math.Round(Y1 * sy));
}

/// <summary>
/// Tunables and screen geometry, ported from config.py without changing a
/// number. Every value here was measured on real captures; the reasoning behind
/// each one is in docs/FINDINGS.md and is not repeated in full.
///
/// Coordinates are written for 1920x1080 and used proportionally: detection
/// divides by <see cref="BaseW"/>/<see cref="BaseH"/> and multiplies by the
/// frame it was actually handed, so the frame size and the screen size never
/// have to agree.
/// </summary>
public static class Config
{
    public static string Locale = "ko_kr";

    /// <summary>
    /// The client language as a bare two-letter code, which is the language the
    /// widget is written in. The window follows the interface setting; the
    /// widget follows this, because it is read next to the augment names.
    /// </summary>
    public static string LocaleLanguage => Locale.Split('_')[0];
    public static string[] OcrLanguages = { "ko-KR", "ko" };

    public static int BaseW = 1920;
    public static int BaseH = 1080;

    // --- OBS ---
    public static string ObsHost = "127.0.0.1";
    public static int ObsPort = 4455;
    public static string ObsPassword = "";
    public static string ObsSource = "League of Legends";

    // Detection runs on small JPEGs: PNG at 1920x1080 costs ~2755 ms per frame,
    // JPEG at 960x540 costs ~69 ms. Full-resolution frames are pulled only when
    // a selection is confirmed, for OCR.
    public const int DetW = 960;
    public const int DetH = 540;
    public const int DetQuality = 80;
    public const int OcrQuality = 92;

    // --- reroll button template gate ---
    // Top-left of each reroll box, measured rather than searched for: a padded
    // search drifts onto the cursor and opens the gate on the item shop.
    public static (int X, int Y)[] RerollBoxes = { (568, 743), (936, 743), (1304, 743) };
    public static (int W, int H) RerollSize = (68, 41);

    // Positives >= 0.659, negatives <= 0.352 over 15 window frames vs 6 without.
    public const double GateOpen = 0.45;      // all three buttons, two frames running
    public const double GateStay = 0.30;      // must stay above the highest negative
    public const double GateStayTwo = 0.38;   // its own number: closing early publishes
                                              // an augment the player did not take
    public const int CloseMisses = 16;

    // --- card regions ---
    public static Dictionary<string, Box> Cards = new()
    {
        ["L"] = new(440, 190, 760, 720),
        ["M"] = new(810, 190, 1130, 720),
        ["R"] = new(1180, 190, 1500, 720),
    };
    public static Dictionary<string, Box> CardInteriors = new()
    {
        ["L"] = new(520, 560, 680, 700),
        ["M"] = new(890, 560, 1050, 700),
        ["R"] = new(1260, 560, 1420, 700),
    };
    // Thin strip across each card's top border -- the rarity colour lives here.
    public static Dictionary<string, Box> CardBorders = new()
    {
        ["L"] = new(470, 188, 730, 206),
        ["M"] = new(840, 188, 1100, 206),
        ["R"] = new(1210, 188, 1470, 206),
    };
    // The augment name printed on the card, which stays for as long as the
    // window is open -- unlike the tooltip, which only exists while hovering.
    public static Dictionary<string, Box> CardTitles = new()
    {
        ["L"] = new(460, 420, 738, 463),
        ["M"] = new(828, 420, 1106, 463),
        ["R"] = new(1196, 420, 1474, 463),
    };
    // Tried in order, stopping at the first confident match. Long names are
    // exact at 1x; 범람 first appears at 4x, 핀볼 at 5x.
    public static readonly int[] CardScales = { 1, 2, 3, 4, 5 };

    public const double HoverSpread = 10.0;
    public const double EntryAnimS = 0.6;

    // --- rarity, from the border strip ---
    // Gold and prismatic separate on hue; silver is the dark one, so it
    // separates on value. Saturation is not usable.
    public const double RarityValueSilver = 160.0;
    public static readonly (double Lo, double Hi) RarityHueGold = (0, 50);
    public static readonly (double Lo, double Hi) RarityHuePrism = (95, 145);

    /// <summary>
    /// Where the tooltip's title used to be assumed to sit. Still the fallback
    /// when the panel cannot be found, so a failed detection is no worse than
    /// what shipped before.
    /// </summary>
    public static Box HoverTooltip = new(620, 788, 1330, 842);

    // --- tooltip panel ----------------------------------------------------
    // The panel is not at a fixed place. Measured on five captures, hand-marked
    // and then snapped onto their edges with the same Sobel the reroll gate
    // uses: width runs 222 to 697 and height 121 to 341, both following the
    // text. What does hold still is
    //
    //   * a horizontal border on y=783 in every single one, and
    //   * a horizontal centre of 962 (spread 960.5 to 963.5), and
    //   * a title row 80 tall with a divider under it at 862-865, and
    //   * an icon about 68 wide at the panel's left.
    //
    // 783 is the anchor. The panel hangs below it, unless it is too tall to fit
    // -- 1080 - 783 = 297 -- in which case it flips and hangs its bottom there
    // instead. The one flipped capture measured 341 tall, bottom at 781.
    public const int TooltipAnchor = 783;
    public const int TooltipAnchorTol = 6;
    public const int TooltipCentre = 960;
    public const int TooltipCentreTol = 45;
    // A reroll button is 68 wide and the narrowest panel seen is 222. The bar
    // sits between them, well clear of both.
    public const int TooltipMinWidth = 200;
    public const int TooltipMaxHalfWidth = 430;
    public const int TooltipTitleHeight = 80;
    public const int TooltipIconWidth = 68;
    public const double TooltipEdgeH = 55.0;
    public const double TooltipEdgeV = 30.0;
    public const double TooltipSideSupport = 0.7;

    public const double SelectFlare = 1.15;
    public const double FlareLookbackS = 2.5;

    // --- selection flare --------------------------------------------------
    // Taking a card IS animated after all: the chosen card fills with white
    // while the other two go dark, over about five frames at 60 fps. The
    // earlier reading that "no confirmation animation happens" came from three
    // windows and did not survive a frame-by-frame capture (docs/FINDINGS.md).
    //
    // Two tests, on two different axes, both measured. Across cards: how far
    // the brightest interior stands above the next. Across time: how far that
    // same card stands above where it sat earlier in this very window.
    //
    // Measured over a 60 fps capture of a level 3 pick (169 frames) and 64
    // frames dumped from 32 real windows, all scored by SelfTest --flare so the
    // numbers are the app's own arithmetic and not an approximation of it:
    //
    //                        inner ratio    rise over own baseline
    //   selection flare       4.3 - 6.6      3.8 - 6.1
    //   shop panel open       1.4 - 1.6      2.3 - 2.6
    //   hover, window open    1.0 - 2.6      about 1
    //   window gone, map      1.0 - 1.8      varies
    //
    // The interior boxes are used rather than the whole card because they are
    // small and central, so they do not straddle the edge of an occluder: with
    // the shop open, whole-card mean puts the exposed card at 5.4x the others
    // and would answer confidently with a panel covering two of the three.
    public const double FlareInnerRatio = 3.0;
    public const double FlareRise = 3.0;
    // Where the "before" in that rise is taken from -- far enough back that the
    // flare itself cannot be in it.
    public const double FlareBaselineFromS = 1.5;
    public const double FlareBaselineToS = 0.5;
    // The flare is the last thing that happens before the window goes, so there
    // is no reason to look far back -- and a reroll earlier in the window is
    // bright enough that it should not be given the chance to answer.
    public const double FlareWindowS = 1.2;

    // A bright-pixel bar was tried as the second test and dropped, which is
    // worth writing down because it looked convincing first time round. Scored
    // through ffmpeg it separated perfectly -- 0% everywhere, 3-12% on the
    // flare -- but ffmpeg's gray conversion is limited range, and on this app's
    // full-range arithmetic the same measurement collapses: over the card box
    // ordinary frames reach 16.9% and the flare only manages 8.7%; over the
    // interior box the flare manages 1.2% against 12.3%; at a near-white cutoff
    // of 245 the flare has no pixels at all. The white of the flare is a
    // champion-shaped burst that does not fill the interior box and is not
    // actually white. A measurement is only as good as the arithmetic it was
    // taken with, and the check that caught this was running it through
    // SelfTest rather than through the tool it was prototyped in.
    // CardStat still carries the number, because the trace records it and a
    // later session may find a cutoff that does work.
    public const int FlareBrightLevel = 200;
    // How old a tooltip reading may be, measured from the last frame the window
    // was up, and still be taken as where the cursor was at the click. Scans run
    // every 0.35 s and a grab plus OCR costs about as much again, so a cursor
    // resting on a card produces a reading well inside this; anything older is a
    // card the player has since moved off.
    public const double HoverTrustS = 1.5;

    // The augment screen can be tucked away with a button under the cards; the
    // window is over only when this goes too.
    public static Box HideBox = new(857, 823, 1066, 890);
    public const double HidePresent = 0.35;

    // --- name matching ---
    // Confirmed reads scored 1.00 except 과층전 -> 과충전 at 0.67; wrong reads
    // topped out at 0.50. Nothing sits between, and on a stream a wrong name is
    // worse than no name.
    public const double OcrMinScore = 0.60;
    // Ranks candidates, never filters them: a misread border must not put the
    // right answer out of reach.
    public const double RarityBonus = 0.05;

    // --- Live Client Data API ---
    public const string LiveUrl = "https://127.0.0.1:2999/liveclientdata/allgamedata";
    public const string MayhemGameMode = "KIWI";

    // --- widget ---
    public static string WidgetHost = "127.0.0.1";
    public static int WidgetPort = 8777;
    /// <summary>Set from settings. Turns on the frame dumps below.</summary>
    public static bool DebugMode;

    /// <summary>
    /// Every frame of every window, written out with its numbers drawn on it.
    ///
    /// This is the only way to check a verdict against what was on screen at the
    /// time: the log records the reading that won, and a wrong pick is almost
    /// always about a frame nobody kept. Costs a JPEG encode per frame and about
    /// 25 MB per window, so it is off unless asked for.
    /// </summary>
    public static bool TraceMode;
    public const int TraceMaxFrames = 1500;
    public const int TraceQuality = 82;

    public static int WidgetRows = 4;
    public static int WidgetMaxW = 420;

    /// <summary>
    /// The folder CommunityDragon keeps this language in.
    ///
    /// There is no en_us folder -- it 404s. English is the source language, so
    /// it lives in "default" and the localised folders exist for everything
    /// else. Asking for en_us used to kill the loop on startup with no augment
    /// data and no obvious reason.
    /// </summary>
    public static string CDragonLocale => Locale == "en_us" ? "default" : Locale;

    public static string CDragonUrl =>
        $"https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/{CDragonLocale}/v1/cherry-augments.json";
    public static string CDragonItemsUrl =>
        $"https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/{CDragonLocale}/v1/items.json";
    // The "/default" is not optional: without it every icon 404s, which the
    // widget swallows silently because a broken <img> just hides itself.
    public const string CDragonAssetBase =
        "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default";

    /// <summary>Where settings, caches and debug frames live: next to the exe.</summary>
    public static string Root { get; set; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();

    public static string Data => Path.Combine(Root, "data");
    public static string State => Path.Combine(Root, "state");
}
