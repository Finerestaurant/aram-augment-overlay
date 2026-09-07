using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AramOverlay.Core;

// Holds the port to the Python implementation it replaces. Run from the repo
// root, or pass the repo root as the first argument.
//
//     dotnet run --project dotnet/AramOverlay.SelfTest

// Korean log lines otherwise land in the console's legacy code page, which turns
// into mojibake the moment the output is redirected to a file.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* no console */ }

var root = args.FirstOrDefault(a => !a.StartsWith("--")) ?? FindRepoRoot();
int failures = 0;

// Live check against a running OBS. Kept behind a flag: the offline parity
// tests are what CI-style runs care about, and this one needs OBS up.
if (args.Contains("--obs"))
    return await ObsLive(root);

// What the app would pick for a client language on this machine, and where each
// answer came from. "My augments are not recognised" is usually this being
// wrong, and it is a great deal easier to read than to reason about.
if (args.Contains("--locale"))
    return LocaleReport();

// Score a folder of frames the way the loop scores them, so the selection-flare
// thresholds can be checked against pictures whose answer is already known --
// captures of a pick, and dumps from windows that went wrong.
//
//     dotnet run --project src/AramOverlay.SelfTest -- --flare <folder>
if (args.Contains("--flare"))
    return await FlareReport(args.FirstOrDefault(a => !a.StartsWith("--")) ?? ".");

// Where the tooltip panel finder puts the panel on each frame in a folder.
//
//     dotnet run --project src/AramOverlay.SelfTest -- --tooltip <folder>
if (args.Contains("--tooltip"))
    return await TooltipReport(args.FirstOrDefault(a => !a.StartsWith("--")) ?? ".");

// Replay a folder of frames as though it were one augment window: run the same
// gate, the same measurements and the same verdict the loop would, and leave the
// same annotated trace behind. A recording of a pick can then be checked against
// what the tool would have published, without waiting for the situation to come
// round again in a real game.
//
//     dotnet run --project src/AramOverlay.SelfTest -- --replay <folder>
if (args.Contains("--replay"))
    return await Replay(args.FirstOrDefault(a => !a.StartsWith("--")) ?? ".",
                        FlagValue(args, "--fps", 60.0));

// The whole thing, headless, until Ctrl+C. This is the loop the WPF window will
// host; running it from a console first keeps the two concerns separate.
if (args.Contains("--loop"))
{
    Config.Root = root;
    Log.StartFile();
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    await using var host = new OverlayHost();
    return await host.RunAsync(stop.Token);
}

failures += DifflibParity(Path.Combine(root, "tests", "difflib_parity.json"));
failures += HangulShaping();
failures += await AugmentPool(root);
failures += await OcrParity(root);
failures += await DetectParity(root);

Console.WriteLine(failures == 0 ? "\n전부 통과" : $"\n{failures}개 실패");
return failures == 0 ? 0 : 1;

