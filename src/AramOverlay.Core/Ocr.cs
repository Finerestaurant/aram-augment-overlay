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
            ? string.Join(", ", InstalledLanguages()) : "(없음)";
        string want = Config.OcrLanguages.FirstOrDefault() ?? "?";
        return $"Windows OCR에 '{want}' 언어가 없습니다.\n" +
               $"    현재 사용 가능한 언어: {have}\n" +
               "    설정 탭에서 사용 가능한 언어를 고르거나,\n" +
               "    관리자 PowerShell에서 다음을 실행한 뒤 다시 시도하세요:\n" +
               $"    Add-WindowsCapability -Online -Name 'Language.OCR~~~{want}~0.0.1.0'";
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
            Log.Write($"  OCR 호출 실패: {exc.GetType().Name}: {exc.Message}");
            return "";
        }
    }
}
