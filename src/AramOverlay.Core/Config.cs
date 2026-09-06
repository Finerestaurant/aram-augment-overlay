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

    public static Box HoverTooltip = new(620, 788, 1330, 842);

    public const double SelectFlare = 1.15;
    public const double FlareLookbackS = 2.5;
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