// The panel finder over a folder, one line per frame. Answers whose frames are
// known are the point: a fixed box could only ever be right about the ordinary
// case, and this has to be right about the flipped one too.
static async Task<int> TooltipReport(string folder)
{
    if (!Directory.Exists(folder))
    {
        Console.WriteLine($"폴더가 없습니다: {folder}");
        return 1;
    }
    var files = Directory.EnumerateFiles(folder)
        .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToArray();
    Console.WriteLine($"앵커 y={Config.TooltipAnchor} ±{Config.TooltipAnchorTol}, " +
                      $"최소 폭 {Config.TooltipMinWidth}, 중심 {Config.TooltipCentre}±{Config.TooltipCentreTol}\n");
    Console.WriteLine($"{"파일",-34}  {"판정",-10} {"상단",5} {"좌",5} {"우",5} {"폭",5}  제목 영역");
    var marks = new List<(string File, Frame Frame, Detect.TooltipPanel? Found)>();
    foreach (string file in files)
    {
        Frame frame;
        try { frame = await Imaging.DecodeAsync(await File.ReadAllBytesAsync(file)); }
        catch (Exception exc)
        {
            Console.WriteLine($"{Path.GetFileName(file),-34}  디코드 실패: {exc.Message}");
            continue;
        }
        var found = Detect.FindTooltip(Cv.ToGray(frame));
        if (found is not { } p)
        {
            Console.WriteLine($"{Path.GetFileName(file),-34}  {"툴팁 없음",-10}");
            marks.Add((file, frame, found));
            continue;
        }
        Console.WriteLine($"{Path.GetFileName(file),-34}  {(p.Flipped ? "뒤집힘" : "아래"),-10} " +
                          $"{p.Top,5} {p.X0,5} {p.X1,5} {p.X1 - p.X0,5}  " +
                          $"({p.Title.X0},{p.Title.Y0})~({p.Title.X1},{p.Title.Y1})");
        marks.Add((file, frame, found));
    }

    // The same picture the loop writes at run time, so what is checked here and
    // what shows up in state\debug are not two different drawings.
    string outDir = Path.Combine(folder, "marked");
    Directory.CreateDirectory(outDir);
    foreach (var (file, frame, found) in marks)
    {
        Detect.Mark(frame, found);
        await Imaging.SavePngAsync(frame,
            Path.Combine(outDir, "mark_" + Path.GetFileNameWithoutExtension(file) + ".png"));
    }
    Console.WriteLine($"\n표시한 화면: {outDir}");
    return 0;
}

static double FlagValue(string[] argv, string name, double fallback)
{
    int at = Array.IndexOf(argv, name);
    return at >= 0 && at + 1 < argv.Length &&
           double.TryParse(argv[at + 1], NumberStyles.Float, CultureInfo.InvariantCulture,
                           out double value)
        ? value : fallback;
}

// A folder of frames run through the loop's own decision path, start to finish.
//
// The frames are timestamped from the given frame rate, which is what makes the
// two time-based tests -- the flare's own baseline, and how far back the search
// reaches -- mean the same thing here as they do live. Gate scores are computed
// rather than assumed, so a frame the reroll buttons have left is excluded from
// the brightness fallback exactly as it would be in a game.
static async Task<int> Replay(string folder, double fps)
{
    if (!Directory.Exists(folder))
    {
        Console.WriteLine($"폴더가 없습니다: {folder}");
        return 1;
    }
    var files = Directory.EnumerateFiles(folder)
        .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToArray();
    if (files.Length == 0)
    {
        Console.WriteLine($"이미지가 없습니다: {folder}");
        return 1;
    }

    Config.Root = folder;
    Config.TraceMode = true;
    Config.DebugMode = true;
    var gate = await Assets.GateAsync();
    var hide = await Assets.HideButtonAsync();

    var history = new List<Beat>();
    var start = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    Trace.Begin(null);
    Console.WriteLine($"{files.Length}개 프레임을 {fps} fps 로 재생합니다 -> {Trace.Dir}\n");

    for (int i = 0; i < files.Length; i++)
    {
        var frame = await Imaging.DecodeAsync(await File.ReadAllBytesAsync(files[i]));
        var gray = Cv.ToGray(frame);
        var scores = gate.Scores(gray);
        double hideScore = hide.Score(gray);
        bool cardsUp = scores.Min() >= Config.GateStay ||
                       scores.Count(s => s >= Config.GateStayTwo) >= 2;
        bool alive = cardsUp || hideScore >= Config.HidePresent;
        var stats = Detect.CardStats(gray);
        var at = start.AddSeconds(i / fps);
        history.Add(new Beat(at, stats, alive, cardsUp));

        var (slot, top, ratio) = Detect.Flare(stats);
        Trace.Add(frame, new TraceSample
        {
            Index = i,
            T = i / fps,
            Gate = scores,
            Hide = hideScore,
            Alive = alive,
            CardsUp = cardsUp,
            Settled = Detect.InteriorDarkness(gray) < 60,
            Stats = stats,
            Flare = $"{top} {ratio:F2}X{(slot is null ? "" : " HIT")}",
        });
    }

    // The close is taken as the last frame the window was alive in, which is
    // what the loop does rather than "now".
    DateTime closedAt = history[^1].At;
    for (int i = history.Count - 1; i >= 0; i--)
        if (history[i].Alive) { closedAt = history[i].At; break; }

    var (flareSlot, flareAt, flareRatio, flareRise) = OverlayLoop.SelectionFlare(history, closedAt);
    var (glowSlot, _, glowVia) = OverlayLoop.HoverGlowSlot(history, closedAt);

    var why = new List<string>
    {
        $"frames      {files.Length} at {fps} fps",
        $"close taken at frame {(int)Math.Round((closedAt - start).TotalSeconds * fps)}",
        flareSlot is null
            ? "flare       none"
            : $"flare       {flareSlot}  inner {flareRatio:F2}x the next card, " +
              $"{flareRise:F2}x its own baseline, " +
              $"{(closedAt - flareAt).TotalSeconds:F2}s before the close",
        glowSlot is null ? "hover glow  none" : $"hover glow  {glowSlot}  {glowVia}",
        "tooltip     not available in a replay (no OCR probe ran)",
        flareSlot is not null ? $"would take  {flareSlot} on the flare"
            : glowSlot is not null ? $"would take  {glowSlot} on hover brightness"
            : "would take  nothing",
    };
    foreach (string line in why)
        Console.WriteLine("  " + line);
    await Trace.FinishAsync(why);
    Console.WriteLine($"\n트레이스: {Path.Combine(folder, "state", "trace")}");
    return 0;
}

