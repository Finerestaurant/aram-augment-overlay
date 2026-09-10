using System.Globalization;
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
/// and a 16:9 screen of any size lands on the same layout without a single box
/// moving. The resolution setting therefore touches no geometry at all -- it
/// only sets how large a frame is pulled for OCR, so a 1440p client is read at
/// its own pixels rather than through a downsample. What none of this can fix
/// is a screen that is not 16:9, where the client anchors its HUD differently.
/// </summary>
public sealed class Settings
{
    /// <summary>Which language the window itself speaks. "" follows Windows.</summary>
    public string UiLanguage { get; set; } = "";

    /// <summary>Shows the log panel. Off by default: the picks list is the
    /// answer, and the log is for working out why an answer is missing.</summary>
    public bool DebugMode { get; set; }

    /// <summary>
    /// Records every frame of every augment window and serves it back at
    /// /inspect. Off by default: a window costs 15-25 MB, and the last eight are
    /// kept. On when a pick came out wrong and the log cannot say why.
    /// </summary>
    public bool Inspector { get; set; }

    public string Locale { get; set; } = DefaultLocale();
    public int ScreenWidth { get; set; } = 1920;
    public int ScreenHeight { get; set; } = 1080;
    public int ObsPort { get; set; } = 4455;
    public string ObsPassword { get; set; } = "";          // "" -> read from OBS's own config
    public string ObsSource { get; set; } = "League of Legends";
    public int WidgetPort { get; set; } = 8777;
    public int WidgetRows { get; set; } = 4;
    public int WidgetMaxWidth { get; set; } = 420;

    /// <summary>
    /// Which widget look goes on stream: "b" HUD tray, "c" one strip, "d" game
    /// palette. Not in <see cref="NeedsRestartFrom"/> -- the widget reads it off
    /// the state it already polls, so it changes on air within a second.
    /// </summary>
    public string WidgetTheme { get; set; } = "d";

    /// <summary>
    /// Whether the loop has to be torn down and rebuilt to honour these values.
    ///
    /// The interface language and the debug switch are deliberately not in the
    /// list: both are applied the moment they are changed and neither is read by
    /// the loop, so restarting detection for them is work nobody asked for. The
    /// button that always said "save and restart" made that look compulsory,
    /// and made every other save look like it carried the same cost.
    /// </summary>
    public bool NeedsRestartFrom(Settings running) =>
        Locale != running.Locale ||
        ScreenWidth != running.ScreenWidth ||
        ScreenHeight != running.ScreenHeight ||
        ObsPort != running.ObsPort ||
        ObsPassword != running.ObsPassword ||
        ObsSource != running.ObsSource ||
        WidgetPort != running.WidgetPort ||
        WidgetRows != running.WidgetRows ||
        WidgetMaxWidth != running.WidgetMaxWidth;

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

    /// <summary>
    /// The client language to guess from the Windows display language, used only
    /// when <see cref="ClientLocale"/> found nothing on disk to read.
    ///
    /// This is a guess and a poor one -- a player on an English Windows with a
    /// Korean client is the case that started all this -- but it beats assuming
    /// one country outright.
    ///
    /// Riot's regional splits are not symmetric -- Chinese splits on script,
    /// Spanish on continent, Portuguese does not split at all -- so the cases
    /// that matter are written out rather than derived from the culture name.
    /// A language with no folder of its own lands on English, which a player is
    /// far more likely to read than whatever we picked first.
    /// </summary>
    /// <summary>
    /// The client language to start from when nobody has chosen one: what the
    /// League install says, and only failing that, what Windows is set to.
    /// </summary>
    public static string DefaultLocale() => ClientLocale.Detect() ?? SystemLocale();

    public static string SystemLocale()
    {
        CultureInfo culture = CultureInfo.CurrentUICulture;
        string lang = culture.TwoLetterISOLanguageName.ToLowerInvariant();

        string region = "";
        try { region = new RegionInfo(culture.Name).TwoLetterISORegionName.ToLowerInvariant(); }
        catch (ArgumentException) { }          // a neutral culture carries no region

        switch (lang)
        {
            case "zh":
                bool traditional =
                    culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                    || region is "tw" or "hk" or "mo";
                return traditional ? "zh_tw" : "zh_cn";
            case "es":
                return region == "es" ? "es_es" : "es_mx";
            case "pt":
                return "pt_br";                // Riot ships no European Portuguese
            case "en":
                return "en_us";
        }

        string exact = lang + "_" + region;
        if (Array.Exists(Locales, l => l.Code == exact))
            return exact;

        foreach (var (code, _) in Locales)     // right language, whatever region is listed
            if (code.StartsWith(lang + "_", StringComparison.Ordinal))
                return code;

        return "en_us";
    }

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

