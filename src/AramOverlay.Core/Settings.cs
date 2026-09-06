using System.Text.Json;
using System.Text.Json.Nodes;

namespace AramOverlay.Core;

/// <summary>
/// User settings: the few things worth changing without editing code.
///
/// Stored in config.json next to the exe -- the same file the Python build used,
/// so an existing install keeps its OBS password and language. Applying a
/// setting overwrites the matching field in <see cref="Config"/>, which keeps
/// the rest of the code reading plain constants.
///
/// Resolution deserves a word. Every box in Config is written in 1920x1080
/// pixels and detection divides by BaseW/BaseH, so the geometry is really
/// proportional: frames arrive from OBS scaled to whatever size is asked for,
/// and a 16:9 screen of any size lands on the same layout. Setting a resolution
/// rescales the boxes and grabs the OCR frame at that size. What it cannot fix
/// is a screen that is not 16:9, where the client anchors its HUD differently.
/// </summary>
public sealed class Settings
{
    public string Locale { get; set; } = "ko_kr";
    public string OcrLanguage { get; set; } = "";          // "" -> follow the locale
    public int ScreenWidth { get; set; } = 1920;
    public int ScreenHeight { get; set; } = 1080;
    public int ObsPort { get; set; } = 4455;
    public string ObsPassword { get; set; } = "";          // "" -> read from OBS's own config
    public string ObsSource { get; set; } = "League of Legends";
    public int WidgetPort { get; set; } = 8777;
    public int WidgetRows { get; set; } = 4;
    public int WidgetMaxWidth { get; set; } = 420;

    /// <summary>CommunityDragon publishes one folder per client language.</summary>
    public static readonly (string Code, string Name)[] Locales =
    {
        ("ko_kr", "한국어"), ("en_us", "English"), ("ja_jp", "日本語"),
        ("zh_cn", "中文(简体)"), ("zh_tw", "中文(繁體)"), ("es_es", "Español"),
        ("es_mx", "Español (MX)"), ("fr_fr", "Français"), ("de_de", "Deutsch"),
        ("it_it", "Italiano"), ("pl_pl", "Polski"), ("ru_ru", "Русский"),
        ("pt_br", "Português"), ("tr_tr", "Türkçe"), ("vi_vn", "Tiếng Việt"),
        ("th_th", "ไทย"),
    };

    /// <summary>The Windows OCR tags for each language, most specific first.</summary>
    public static readonly Dictionary<string, string[]> OcrForLocale = new()
    {
        ["ko_kr"] = new[] { "ko-KR", "ko" }, ["en_us"] = new[] { "en-US", "en" },
        ["ja_jp"] = new[] { "ja-JP", "ja" }, ["zh_cn"] = new[] { "zh-Hans-CN", "zh-Hans" },
        ["zh_tw"] = new[] { "zh-Hant-TW", "zh-Hant" }, ["es_es"] = new[] { "es-ES", "es" },
        ["es_mx"] = new[] { "es-MX", "es" }, ["fr_fr"] = new[] { "fr-FR", "fr" },
        ["de_de"] = new[] { "de-DE", "de" }, ["it_it"] = new[] { "it-IT", "it" },
        ["pl_pl"] = new[] { "pl-PL", "pl" }, ["ru_ru"] = new[] { "ru-RU", "ru" },
        ["pt_br"] = new[] { "pt-BR", "pt" }, ["tr_tr"] = new[] { "tr-TR", "tr" },
        ["vi_vn"] = new[] { "vi-VN", "vi" }, ["th_th"] = new[] { "th-TH", "th" },
    };

    private static string Path_ => System.IO.Path.Combine(Config.Root, "config.json");

    // The shipped coordinates, kept as written so rescaling always starts from
    // them rather than from an already-scaled set.
    private static readonly Box[] BaseCards = Config.Cards.Values.ToArray();
    private static readonly Box[] BaseInteriors = Config.CardInteriors.Values.ToArray();
    private static readonly Box[] BaseBorders = Config.CardBorders.Values.ToArray();
    private static readonly Box[] BaseTitles = Config.CardTitles.Values.ToArray();
    private static readonly (int X, int Y)[] BaseReroll = Config.RerollBoxes.ToArray();
    private static readonly (int W, int H) BaseRerollSize = Config.RerollSize;
    private static readonly Box BaseTooltip = Config.HoverTooltip;
    private static readonly Box BaseHide = Config.HideBox;
    private static readonly (int W, int H) BaseSize = (Config.BaseW, Config.BaseH);
    private static readonly string[] SlotOrder = { "L", "M", "R" };