// Every frame in a folder, measured the way the loop measures one, with the
// selection-flare verdict beside it. Prints the ratio and the bright fraction
// even when they fail, because the useful question is usually how close a
// negative came rather than which ones passed.
static async Task<int> FlareReport(string folder)
{
    if (!Directory.Exists(folder))
    {
        Console.WriteLine($"폴더가 없습니다: {folder}");
        return 1;
    }
    var files = Directory.EnumerateFiles(folder)
        .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToArray();
    if (files.Length == 0)
    {
        Console.WriteLine($"이미지가 없습니다: {folder}");
        return 1;
    }

    // Only the across-cards half of the flare test can be judged from a still.
    // The other half asks how far the card has risen since earlier in its own
    // window, and a folder of unrelated pictures has no window to compare with.
    Console.WriteLine($"프레임 단위 검사: inner 비율 {Config.FlareInnerRatio}배 이상");
    Console.WriteLine("(자기 기준선 대비 상승 조건은 창의 이력이 필요하므로 여기서는 판정하지 않습니다)\n");
    Console.WriteLine("파일                                      " +
                      "L mean/in/br        M mean/in/br        R mean/in/br        top  inner  판정");

    int flares = 0;
    foreach (string file in files)
    {
        Frame frame;
        try
        {
            frame = await Imaging.DecodeAsync(await File.ReadAllBytesAsync(file));
        }
        catch (Exception exc)
        {
            Console.WriteLine($"{Path.GetFileName(file),-40}  디코드 실패: {exc.Message}");
            continue;
        }
        var stats = Detect.CardStats(Cv.ToGray(frame));
        var (slot, top, ratio) = Detect.Flare(stats);
        if (slot is not null)
            flares++;

        var cells = new List<string>();
        foreach (string key in new[] { "L", "M", "R" })
        {
            var v = stats[key];
            cells.Add($"{v.Mean,5:F1}/{v.Inner,5:F1}/{v.Bright,4:F1}");
        }
        Console.WriteLine($"{Path.GetFileName(file),-40}  {string.Join("  ", cells)}  " +
                          $"{top}  {ratio,5:F2}  " +
                          $"{(slot is null ? "-" : "RATIO PASSES " + slot)}");
    }

    Console.WriteLine($"\n{files.Length}개 중 {flares}개가 inner 비율 조건을 통과했습니다.");
    return 0;
}