    /// <summary>
    /// The Windows capability name for a locale, which is not the tag the OCR
    /// engine reports. Windows installs Language.OCR~~~zh-CN while the engine
    /// lists the result as zh-Hans-CN; asking to install the engine's tag fails
    /// silently, because no such capability exists.
    /// </summary>
    public static string CapabilityTag(string locale)
    {
        var parts = locale.Split('_');
        return parts.Length == 2
            ? $"{parts[0]}-{parts[1].ToUpperInvariant()}"
            : locale;
    }

    private static string Path_ => System.IO.Path.Combine(Config.Root, "config.json");

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
            settings.UiLanguage = json["ui_language"]?.GetValue<string>() ?? settings.UiLanguage;
            settings.DebugMode = json["debug_mode"]?.GetValue<bool>() ?? settings.DebugMode;
            settings.Inspector = json["inspector"]?.GetValue<bool>() ?? settings.Inspector;
            settings.Locale = json["locale"]?.GetValue<string>() ?? settings.Locale;
            // ocr_language used to be a separate setting. It is read no more:
            // choosing a recogniser that did not match the client language read
            // nothing at all, and there was no legitimate reason to differ.
            settings.ScreenWidth = Int(json["screen_width"], settings.ScreenWidth);
            settings.ScreenHeight = Int(json["screen_height"], settings.ScreenHeight);
            settings.ObsPort = Int(json["obs_port"], settings.ObsPort);
            settings.ObsPassword = json["obs_password"]?.GetValue<string>() ?? settings.ObsPassword;
            settings.ObsSource = json["obs_source"]?.GetValue<string>() ?? settings.ObsSource;
            settings.WidgetPort = Int(json["widget_port"], settings.WidgetPort);
            settings.WidgetRows = Int(json["widget_rows"], settings.WidgetRows);
            settings.WidgetMaxWidth = Int(json["widget_max_width"], settings.WidgetMaxWidth);
            settings.WidgetTheme = json["widget_theme"]?.GetValue<string>() ?? settings.WidgetTheme;
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
            ["ui_language"] = UiLanguage,
            ["debug_mode"] = DebugMode,
            ["inspector"] = Inspector,
            ["locale"] = Locale,
            ["screen_width"] = ScreenWidth,
            ["screen_height"] = ScreenHeight,
            ["obs_port"] = ObsPort,
            ["obs_password"] = ObsPassword,
            ["obs_source"] = ObsSource,
            ["widget_port"] = WidgetPort,
            ["widget_rows"] = WidgetRows,
            ["widget_max_width"] = WidgetMaxWidth,
            ["widget_theme"] = WidgetTheme,
        };
        Directory.CreateDirectory(Config.Root);
        File.WriteAllText(Path_, json.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    // A 16:9 check used to live here, on the belief that anything else needed
    // the boxes redrawn. It does not: the client lays the augment screen out by
    // height and centres it, so one scale and one offset carry every shape
    // (Detect.Geometry). Nothing asks the question any more, and leaving a
    // predicate called "IsSupportedShape" behind would answer a question the
    // tool no longer has.

    public void Apply()
    {
        Strings.Language = UiLanguage.Length > 0 ? UiLanguage : Strings.SystemDefault();

        string locale = Locales.Any(l => l.Code == Locale) ? Locale : DefaultLocale();
        Config.Locale = locale;
        Config.OcrLanguages = OcrForLocale.GetValueOrDefault(locale, new[] { "en-US", "en" });

        // No box is touched here. The geometry is proportional to the frame
        // (Detect.Scale), so rescaling it for the screen was a no-op on 16:9
        // and, because it also moved BaseW/BaseH under the tooltip finder's
        // constants, a regression everywhere else. The screen size decides one
        // thing: how large a frame to pull for OCR.
        if (ScreenWidth > 0 && ScreenHeight > 0)
        {
            Config.OcrW = ScreenWidth;
            Config.OcrH = ScreenHeight;
        }

        Config.DebugMode = DebugMode;
        Config.InspectorOn = Inspector;
        Observe.Attach(Inspector);
        Config.ObsPort = ObsPort;
        Config.ObsPassword = ObsPassword;
        Config.ObsSource = ObsSource;
        Config.WidgetPort = WidgetPort;
        Config.WidgetRows = Math.Max(1, WidgetRows);
        Config.WidgetMaxW = Math.Max(120, WidgetMaxWidth);
        Config.WidgetTheme = Array.IndexOf(Config.WidgetThemes, WidgetTheme) >= 0
            ? WidgetTheme : "d";
    }
}
