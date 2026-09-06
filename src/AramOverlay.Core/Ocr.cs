using Windows.Globalization;
using Windows.Media.Ocr;

namespace AramOverlay.Core;

/// <summary>
/// Tooltip and card-title OCR on the engine built into Windows.
///
/// Measured against the five captures with known answers, the Windows engine
/// read 5/5 exactly at 6 ms per crop, where an ONNX recogniser managed 4/5 at
/// 87 ms and needed 64 MB of wheels. It also ignores the augment icon sitting
/// at the left of the crop, which the ONNX model turned into leading junk.
///
/// Native resolution first, upscaling only as a retry: 상급 조준경 부착 reads
/// exactly at 1x and collapses to nonsense at 3x, while 과충전 comes back empty
/// until 3x. Retrying only when 1x reads nothing costs the extra call on the
/// rare frame that needs it.
/// </summary>
public sealed class TooltipOcr
{
    private readonly OcrEngine _engine;

    private TooltipOcr(OcrEngine engine) => _engine = engine;

    public static IReadOnlyList<string> InstalledLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Whether the recogniser has this language, comparing the way Windows
    /// reports it rather than the way it is installed: the capability is called
    /// ja-JP but the engine lists it as ja, and an exact match never fires.
    /// </summary>
    public static bool HasLanguage(string capabilityTag)
    {
        var installed = InstalledLanguages();
        if (installed.Contains(capabilityTag))
            return true;
        string prefix = capabilityTag.Split('-')[0];
        return installed.Any(l => l == prefix || l.StartsWith(prefix + "-", StringComparison.Ordinal));
    }

    /// <summary>The engine for the configured language, or null when its pack is missing.</summary>
    public static TooltipOcr? TryCreate()
    {
        foreach (string tag in Config.OcrLanguages)
        {
            try
            {
                var language = new Language(tag);
                if (!OcrEngine.IsLanguageSupported(language))
                    continue;
                var engine = OcrEngine.TryCreateFromLanguage(language);
                if (engine is not null)
                    return new TooltipOcr(engine);
            }
            catch
            {
                // An unknown tag throws rather than returning false; try the next.
            }
        }
        return null;
    }

    public static string MissingLanguageMessage()
    {
        string have = InstalledLanguages().Count > 0
            ? string.Join(", ", InstalledLanguages()) : Strings.Get("Ocr.None");
        string want = Config.OcrLanguages.FirstOrDefault() ?? "?";
        return Strings.Get("Ocr.LanguageMissing", want, have);
    }

    /// <summary>One reading of one region, at one scale.</summary>
    public async Task<string> ReadBoxAsync(Frame frame, Box box, int scale = 1)
    {
        var crop = frame.Crop(frame.Fit(box));
        if (crop.Width == 0 || crop.Height == 0)
            return "";
        if (scale != 1)
            crop = Imaging.Resize(crop, crop.Width * scale, crop.Height * scale);

        try
        {
            using var bitmap = Imaging.ToSoftwareBitmap(crop);
            var result = await _engine.RecognizeAsync(bitmap);
            return string.Join(" ", result.Lines.Select(l => l.Text)).Trim();
        }
        catch (Exception exc)
        {
            Log.Write(Strings.Get("Ocr.CallFailed", exc.GetType().Name, exc.Message));
            return "";
        }
    }
}
