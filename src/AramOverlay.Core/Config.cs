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

    // The space every coordinate below is written in. Constant on purpose: an
    // earlier build let the resolution setting overwrite these while the
    // tooltip finder kept its own numbers (783, 960, 500..1420) as consts, so a
    // 1440p setting shrank the anchor search onto the wrong rows and no tooltip
    // was ever found there. The frame size varies; the space does not.
    public const int BaseW = 1920;
    public const int BaseH = 1080;

    /// <summary>
    /// How large a frame to pull for OCR, set from the game resolution. Native
    /// pixels read better than a downsample, and this is the only thing the
    /// resolution setting decides -- detection runs on <see cref="DetW"/> x
    /// <see cref="DetH"/> regardless, and the geometry is proportional.
    /// </summary>
    public static int OcrW = 1920;
    public static int OcrH = 1080;

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

    /// <summary>
    /// The detection frame to ask OBS for, given the source's own size.
    ///
    /// OBS scales to whatever size is asked for and does not keep the shape
    /// (a 1680x1050 source asked for at 960x540 comes back stretched), while
    /// the client lays the augment screen out by height and centres it. So the
    /// frame keeps the source's shape at the standard height, and the geometry
    /// (Detect.Geometry) does the rest. 16:9 sources land on exactly 960x540.
    /// </summary>
    public static (int W, int H) DetSizeFor(int sourceW, int sourceH) =>
        (Math.Max(8, (int)Math.Round((double)DetH * sourceW / sourceH)), DetH);

    // An unhooked game capture is pure black (every sample under this); three
    // such frames in a row, a second apart, is the source not being on the game
    // rather than a dark moment in it.
    public const int BlankLevel = 8;
    public const int BlankFramesBeforeRepair = 3;

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
    // How far BEFORE the anchor -- the last frame the reroll gate could still
    // see cards -- a frame may sit and still be read as the selection.
    //
    // The anchor is what makes this small. Taking a card wipes the UI, so the
    // flare straddles the anchor by construction: its first frame or two may
    // still score as cards-up, everything after is cards-down. A candidate with
    // the cards still up well after it is therefore not a selection at all --
    // whatever lit that card, the screen carried on.
    //
    // This was 1.2 s, which is fourteen times the length of the event, and the
    // surplus is where a reroll answers. Two windows on 2026-09-10, both with
    // all three cards rerolled:
    //
    //   17:48 lv11  flare R 4.50x/4.81x, 0.82 s before the close; tooltip and
    //               hover both said M, and M (신비한 주먹) was what was taken.
    //               R was the card the player had just rerolled.
    //   19:16 lv15  flare M 3.58x/5.02x, 0.82 s before the close; tooltip and
    //               hover both said R, and R (지옥의 전도체) was taken.
    //
    // In both, hover brightness -- which only reads frames the gate still saw
    // cards in -- came from 0.22 s and 0.42 s AFTER the winning flare frame. The
    // cards were up for a fifth of a second after the thing that supposedly
    // ended the screen. Every one of the fourteen windows the flare got right
    // that evening has the newest cards-up frame at or before its flare frame.
    //
    // 0.15 s covers a flare caught on its own first frame at any frame rate the
    // loop runs at (25-70 ms), and nothing near the 0.22 s that was measured on
    // the shortest false positive.
    //
    // Since the cards-down rule came in this reach decides almost nothing. The
    // anchor is the last frame the gate saw cards, a selection wipes them for
    // good, and the flare's own frames are cards-down -- so a real flare is
    // always AFTER the anchor. What is left back here is gate flicker: a frame
    // that scored cards-down before the cards actually went. Keeping the reach
    // short means such a frame cannot answer either.
    public const double FlareWindowS = 0.15;

    /// <summary>
    /// How far back the search still LOOKS, deciding nothing, so that a
    /// candidate the anchor rule threw out lands in the log with its numbers.
    /// The old reach, kept for exactly that: without it a wrong pick and a
    /// window where nothing flared at all write the same line.
    /// </summary>
    public const double FlareWideWindowS = 1.2;

    // How far PAST the anchor the search may reach, and since the cards-down
    // rule came in this is the only reach that decides anything.
    //
    // Reaching forward was never optional: the flare is what kills the reroll
    // gate, so its own frames score as cards-down and land on the far side of
    // the anchor by construction (the 60 fps capture has the gate going
    // 0.83/0.81/0.80 -> -0.12/0.12/0.10 across it). It used to be 1.2 s, which
    // is fourteen times the length of an 83 ms event, and that surplus is where
    // a level 11 window went wrong: 0.36 s after the cards had gone, the left
    // card's box sat over a lit champion on open map and the other two over dark
    // terrain, clearing both axes -- 3.21x the next box, 3.93x a baseline taken
    // from dark card interiors -- and outvoting a tooltip and a hover that both
    // named the right card. Nothing about that frame is a selection; it is three
    // patches of map. So it was cut to 0.05 s.
    //
    // 0.05 s was too tight once the backwards reach stopped doing the work.
    // Measured on the window recorded at 21:25 on 2026-09-10, where a reroll
    // flip sat on the anchor and the real selection came after it:
    //
    //   +0.025 s  M  ratio 4.30  rise 2.25   rise short
    //   +0.048 s  M  ratio 3.40  rise 2.92   rise short
    //   +0.068 s  M  ratio 3.47  rise 4.11   both pass  <- first frame that does
    //   +0.137 s  M  ratio 3.22  rise 5.51   still climbing
    //
    // The flare needs about 70 ms to clear the rise test, because the rise is
    // measured against a baseline the card has only just left. 0.15 s covers
    // that with room and stays well under the 0.36 s where the map answered --
    // and the map frame is cards-down too, so the cards-down rule does not
    // protect against it. This number does.
    public const double FlareWindowAfterS = 0.15;

    // A loser-collapse test was tried here and taken out again, which is worth
    // writing down because it looks obviously right. Section 6 measured the
    // losers falling to 0.51-0.70 of baseline on a real pick, so requiring that
    // should reject any lone bright thing landing in a card box. Applied to
    // inner against each slot's own baseline it rejected all five recorded
    // windows, real picks included: section 6's numbers are whole-card MEANS
    // against a baseline shared by the three cards, and neither the quantity nor
    // the denominator survives the substitution. The idea may still be right;
    // the threshold has to be measured on inner-against-own-baseline first.

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

    /// <summary>
    /// How often the tooltip scan may run. A full-resolution screenshot costs
    /// about 250 ms on its own request, so this paces how hard OBS is hit rather
    /// than the detection loop. It is also how stale the panel box beside a
    /// detection frame can be, which is why anything drawing that box needs it.
    /// </summary>
    public const double TooltipProbeS = 0.35;

    // The augment screen can be tucked away with a button under the cards; the
    // window is over only when this goes too.
    public static Box HideBox = new(857, 823, 1066, 890);
    public const double HidePresent = 0.35;

    // --- name matching ---
    // Confirmed reads scored 1.00 except 과층전 -> 과충전 at 0.67; wrong reads
    // topped out at 0.50. Nothing sits between, and on a stream a wrong name is
    // worse than no name.
    public const double OcrMinScore = 0.60;
    /// <summary>
    /// A reading good enough to be believed over the tooltip, and good enough
    /// to rule the tooltip's name out as belonging to some other card.
    ///
    /// Confirmed reads land on 1.00; the two that were only partly on screen
    /// because the tooltip panel was covering them scored 0.67 and 0.61. The
    /// bar sits above those and below a clean read.
    /// </summary>
    public const double OcrConfident = 0.90;
    // Ranks candidates, never filters them: a misread border must not put the
    // right answer out of reach.
    public const double RarityBonus = 0.05;

    /// <summary>
    /// The modes whose augment lists make up the Mayhem pool.
    ///
    /// KIWI is 222 augments and KIWI_JADE 188, overlapping in 163, so the union
    /// is 247 of the 657 rows the data file carries. Every one of the 247
    /// matches an augmentNameId in cherry-augments.json.
    /// </summary>
    public static readonly string[] MayhemModes = { "KIWI", "KIWI_JADE" };

    /// <summary>
    /// What being in that pool is worth to a candidate's rank.
    ///
    /// A bonus and not a filter, for the reason the rarity bonus is one: the
    /// pool is a claim about a patch, and cutting the other 410 rows out means a
    /// stale list can put the right answer somewhere the matcher cannot reach.
    /// As a nudge it does its best work exactly where the tie test would
    /// otherwise give up -- a truncated title that fits an Arena-only name as
    /// well as a Mayhem one is no longer a coin toss.
    ///
    /// Same size as the rarity bonus. Both only decide near-ties, which is all
    /// either of them is entitled to decide.
    /// </summary>
    public const double MayhemBonus = 0.05;

    // --- Live Client Data API ---
    public const string LiveUrl = "https://127.0.0.1:2999/liveclientdata/allgamedata";
    public const string MayhemGameMode = "KIWI";

    /// <summary>
    /// How far two derived game-start times may sit apart and still be the same
    /// game, in seconds.
    ///
    /// The value is now minus the Live Client's gameTime. That was written down
    /// as drifting by "the poll interval and whatever the client rounds", which
    /// is wrong: measured inside one game on 2026-09-10, the derived start moved
    /// 61 s later over about fourteen minutes. gameTime does not advance with
    /// the wall clock. At 60 s the restore then refused a game it was still in
    /// the middle of -- a restart to install a build dropped three augments off
    /// a stream mid-game, which is the failure this whole file exists to
    /// prevent.
    ///
    /// Generous costs nothing and tight costs picks: the NEXT game's derived
    /// start differs by the length of the last one plus the queue, which is ten
    /// minutes at the very least, so five is clear of both.
    ///
    /// The drift is a rate mismatch, so it grows with the length of the game;
    /// a game long enough to outrun even this wants the saved gameTime compared
    /// against the live one instead of two derived starts.
    /// </summary>
    public const double SameGameToleranceS = 300.0;

    // --- widget ---
    public static string WidgetHost = "127.0.0.1";
    public static int WidgetPort = 8777;
    /// <summary>Set from settings. Turns on the decision frame dumps.</summary>
    public static bool DebugMode;

    // --- inspector --------------------------------------------------------
    /// <summary>
    /// Set from settings. Records every frame of every augment window, with the
    /// measurements each verdict was taken from, and serves them back on the
    /// widget's own port.
    ///
    /// Off by default and deliberately so: a window costs 15-25 MB. It exists
    /// because three wrong picks in one evening could be argued about from the
    /// log and not looked at -- the close dump is up to a second late and the
    /// frame that decided was already gone.
    /// </summary>
    public static bool InspectorOn;

    /// <summary>How many recorded windows to keep before the oldest is deleted.</summary>
    public static int InspectorKeep = 8;

    /// <summary>
    /// A hard stop on one recording, in frames. A window normally runs 120-510
    /// frames; a gate stuck open must not fill the disk while nobody is looking.
    /// </summary>
    public const int InspectorMaxFrames = 1600;

    public static int WidgetRows = 4;
    public static int WidgetMaxW = 420;

    /// <summary>
    /// Which of the widget's three looks the stream shows: "b" the HUD tray,
    /// "c" the single strip, "d" the game palette on today's layout.
    ///
    /// Served with the state rather than baked into the page, so choosing one
    /// takes effect within a poll instead of needing the browser source
    /// reloaded. The three are not variations on one idea -- b grows sideways,
    /// c takes almost no room and cannot show a level, d reads at a glance --
    /// so which is right is the layout's question, not ours.
    /// </summary>
    public static string WidgetTheme = "d";

    /// <summary>The themes the widget page knows how to draw.</summary>
    public static readonly string[] WidgetThemes = { "b", "c", "d" };

    /// <summary>
    /// The folder CommunityDragon keeps this language in.
    ///
    /// There is no en_us folder -- it 404s. English is the source language, so
    /// it lives in "default" and the localised folders exist for everything
    /// else. Asking for en_us used to kill the loop on startup with no augment
    /// data and no obvious reason.
    /// </summary>
    public static string CDragonLocale => Locale == "en_us" ? "default" : Locale;

    public static string CDragonUrl => AugmentsUrlFor(Locale);

    /// <summary>The augment list for a named locale, not necessarily the game's.</summary>
    public static string AugmentsUrlFor(string locale) =>
        "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/" +
        (locale == "en_us" ? "default" : locale) + "/v1/cherry-augments.json";
    public static string CDragonItemsUrl =>
        $"https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/{CDragonLocale}/v1/items.json";
    /// <summary>
    /// Which augments each game mode actually draws from.
    ///
    /// Not localised on purpose: the lists hold asset paths whose last segment
    /// is an augmentNameId, so there is nothing in them to translate and one
    /// cache serves every language.
    /// </summary>
    public const string CDragonListsUrl =
        "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/augment-lists.json";
    // The "/default" is not optional: without it every icon 404s, which the
    // widget swallows silently because a broken <img> just hides itself.
    public const string CDragonAssetBase =
        "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default";

    /// <summary>Where settings, caches and debug frames live: next to the exe.</summary>
    public static string Root { get; set; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();

    public static string Data => Path.Combine(Root, "data");
    public static string State => Path.Combine(Root, "state");

    /// <summary>Where recorded windows live, one folder per window.</summary>
    public static string Inspect => Path.Combine(State, "inspect");
}