// End to end against the real thing: connect, find the capture source, pull a
// frame, run the gate on it, and serve the widget page the OBS browser source
// would load.
static async Task<int> ObsLive(string root)
{
    Config.Root = root;
    var cfg = ObsCapture.ReadWebsocketConfig();
    string password = cfg?["server_password"]?.GetValue<string>() ?? "";
    int port = cfg?["server_port"]?.GetValue<int>() ?? Config.ObsPort;
    Console.WriteLine(password.Length > 0
        ? "OBS 설정 파일에서 websocket 비밀번호를 읽었습니다."
        : "websocket 비밀번호가 비어 있습니다.");

    await using var obs = await ObsCapture.ConnectAsync(port: port, password: password);
    Console.WriteLine("PASS  연결: " + await obs.VersionAsync());
    Console.WriteLine($"PASS  게임 캡처 소스 '{obs.Source}' 존재: {await obs.HasSourceAsync()}");

    var frame = await obs.GrabAsync();
    if (frame is null)
    {
        Console.WriteLine("정보  캡처가 비어 있습니다 (게임이 후킹되지 않음). 프레임 검사는 건너뜁니다.");
    }
    else
    {
        Console.WriteLine($"PASS  프레임 {frame.Width}x{frame.Height}");
        var gate = await Assets.GateAsync();
        var gray = Cv.ToGray(frame);
        var scores = gate.Scores(gray);
        Console.WriteLine($"PASS  게이트 [{string.Join(", ", scores.Select(s => s.ToString("F2")))}] " +
                          $"-> 창 {(scores.All(s => s >= Config.GateOpen) ? "열림" : "닫힘")}");
    }

    // A spare port, so this never fights the overlay that may be running.
    var state = new RunState { GameMode = "KIWI", Connected = true, Level = 7 };
    state.Picks.Add(new Pick { Name = "이빨 요정", Rarity = "gold", Level = 7 });
    string template = Assets.Text("widget.html");
    using var server = new WidgetServer(state, template, port: 8795);
    string url = server.Start();

    using var http = new HttpClient();
    string page = await http.GetStringAsync(url);
    string stateJson = await http.GetStringAsync(url + "state.json");
    Console.WriteLine($"PASS  위젯 {page.Length}바이트, 설정 주입 {page.Contains("__CFG__")}");
    Console.WriteLine($"PASS  상태 {stateJson}");

    var reset = await http.PostAsync(url + "reset", new StringContent(""));
    Console.WriteLine($"PASS  초기화 후 증강 {state.Picks.Count}개");
    reset.Dispose();

    var refreshed = await obs.RefreshBrowserSourcesAsync(url);
    Console.WriteLine($"정보  이 주소를 보는 브라우저 소스: {refreshed.Count}개");
    return 0;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

static int DifflibParity(string path)
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"FAIL  대조 데이터가 없습니다: {path}");
        Console.WriteLine("      python scripts/gen_parity.py 로 생성하세요.");
        return 1;
    }

    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    int checked_ = 0, bad = 0;
    double worst = 0;
    string worstPair = "";
    foreach (var row in doc.RootElement.EnumerateArray())
    {
        string a = row.GetProperty("a").GetString()!;
        string b = row.GetProperty("b").GetString()!;
        double expected = row.GetProperty("ratio").GetDouble();
        double got = Difflib.Ratio(a, b);
        double diff = Math.Abs(got - expected);
        checked_++;
        if (diff > worst)
        {
            worst = diff;
            worstPair = $"'{a}' vs '{b}': python {expected:R}, c# {got:R}";
        }
        if (diff > 1e-12)
            bad++;
    }

    Console.WriteLine(bad == 0
        ? $"PASS  difflib 일치 {checked_}쌍 (최대 오차 {worst:E2})"
        : $"FAIL  difflib 불일치 {bad}/{checked_}쌍, 최악: {worstPair}");
    return bad == 0 ? 0 : 1;
}