    public static Settings Load()
    {
        var settings = new Settings();
        try
        {
            if (!File.Exists(Path_))
                return settings;
            var json = JsonNode.Parse(File.ReadAllText(Path_));
            if (json is null)
                return settings;
            settings.Locale = json["locale"]?.GetValue<string>() ?? settings.Locale;
            settings.OcrLanguage = json["ocr_language"]?.GetValue<string>() ?? settings.OcrLanguage;
            settings.ScreenWidth = Int(json["screen_width"], settings.ScreenWidth);
            settings.ScreenHeight = Int(json["screen_height"], settings.ScreenHeight);
            settings.ObsPort = Int(json["obs_port"], settings.ObsPort);
            settings.ObsPassword = json["obs_password"]?.GetValue<string>() ?? settings.ObsPassword;
            settings.ObsSource = json["obs_source"]?.GetValue<string>() ?? settings.ObsSource;
            settings.WidgetPort = Int(json["widget_port"], settings.WidgetPort);
            settings.WidgetRows = Int(json["widget_rows"], settings.WidgetRows);
            settings.WidgetMaxWidth = Int(json["widget_max_width"], settings.WidgetMaxWidth);
        }
        catch
        {
            // An unreadable config falls back to defaults rather than refusing
            // to start; the settings tab can then write a good one over it.
        }
        return settings;
    }

    private static int Int(JsonNode? node, int fallback)
    {
        try
        {
            return node is null ? fallback : (int)node.GetValue<double>();
        }
        catch
        {
            return fallback;
        }
    }

    public void Save()
    {
        var json = new JsonObject
        {
            ["locale"] = Locale,
            ["ocr_language"] = OcrLanguage,
            ["screen_width"] = ScreenWidth,
            ["screen_height"] = ScreenHeight,
            ["obs_port"] = ObsPort,
            ["obs_password"] = ObsPassword,
            ["obs_source"] = ObsSource,
            ["widget_port"] = WidgetPort,
            ["widget_rows"] = WidgetRows,
            ["widget_max_width"] = WidgetMaxWidth,
        };
        Directory.CreateDirectory(Config.Root);
        File.WriteAllText(Path_, json.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    /// <summary>16:9 within a pixel of rounding. Anything else needs the boxes redrawn.</summary>
    public static bool IsSupportedShape(int width, int height) =>
        height > 0 && Math.Abs((double)width / height - 16.0 / 9.0) < 0.01;

    public void Apply()
    {
        string locale = Locales.Any(l => l.Code == Locale) ? Locale : "ko_kr";
        Config.Locale = locale;
        Config.OcrLanguages = OcrLanguage.Trim().Length > 0
            ? new[] { OcrLanguage.Trim() }
            : OcrForLocale.GetValueOrDefault(locale, new[] { "en-US", "en" });

        if (ScreenWidth > 0 && ScreenHeight > 0)
        {
            double sx = (double)ScreenWidth / BaseSize.W, sy = (double)ScreenHeight / BaseSize.H;
            Config.BaseW = ScreenWidth;
            Config.BaseH = ScreenHeight;
            Config.RerollBoxes = BaseReroll
                .Select(b => ((int)Math.Round(b.X * sx), (int)Math.Round(b.Y * sy))).ToArray();
            Config.RerollSize = ((int)Math.Round(BaseRerollSize.W * sx),
                                 (int)Math.Round(BaseRerollSize.H * sy));
            Config.Cards = Rescale(BaseCards, sx, sy);
            Config.CardInteriors = Rescale(BaseInteriors, sx, sy);
            Config.CardBorders = Rescale(BaseBorders, sx, sy);
            Config.CardTitles = Rescale(BaseTitles, sx, sy);
            Config.HoverTooltip = BaseTooltip.Scaled(sx, sy);
            Config.HideBox = BaseHide.Scaled(sx, sy);
        }

        Config.ObsPort = ObsPort;
        Config.ObsPassword = ObsPassword;
        Config.ObsSource = ObsSource;
        Config.WidgetPort = WidgetPort;
        Config.WidgetRows = Math.Max(1, WidgetRows);
        Config.WidgetMaxW = Math.Max(120, WidgetMaxWidth);
    }

    private static Dictionary<string, Box> Rescale(Box[] boxes, double sx, double sy) =>
        SlotOrder.Zip(boxes).ToDictionary(p => p.First, p => p.Second.Scaled(sx, sy));
}