// A level 11 pick of 적응형 능력치 was published as 이동 속도, which is not an
// augment at all -- it is Arena's Stat_Movespeed shard, riding along in
// cherry-augments.json. Short names like that match misread text hard, so the
// pool must hold only what can actually appear on a Mayhem card.
static async Task<int> AugmentPool(string root)
{
    Config.Root = root;
    AugmentDb db;
    try
    {
        db = await AugmentDb.LoadAsync();
    }
    catch (Exception exc)
    {
        Console.WriteLine($"SKIP  증강 목록 — 캐시도 네트워크도 없습니다 ({exc.GetType().Name})");
        return 0;
    }

    int bad = 0;
    var rarities = db.Augments.Select(a => a.Rarity).Distinct().OrderBy(r => r).ToArray();
    if (!rarities.SequenceEqual(new[] { "gold", "prismatic", "silver" }))
    {
        Console.WriteLine($"FAIL  등급이 셋뿐이어야 합니다: {string.Join(", ", rarities)}");
        bad++;
    }

    // Names that must never be reachable: stat shards and event picks.
    foreach (string name in new[] { "이동 속도", "방어력", "스킬 가속", "증강 슬롯 획득", "증강 교체" })
    {
        if (db.Augments.Any(a => a.Name == name))
        {
            Console.WriteLine($"FAIL  '{name}' 은 증강이 아닌데 후보에 있습니다");
            bad++;
        }
    }

    // ...and one that must still be, exactly.
    var (aug, score) = db.Match("적응형 능력치");
    if (aug?.Name != "적응형 능력치" || score < 0.999)
    {
        Console.WriteLine($"FAIL  '적응형 능력치' 가 {aug?.Name} ({score:F2}) 로 매칭됩니다");
        bad++;
    }

    if (bad == 0)
        Console.WriteLine($"PASS  증강 후보 {db.Augments.Count}종, 능력치 파편·이벤트 선택지 제외됨");
    return bad == 0 ? 0 : 1;
}

// The port decodes frames with WinRT instead of PIL and upscales with its own
// Lanczos, so the recogniser is being fed pixels that came a different way. The
// text it returns still has to be identical, because the match thresholds were
// measured against these exact strings.
static async Task<int> OcrParity(string root)
{
    string path = Path.Combine(root, "tests", "ocr_expect.json");
    if (!File.Exists(path))
    {
        Console.WriteLine($"FAIL  OCR 대조 데이터가 없습니다: {path}");
        Console.WriteLine("      python scripts/gen_ocr_expect.py 로 생성하세요.");
        return 1;
    }

    var ocr = TooltipOcr.TryCreate();
    if (ocr is null)
    {
        // The Korean OCR pack ships with Korean Windows and is absent from the
        // English images CI runs on. Skipping keeps the rest of the suite
        // meaningful there; on a machine that can actually run the tool, the
        // engine is present and this runs.
        Console.WriteLine("SKIP  OCR 대조 — 이 PC에 해당 언어 팩이 없습니다 " +
                          $"(사용 가능: {string.Join(", ", TooltipOcr.InstalledLanguages())})");
        return 0;
    }

    Config.Root = root;                       // find the cache Python already filled
    var db = await AugmentDb.LoadAsync();

    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var frames = new Dictionary<string, Frame>();
    int checked_ = 0, sameText = 0, bad = 0;
    var mismatches = new List<string>();

    foreach (var row in doc.RootElement.EnumerateArray())
    {
        string file = row.GetProperty("file").GetString()!;
        if (!frames.TryGetValue(file, out var frame))
        {
            string full = Path.Combine(root, "tests", "fixtures", file);
            frames[file] = frame = await Imaging.DecodeAsync(await File.ReadAllBytesAsync(full));
        }
        var b = row.GetProperty("box");
        var box = new Box(b[0].GetInt32(), b[1].GetInt32(), b[2].GetInt32(), b[3].GetInt32());
        int scale = row.GetProperty("scale").GetInt32();
        string slot = row.GetProperty("slot").GetString()!;
        string expectedText = row.GetProperty("text").GetString()!;
        string? expectedMatch = row.GetProperty("match").GetString();
        bool expectedAccepted = row.GetProperty("accepted").GetBoolean();

        string got = await ocr.ReadBoxAsync(frame, box, scale);
        var (aug, score) = db.Match(got);
        bool accepted = score >= Config.OcrMinScore;
        checked_++;
        if (got == expectedText)
            sameText++;

        // What has to agree is the decision. Upscaled garbage that neither
        // implementation can read is allowed to differ by a glyph, as long as
        // both land on the same augment and both reject it.
        if (aug?.Name != expectedMatch || accepted != expectedAccepted)
        {
            bad++;
            if (mismatches.Count < 8)
                mismatches.Add($"      {file} {slot} x{scale}: " +
                               $"python '{expectedText}' -> {expectedMatch} ({expectedAccepted}) / " +
                               $"c# '{got}' -> {aug?.Name} ({accepted})");
        }
    }

    if (bad == 0)
    {
        Console.WriteLine($"PASS  OCR 판정 일치 {checked_}건 (문자열까지 동일 {sameText}건)");
        return 0;
    }
    Console.WriteLine($"FAIL  OCR 판정 불일치 {bad}/{checked_}건");
    mismatches.ForEach(Console.WriteLine);
    return 1;
}

// The gate thresholds are measurements of what OpenCV returned on these frames,
// so the reimplemented cvtColor / Sobel / INTER_AREA / matchTemplate have to
// land on the same numbers. Scores are allowed a hair of drift from resampling;
// every verdict drawn from them must be identical.
static async Task<int> DetectParity(string root)
{
    string path = Path.Combine(root, "tests", "detect_expect.json");
    if (!File.Exists(path))
    {
        Console.WriteLine($"FAIL  감지 대조 데이터가 없습니다: {path}");
        Console.WriteLine("      python scripts/gen_detect_expect.py 로 생성하세요.");
        return 1;
    }

    // WinRT imaging is not guaranteed on a Windows Server CI image, where the
    // media stack is optional. Fail loudly on a machine that can run the tool,
    // skip where the platform simply cannot decode.
    TemplateGate gate;
    HideButton hide;
    try
    {
        gate = await Assets.GateAsync();
        hide = await Assets.HideButtonAsync();
    }
    catch (Exception exc)
    {
        Console.WriteLine($"SKIP  감지 대조 — 이 환경에서 이미지 디코딩 불가 ({exc.GetType().Name})");
        return 0;
    }

    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var decoded = new Dictionary<string, Frame>();
    int checked_ = 0, bad = 0;
    double worstGate = 0, worstMean = 0, worstDecoder = 0;
    var problems = new List<string>();

    foreach (var row in doc.RootElement.EnumerateArray())
    {
        string file = row.GetProperty("file").GetString()!;
        string size = row.GetProperty("size").GetString()!;
        if (!decoded.TryGetValue(file, out var full))
        {
            string p = Path.Combine(root, "tests", "fixtures", file);
            decoded[file] = full = await Imaging.DecodeAsync(await File.ReadAllBytesAsync(p));
        }
        var frame = size == "det" ? Imaging.ResizeArea(full, Config.DetW, Config.DetH) : full;
        var gray = Cv.ToGray(frame);
        string where = $"{file} [{size}]";
        checked_++;

        // Decoder check first: WinRT and OpenCV do not have to agree bit for bit
        // on a JPEG, and if they disagree here nothing downstream can be exact.
        double[] bgr = new double[3];
        for (int p = 0; p < frame.Bgra.Length; p += 4)
            for (int c = 0; c < 3; c++)
                bgr[c] += frame.Bgra[p + c];
        long pixels = (long)frame.Width * frame.Height;
        var wantBgr = row.GetProperty("bgr_mean").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        double decoderDrift = 0;
        for (int c = 0; c < 3; c++)
            decoderDrift = Math.Max(decoderDrift, Math.Abs(bgr[c] / pixels - wantBgr[c]));
        worstDecoder = Math.Max(worstDecoder, decoderDrift);

        // Two sources of +/-1 per pixel are unavoidable and documented: the JPEG
        // decoders disagree, and OpenCV's cvtColor is not reproducible in closed
        // form (see Cv.ToGray). Both land far under the margins the thresholds
        // are built on, so brightness gets a tolerance and the verdicts do not.
        double grayMean = gray.Pixels.Aggregate(0.0, (acc, v) => acc + v) / gray.Pixels.Length;
        double brightnessTolerance = 0.05;
        if (Math.Abs(grayMean - row.GetProperty("gray_mean").GetDouble()) > brightnessTolerance)
        {
            problems.Add($"      {where} 그레이스케일 평균 {grayMean:F6} / " +
                         $"{row.GetProperty("gray_mean").GetDouble():F6}");
            bad++;
        }

        var gotGate = gate.Scores(gray);
        var wantGate = row.GetProperty("gate").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        for (int i = 0; i < 3; i++)
        {
            worstGate = Math.Max(worstGate, Math.Abs(gotGate[i] - wantGate[i]));
            if (Math.Abs(gotGate[i] - wantGate[i]) > 0.01)
            {
                problems.Add($"      {where} 게이트[{i}] {gotGate[i]:F4} / {wantGate[i]:F4}");
                bad++;
            }
        }
        // The verdicts the thresholds actually drive.
        bool gotOpen = gotGate.All(s => s >= Config.GateOpen);
        bool wantOpen = wantGate.All(s => s >= Config.GateOpen);
        if (gotOpen != wantOpen)
        {
            problems.Add($"      {where} 게이트 판정 {gotOpen} / {wantOpen}");
            bad++;
        }

        double gotHide = hide.Score(gray), wantHide = row.GetProperty("hide").GetDouble();
        worstGate = Math.Max(worstGate, Math.Abs(gotHide - wantHide));
        if (Math.Abs(gotHide - wantHide) > 0.01 ||
            gotHide >= Config.HidePresent != wantHide >= Config.HidePresent)
        {
            problems.Add($"      {where} 숨김버튼 {gotHide:F4} / {wantHide:F4}");
            bad++;
        }

        var means = Detect.CardMeans(gray);
        foreach (var slot in new[] { "L", "M", "R" })
        {
            double want = row.GetProperty("means").GetProperty(slot).GetDouble();
            worstMean = Math.Max(worstMean, Math.Abs(means[slot] - want));
            if (Math.Abs(means[slot] - want) > brightnessTolerance)
            {
                problems.Add($"      {where} 카드평균[{slot}] {means[slot]:F6} / {want:F6}");
                bad++;
            }
        }

        double interior = Detect.InteriorDarkness(gray);
        if (Math.Abs(interior - row.GetProperty("interior").GetDouble()) > brightnessTolerance)
        {
            problems.Add($"      {where} 카드내부 {interior:F6} / " +
                         $"{row.GetProperty("interior").GetDouble():F6}");
            bad++;
        }

        var (slotGot, spreadGot) = Detect.HoveredCard(means);
        string? slotWant = row.GetProperty("hover").ValueKind == JsonValueKind.Null
            ? null : row.GetProperty("hover").GetString();
        if (slotGot != slotWant || Math.Abs(spreadGot - row.GetProperty("spread").GetDouble()) > brightnessTolerance)
        {
            problems.Add($"      {where} 호버 {slotGot}/{spreadGot:F4} / {slotWant}/" +
                         $"{row.GetProperty("spread").GetDouble():F4}");
            bad++;
        }

        foreach (var slot in new[] { "L", "M", "R" })
        {
            string gotRarity = Detect.RarityOf(frame, slot);
            string wantRarity = row.GetProperty("rarity").GetProperty(slot).GetString()!;
            if (gotRarity != wantRarity)
            {
                problems.Add($"      {where} 등급[{slot}] {gotRarity} / {wantRarity}");
                bad++;
            }
        }
    }

    if (bad == 0)
    {
        Console.WriteLine($"PASS  감지 일치 {checked_}프레임 " +
                          $"(점수 최대 오차 {worstGate:F5}, 밝기 {worstMean:E1})");
        return 0;
    }
    Console.WriteLine($"FAIL  감지 불일치 {bad}건 / {checked_}프레임");
    problems.Take(14).ToList().ForEach(Console.WriteLine);
    if (problems.Count > 14)
        Console.WriteLine($"      ... 외 {problems.Count - 14}건");
    return 1;
}

static int HangulShaping()
{
    (string input, string squashed, string stripped)[] cases =
    {
        // StripFinal leaves spacing alone; Squash is what removes it.
        ("끝없는 학살", "끝없는학살", "끄어느 하사"),
        ("범람", "범람", "버라"),
        ("핀볼", "핀볼", "피보"),
        ("상급 조준경 부착", "상급조준경부착", "사그 조주겨 부차"),
        ("ARAM 2", "ARAM2", "ARAM 2"),
        ("", "", ""),
    };

    int bad = 0;
    foreach (var (input, squashed, stripped) in cases)
    {
        string gotSquash = Hangul.Squash(input);
        string gotStrip = Hangul.StripFinal(input);
        if (gotSquash != squashed)
        {
            Console.WriteLine($"FAIL  Squash('{input}') -> '{gotSquash}', 기대 '{squashed}'");
            bad++;
        }
        if (gotStrip != stripped)
        {
            Console.WriteLine($"FAIL  StripFinal('{input}') -> '{gotStrip}', 기대 '{stripped}'");
            bad++;
        }
    }
    if (bad == 0)
        Console.WriteLine("PASS  한글 정규화");
    return bad == 0 ? 0 : 1;
}

static int LocaleReport()
{
    string product = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Riot Games", "Metadata", "league_of_legends.live",
        "league_of_legends.live.product_settings.yaml");

    Console.WriteLine($"product_settings.yaml   {(File.Exists(product) ? "있음" : "없음")}");
    Console.WriteLine($"  {product}");

    if (File.Exists(product))
    {
        string text = File.ReadAllText(product);
        var install = Regex.Match(text, @"product_install_full_path\s*:\s*""?(?<v>[^""\r\n]+?)""?\s*$",
                                  RegexOptions.Multiline);
        var fallback = Regex.Match(text, @"default_locale\s*:\s*""?(?<v>[A-Za-z]{2}_[A-Za-z]{2})""?");
        Console.WriteLine($"  default_locale          {(fallback.Success ? fallback.Groups["v"].Value : "찾지 못함")}");
        Console.WriteLine($"  install path            {(install.Success ? install.Groups["v"].Value : "찾지 못함")}");

        if (install.Success)
        {
            string settings = Path.Combine(install.Groups["v"].Value.Trim(),
                                           "Config", "LeagueClientSettings.yaml");
            Console.WriteLine($"LeagueClientSettings    {(File.Exists(settings) ? "있음" : "없음")}");
            Console.WriteLine($"  {settings}");
            if (File.Exists(settings))
            {
                var chosen = Regex.Match(File.ReadAllText(settings),
                    @"^\s+locale\s*:\s*""?(?<v>[A-Za-z]{2}_[A-Za-z]{2})""?", RegexOptions.Multiline);
                Console.WriteLine($"  install.globals.locale  {(chosen.Success ? chosen.Groups["v"].Value : "찾지 못함")}");
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine($"ClientLocale.Detect()     {ClientLocale.Detect() ?? "null (디스크에서 못 읽음)"}");
    Console.WriteLine($"Settings.SystemLocale()   {Settings.SystemLocale()}   (Windows: {CultureInfo.CurrentUICulture.Name})");
    Console.WriteLine($"Settings.DefaultLocale()  {Settings.DefaultLocale()}   <-- 첫 실행에 쓰이는 값");
    return 0;
}
