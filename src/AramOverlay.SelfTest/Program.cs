using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AramOverlay.Core;
using AramOverlay.SelfTest;

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

// Pull the capture source from a running OBS at its own size and at a list of
// asked-for sizes, keep the files, and score each one. How OBS scales a frame
// to a size that is not the source's shape is a fact to look at rather than
// assume, and it decides what a non-16:9 screen would hand detection.
//
//     dotnet run --project src/AramOverlay.SelfTest -- --grab <folder> [2560x1440 ...]
if (args.Contains("--grab"))
    return await GrabSizes(args.FirstOrDefault(a => !a.StartsWith("--")) ?? ".", args);

// Any one request against a running OBS, its answer printed as JSON -- for
// looking at what OBS sees (which windows a capture could pick, whether a
// source is active) without adding a command per question. Single quotes in
// the JSON are accepted, because double ones do not survive a shell.
//
//     dotnet run --project src/AramOverlay.SelfTest -- --obs-req GetSourceActive "{'sourceName':'League of Legends'}"
if (args.Contains("--obs-req"))
    return await ObsRequest(args);

// A folder of game frames at their own sizes -- fullscreen captures at every
// resolution the client offers -- run the way the loop would run them: shrunk
// to the standard height keeping their shape, mapped by height and centre.
// Beside each, what the old stretched-to-16:9 frame would have scored.
//
//     dotnet run --project src/AramOverlay.SelfTest -- --aspect <folder>
if (args.Contains("--aspect"))
    return await AspectReport(args.FirstOrDefault(a => !a.StartsWith("--")) ?? ".");

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
    // The only way to trace a live game now. It used to be trace_mode in
    // config.json, which meant a user could switch on 25 MB a window from a
    // settings file and the code to do it shipped in their exe; it is a flag on
    // the developer tool instead.
    //
    //     dotnet run --project src/AramOverlay.SelfTest -- --loop --trace
    if (args.Contains("--trace"))
    {
        Observe.Sink = new TraceSink();
        Config.DebugMode = true;
        Console.WriteLine("트레이스 켜짐 -> " + Path.Combine(root, "state", "trace"));
    }
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
failures += await ScaleInvariance(root);
failures += await AspectParity(root);
failures += FlareTiming();

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
    // A replay is always traced -- leaving a trace beside the frames is the
    // whole reason to replay them.
    Observe.Sink = new TraceSink();
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

    var (flare, flareRejected) = OverlayLoop.SelectionFlare(history, closedAt);
    var (glowSlot, _, glowVia) = OverlayLoop.HoverGlowSlot(history, closedAt);

    var why = new List<string>
    {
        $"frames      {files.Length} at {fps} fps",
        $"close taken at frame {(int)Math.Round((closedAt - start).TotalSeconds * fps)}",
        flare.Found ? "flare       " + flare.Describe(closedAt) : "flare       none",
        flareRejected.Found
            ? "flare xx    " + flareRejected.Describe(closedAt) + "  -> not a selection"
            : "flare xx    nothing thrown out",
        glowSlot is null ? "hover glow  none" : $"hover glow  {glowSlot}  {glowVia}",
        "tooltip     not available in a replay (no OCR probe ran)",
        flare.Found ? $"would take  {flare.Slot} on the flare"
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

// The same frame at five sizes has to produce the same verdicts. Every box is
// written in 1920x1080 pixels and mapped onto the frame it is handed, so a 1440p
// or a 720p capture is the same picture to the code -- that is the claim the
// resolution setting rests on, and it broke once without anything here to say
// so: the setting used to move BaseW/BaseH while the tooltip finder kept its own
// constants, and no fixture had ever been run at another size.
//
// Scores are allowed to drift with resampling; verdicts, slots, rarities and the
// tooltip's title box (in 1080p space, within a few pixels) are not. OCR is
// checked the way the loop uses it -- escalating scale until confident -- on the
// upscaled frames, since a smaller frame than 1080p is never read.
static async Task<int> ScaleInvariance(string root)
{
    TemplateGate gate;
    HideButton hide;
    try
    {
        gate = await Assets.GateAsync();
        hide = await Assets.HideButtonAsync();
    }
    catch (Exception exc)
    {
        Console.WriteLine($"SKIP  배율 불변 — 이 환경에서 이미지 디코딩 불가 ({exc.GetType().Name})");
        return 0;
    }

    var sizes = new (int W, int H)[] { (1280, 720), (1600, 900), (2560, 1440), (3840, 2160) };
    string[] slots = { "L", "M", "R" };

    var ocr = TooltipOcr.TryCreate();
    AugmentDb? db = null;
    if (ocr is not null)
    {
        Config.Root = root;
        try { db = await AugmentDb.LoadAsync(); } catch { db = null; }
    }

    var files = Directory.EnumerateFiles(Path.Combine(root, "tests", "fixtures"))
        .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToArray();

    int checked_ = 0, bad = 0, ocrChecked = 0;
    double worstGate = 0, worstMean = 0;
    int worstTitle = 0;
    var problems = new List<string>();
    var clock = System.Diagnostics.Stopwatch.StartNew();

    foreach (string file in files)
    {
        string name = Path.GetFileName(file);
        var full = await Imaging.DecodeAsync(await File.ReadAllBytesAsync(file));
        var refGray = Cv.ToGray(full);
        var refGate = gate.Scores(refGray);
        bool refOpen = refGate.All(s => s >= Config.GateOpen);
        bool refHide = hide.Score(refGray) >= Config.HidePresent;
        var refMeans = Detect.CardMeans(refGray);
        var (refHover, _) = Detect.HoveredCard(refMeans);
        var refRarity = slots.ToDictionary(s => s, s => Detect.RarityOf(full, s));
        var refTip = Detect.FindTooltip(refGray);
        var (refFlare, _, _) = Detect.Flare(Detect.CardStats(refGray));

        // What the loop would read off the 1080p frame, escalating as it does.
        var refCards = new Dictionary<string, string?>();
        string? refTipName = null;
        if (ocr is not null && db is not null)
        {
            foreach (string slot in slots)
                refCards[slot] = (await ReadEscalating(ocr, db, full, Config.CardTitles[slot])).Name;
            if (refTip is { } rt)
                refTipName = (await ReadEscalating(ocr, db, full, rt.Title)).Name;
        }

        foreach (var (w, h) in sizes)
        {
            var frame = w < full.Width ? Imaging.ResizeArea(full, w, h) : Imaging.Resize(full, w, h);
            var gray = Cv.ToGray(frame);
            string where = $"{name} @{w}x{h}";
            checked_++;

            var g = gate.Scores(gray);
            for (int i = 0; i < 3; i++)
                worstGate = Math.Max(worstGate, Math.Abs(g[i] - refGate[i]));
            if (g.All(s => s >= Config.GateOpen) != refOpen)
            {
                problems.Add($"      {where} 게이트 판정 {string.Join("/", g.Select(s => s.ToString("F2")))} " +
                             $"vs 1080p {string.Join("/", refGate.Select(s => s.ToString("F2")))}");
                bad++;
            }
            if (hide.Score(gray) >= Config.HidePresent != refHide)
            {
                problems.Add($"      {where} 숨김버튼 판정 {hide.Score(gray):F2}");
                bad++;
            }

            var means = Detect.CardMeans(gray);
            foreach (string slot in slots)
            {
                double drift = Math.Abs(means[slot] - refMeans[slot]);
                worstMean = Math.Max(worstMean, drift);
                if (drift > 1.5)
                {
                    problems.Add($"      {where} 카드평균[{slot}] {means[slot]:F2} vs {refMeans[slot]:F2}");
                    bad++;
                }
            }
            if (Detect.HoveredCard(means).Slot != refHover)
            {
                problems.Add($"      {where} 호버 {Detect.HoveredCard(means).Slot} vs {refHover}");
                bad++;
            }
            foreach (string slot in slots)
            {
                string got = Detect.RarityOf(frame, slot);
                if (got != refRarity[slot])
                {
                    problems.Add($"      {where} 등급[{slot}] {got} vs {refRarity[slot]}");
                    bad++;
                }
            }
            if (Detect.Flare(Detect.CardStats(gray)).Slot != refFlare)
            {
                problems.Add($"      {where} 플레어 슬롯 vs {refFlare}");
                bad++;
            }

            var tip = Detect.FindTooltip(gray);
            if (tip is null != refTip is null)
            {
                problems.Add($"      {where} 툴팁 {(tip is null ? "없음" : "있음")} vs 1080p {(refTip is null ? "없음" : "있음")}");
                bad++;
            }
            else if (tip is { } t && refTip is { } r)
            {
                // Title boxes come back in 1080p space whatever the frame, so
                // they can be compared directly; a few pixels of resampling
                // slop is expected, a different panel is not.
                int off = Math.Max(Math.Max(Math.Abs(t.Title.X0 - r.Title.X0), Math.Abs(t.Title.Y0 - r.Title.Y0)),
                                   Math.Max(Math.Abs(t.Title.X1 - r.Title.X1), Math.Abs(t.Title.Y1 - r.Title.Y1)));
                worstTitle = Math.Max(worstTitle, off);
                if (t.Flipped != r.Flipped || off > 6)
                {
                    problems.Add($"      {where} 툴팁 제목 ({t.Title.X0},{t.Title.Y0})~({t.Title.X1},{t.Title.Y1})" +
                                 $"{(t.Flipped ? " 뒤집힘" : "")} vs ({r.Title.X0},{r.Title.Y0})~({r.Title.X1},{r.Title.Y1})" +
                                 $"{(r.Flipped ? " 뒤집힘" : "")}");
                    bad++;
                }
            }

            if (ocr is null || db is null || w <= full.Width)
                continue;
            foreach (string slot in slots)
            {
                if (refCards[slot] is not string want)
                    continue;             // unreadable at 1080p: nothing to hold the bigger frame to
                ocrChecked++;
                var got = await ReadEscalating(ocr, db, frame, Config.CardTitles[slot]);
                if (got.Name != want)
                {
                    problems.Add($"      {where} OCR 카드[{slot}] '{got.Raw}' -> {got.Name} vs {want}");
                    bad++;
                }
            }
            if (refTipName is not null && tip is { } t2)
            {
                ocrChecked++;
                var got = await ReadEscalating(ocr, db, frame, t2.Title);
                if (got.Name != refTipName)
                {
                    problems.Add($"      {where} OCR 툴팁 '{got.Raw}' -> {got.Name} vs {refTipName}");
                    bad++;
                }
            }
        }
    }

    string ocrNote = ocr is null || db is null
        ? "OCR 생략" : $"OCR {ocrChecked}건 포함";
    if (bad == 0)
    {
        Console.WriteLine($"PASS  배율 불변 {checked_}프레임 ({sizes.Length}가지 크기, {ocrNote}, " +
                          $"게이트 최대 오차 {worstGate:F3}, 밝기 {worstMean:F2}, 툴팁 제목 {worstTitle}px, " +
                          $"{clock.Elapsed.TotalSeconds:F0}s)");
        return 0;
    }
    Console.WriteLine($"FAIL  배율 불변 {bad}건 / {checked_}프레임 ({ocrNote})");
    problems.Take(14).ToList().ForEach(Console.WriteLine);
    if (problems.Count > 14)
        Console.WriteLine($"      ... 외 {problems.Count - 14}건");
    return 1;
}

// The loop's own reading rule: each scale in turn, stop at the first confident
// match, keep the best otherwise.
static async Task<(string? Name, double Score, string Raw)> ReadEscalating(
    TooltipOcr ocr, AugmentDb db, Frame frame, Box box)
{
    (string? Name, double Score, string Raw) best = (null, 0, "");
    foreach (int scale in Config.CardScales)
    {
        string raw = await ocr.ReadBoxAsync(frame, box, scale);
        if (raw.Length == 0)
            continue;
        var (aug, score) = db.Match(raw);
        if (score > best.Score)
            best = (aug?.Name, score, raw);
        if (best.Score >= Config.OcrMinScore)
            break;
    }
    return best.Score >= Config.OcrMinScore ? best : (null, best.Score, best.Raw);
}

// Frames from a live OBS at several sizes, saved beside their scores. The
// native grab says what the game is rendering at; the others say how OBS
// scales -- stretched or letterboxed -- when the asked-for shape differs.
static async Task<int> GrabSizes(string outDir, string[] argv)
{
    Config.Root = FindRepoRoot();
    var sizes = argv.Where(a => Regex.IsMatch(a, @"^\d+x\d+$"))
        .Select(a => a.Split('x')).Select(p => (W: int.Parse(p[0]), H: int.Parse(p[1]))).ToArray();
    if (sizes.Length == 0)
        sizes = new[] { (960, 540), (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160),
                        (2560, 1600), (3440, 1440) };
    Directory.CreateDirectory(outDir);

    var cfg = ObsCapture.ReadWebsocketConfig();
    string password = cfg?["server_password"]?.GetValue<string>() ?? "";
    int port = cfg?["server_port"]?.GetValue<int>() ?? Config.ObsPort;
    await using var obs = await ObsCapture.ConnectAsync(port: port, password: password);
    Console.WriteLine($"연결: {await obs.VersionAsync()}, 소스 '{obs.Source}' 존재: {await obs.HasSourceAsync()}");
    var gate = await Assets.GateAsync();

    async Task Report(string label, Frame? frame, long ms)
    {
        if (frame is null)
        {
            Console.WriteLine($"{label,-14} 비어 있음 ({ms} ms)");
            return;
        }
        var gray = Cv.ToGray(frame);
        var scores = gate.Scores(gray);
        var tip = Detect.FindTooltip(gray);
        string path = Path.Combine(outDir, $"grab_{label}.png");
        await Imaging.SavePngAsync(frame, path);
        Console.WriteLine($"{label,-14} {frame.Width}x{frame.Height} {ms,5} ms  " +
                          $"게이트 {string.Join("/", scores.Select(s => s.ToString("F2")))}  " +
                          $"툴팁 {(tip is { } t ? $"{(t.Flipped ? "뒤집힘" : "아래")} x={t.X0}~{t.X1}" : "없음")}  -> {Path.GetFileName(path)}");
    }

    var clock = System.Diagnostics.Stopwatch.StartNew();
    var native = await obs.GrabNativeAsync();
    await Report("native", native, clock.ElapsedMilliseconds);
    foreach (var (w, h) in sizes)
    {
        clock.Restart();
        var frame = await obs.GrabAsync(w, h, Config.OcrQuality);
        await Report($"{w}x{h}", frame, clock.ElapsedMilliseconds);
    }
    return 0;
}

// One augment window captured at five fullscreen sizes of four shapes -- 16:9,
// 16:10, 5:4 and 4:3 -- with the same three prismatic cards on every one. The
// detection frame keeps the source's shape and the geometry maps by height and
// centre; here that has to open the gate, read all three rarities and all three
// titles at every size. The 1680x1050 capture has the tooltip open over the
// left and middle cards, which is what a real hover looks like: there the
// right card and the flipped tooltip are what must be found.
static async Task<int> AspectParity(string root)
{
    string dir = Path.Combine(root, "tests", "fixtures", "aspect");
    if (!Directory.Exists(dir))
    {
        Console.WriteLine($"FAIL  비율 픽스처가 없습니다: {dir}");
        return 1;
    }
    TemplateGate gate;
    try { gate = await Assets.GateAsync(); }
    catch (Exception exc)
    {
        Console.WriteLine($"SKIP  비율 대조 — 이 환경에서 이미지 디코딩 불가 ({exc.GetType().Name})");
        return 0;
    }
    var ocr = TooltipOcr.TryCreate();
    AugmentDb? db = null;
    if (ocr is not null)
    {
        Config.Root = root;
        try { db = await AugmentDb.LoadAsync(); } catch { db = null; }
    }
    string[] slots = { "L", "M", "R" };
    string[] want = { "과충전", "궁극기 봇", "지옥불 난사 발동" };

    int checked_ = 0, bad = 0, ocrChecked = 0;
    var problems = new List<string>();
    foreach (string file in Directory.EnumerateFiles(dir, "*.jpg").OrderBy(f => f, StringComparer.Ordinal))
    {
        string name = Path.GetFileName(file);
        bool tooltipOpen = name.Contains("1680x1050");
        var frame = await Imaging.DecodeAsync(await File.ReadAllBytesAsync(file));
        var det = Config.DetSizeFor(frame.Width, frame.Height);
        var gray = Cv.ToGray(Imaging.ResizeArea(frame, det.W, det.H));
        checked_++;

        var scores = gate.Scores(gray);
        for (int i = 0; i < 3; i++)
        {
            if (tooltipOpen && i < 2)
                continue;                      // under the tooltip panel
            if (scores[i] < Config.GateOpen)
            {
                problems.Add($"      {name} 게이트[{slots[i]}] {scores[i]:F2} < {Config.GateOpen}");
                bad++;
            }
        }
        foreach (string slot in slots)
        {
            string got = Detect.RarityOf(frame, slot);
            if (got != "prismatic")
            {
                problems.Add($"      {name} 등급[{slot}] {got}");
                bad++;
            }
        }
        var tip = Detect.FindTooltip(gray);
        if (tooltipOpen && tip is not { Flipped: true })
        {
            problems.Add($"      {name} 툴팁 {(tip is null ? "없음" : "아래로 판정")}, 뒤집힘이어야 함");
            bad++;
        }
        if (!tooltipOpen && tip is not null)
        {
            problems.Add($"      {name} 툴팁이 없는데 찾았다고 함");
            bad++;
        }
        if (ocr is null || db is null)
            continue;
        for (int i = 0; i < 3; i++)
        {
            if (tooltipOpen && i < 2)
                continue;
            ocrChecked++;
            var got = await ReadEscalating(ocr, db, frame, Config.CardTitles[slots[i]]);
            if (got.Name != want[i])
            {
                problems.Add($"      {name} OCR[{slots[i]}] '{got.Raw}' -> {got.Name} vs {want[i]}");
                bad++;
            }
        }
    }

    string ocrNote = ocr is null || db is null ? "OCR 생략" : $"OCR {ocrChecked}건 포함";
    if (bad == 0)
    {
        Console.WriteLine($"PASS  비율 대조 {checked_}프레임 (16:9·16:10·5:4·4:3, {ocrNote})");
        return 0;
    }
    Console.WriteLine($"FAIL  비율 대조 {bad}건 / {checked_}프레임 ({ocrNote})");
    problems.ForEach(Console.WriteLine);
    return 1;
}

static async Task<int> AspectReport(string folder)
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
    var gate = await Assets.GateAsync();
    var hide = await Assets.HideButtonAsync();
    var ocr = TooltipOcr.TryCreate();
    AugmentDb? db = null;
    if (ocr is not null)
    {
        Config.Root = FindRepoRoot();
        try { db = await AugmentDb.LoadAsync(); } catch { db = null; }
    }
    string[] slots = { "L", "M", "R" };
    string outDir = Path.Combine(folder, "marked");
    Directory.CreateDirectory(outDir);

    Console.WriteLine($"{"파일",-22} {"크기",-10} {"비율",-6} {"감지프레임",-9}  {"게이트(모양 유지)",-18} {"게이트(늘림)",-18} {"숨김",4}  {"등급 L/M/R",-22} {"툴팁",-8} OCR L / M / R");
    foreach (string file in files)
    {
        var frame = await Imaging.DecodeAsync(await File.ReadAllBytesAsync(file));
        var det = Config.DetSizeFor(frame.Width, frame.Height);
        var kept = Imaging.ResizeArea(frame, det.W, det.H);
        var stretched = Imaging.ResizeArea(frame, Config.DetW, Config.DetH);
        var gray = Cv.ToGray(kept);
        var g1 = gate.Scores(gray);
        var g2 = gate.Scores(Cv.ToGray(stretched));
        double hd = hide.Score(gray);
        var rarity = string.Join("/", slots.Select(s => Detect.RarityOf(frame, s) switch
        {
            "prismatic" => "프리즘", "gold" => "골드", "silver" => "실버", _ => "?",
        }));
        var tip = Detect.FindTooltip(gray);
        string ocrText = "";
        if (ocr is not null && db is not null)
        {
            var names = new List<string>();
            foreach (string slot in slots)
                names.Add((await ReadEscalating(ocr, db, frame, Config.CardTitles[slot])).Name ?? "-");
            ocrText = string.Join(" / ", names);
        }
        double ratio = (double)frame.Width / frame.Height;
        string shape = Math.Abs(ratio - 16.0 / 9) < 0.01 ? "16:9" : Math.Abs(ratio - 1.6) < 0.01 ? "16:10"
            : Math.Abs(ratio - 1.25) < 0.01 ? "5:4" : Math.Abs(ratio - 4.0 / 3) < 0.01 ? "4:3" : ratio.ToString("F3");
        Console.WriteLine($"{Path.GetFileName(file),-22} {frame.Width + "x" + frame.Height,-10} {shape,-6} {det.W + "x" + det.H,-9}  " +
                          $"{string.Join("/", g1.Select(s => s.ToString("F2"))),-18} {string.Join("/", g2.Select(s => s.ToString("F2"))),-18} {hd,4:F2}  " +
                          $"{rarity,-22} {(tip is { } t ? (t.Flipped ? "뒤집힘" : "아래") : "없음"),-8} {ocrText}");

        // The boxes the loop would read, drawn on the native frame.
        foreach (var (slot, box) in Config.Cards)
        {
            frame.DrawBox(box, 90, 200, 90, 2);
            frame.DrawBox(Config.CardTitles[slot], 60, 240, 255, 2);
            frame.DrawBox(Config.CardBorders[slot], 200, 160, 60, 1);
        }
        frame.DrawBox(Config.HideBox, 200, 80, 200, 2);
        var geo = Detect.Geometry.Of(frame.Width, frame.Height);
        foreach (var (bx, by) in Config.RerollBoxes)
            frame.DrawRaw(geo.X(bx), geo.Y(by), geo.X(bx + Config.RerollSize.W), geo.Y(by + Config.RerollSize.H), 220, 220, 80, 2);
        Detect.Mark(frame, tip);
        await Imaging.SavePngAsync(frame, Path.Combine(outDir, "mark_" + Path.GetFileNameWithoutExtension(file) + ".png"));
    }
    Console.WriteLine($"\n표시한 화면: {outDir}");
    return 0;
}

static async Task<int> ObsRequest(string[] argv)
{
    int at = Array.IndexOf(argv, "--obs-req");
    if (at + 1 >= argv.Length)
    {
        Console.WriteLine("사용법: --obs-req <요청 이름> [JSON]");
        return 1;
    }
    string type = argv[at + 1];
    JsonObject? data = null;
    if (at + 2 < argv.Length && argv[at + 2].TrimStart().StartsWith('{'))
        data = JsonNode.Parse(argv[at + 2].Replace('\'', '"')) as JsonObject;

    var cfg = ObsCapture.ReadWebsocketConfig();
    string password = cfg?["server_password"]?.GetValue<string>() ?? "";
    int port = cfg?["server_port"]?.GetValue<int>() ?? Config.ObsPort;
    await using var client = new ObsClient();
    await client.ConnectAsync(Config.ObsHost, port, password);
    try
    {
        var answer = await client.RequestAsync(type, data);
        Console.WriteLine(answer?.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) ?? "(응답 데이터 없음)");
        return 0;
    }
    catch (Exception exc)
    {
        Console.WriteLine($"실패: {exc.Message}");
        return 1;
    }
}

// Taking a card wipes the screen, so nothing that leaves the cards standing can
// be the selection. Three windows on 2026-09-10 published the wrong augment off
// a reroll's flash -- see Config.FlareWindowS -- and the shape of that mistake
// is what this pins: a card lighting up while the cards go on standing.
//
// Written as histories rather than frames because the fault is in the timing,
// not in any pixel: a reroll's flash and a selection's are the same brightness
// on one frame, and what separates them is what the gate says afterwards.
static int FlareTiming()
{
    var start = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    const double Fps = 40.0;              // what the loop actually runs at here
    const int Frames = 120;               // three seconds
    const int LastCardsUp = 100;          // the anchor

    static Beat Frame(DateTime at, double l, double m, double r, bool cardsUp) => new(
        at,
        new Dictionary<string, Detect.CardStat>
        {
            ["L"] = new(l, l, 0), ["M"] = new(m, m, 0), ["R"] = new(r, r, 0),
        },
        true, cardsUp);

    // Cards sitting at 20, one of them blazing at 100 over the given frames.
    List<Beat> Window(int flashFrom, int flashTo)
    {
        var beats = new List<Beat>();
        for (int i = 0; i < Frames; i++)
        {
            bool lit = i >= flashFrom && i <= flashTo;
            beats.Add(Frame(start.AddSeconds(i / Fps), 20, 20, lit ? 100 : 20,
                            i <= LastCardsUp));
        }
        return beats;
    }
    DateTime CloseOf(List<Beat> h) => h[^1].At;

    int bad = 0;
    void Check(string what, bool ok, string got)
    {
        if (ok)
            return;
        Console.WriteLine($"      {what}: {got}");
        bad++;
    }

    // A reroll: R flashes 0.3 s before the cards go, and the screen carries on.
    var reroll = Window(86, 89);
    var (rerollTaken, rerollThrown) = OverlayLoop.SelectionFlare(reroll, CloseOf(reroll));
    Check("리롤 섬광을 선택으로 읽음", !rerollTaken.Found, $"{rerollTaken.Slot} 로 판정");
    Check("버려진 후보를 기록하지 않음", rerollThrown.Slot == "R", $"{rerollThrown.Slot}");
    Check("버려진 후보의 카드 유지 시간이 0", rerollThrown.CardsUpAfterS > 0.2,
          $"{rerollThrown.CardsUpAfterS:F2}s");

    // A selection: R flashes across the last frame the gate saw cards.
    var pick = Window(98, 101);
    var (pickTaken, pickThrown) = OverlayLoop.SelectionFlare(pick, CloseOf(pick));
    Check("실제 선택 섬광을 놓침", pickTaken.Slot == "R", $"{pickTaken.Slot ?? "없음"}");
    Check("실제 선택인데 카드가 남아 있다고 함", pickTaken.CardsUpAfterS < 0.05,
          $"{pickTaken.CardsUpAfterS:F2}s");
    Check("실제 선택인데 후보를 버림", !pickThrown.Found, $"{pickThrown.Slot}");
    Check("실제 선택의 상승비가 낮음", pickTaken.Rise >= Config.FlareRise,
          $"{pickTaken.Rise:F2}x");

    // Nothing lights up at all.
    var quiet = Window(-1, -1);
    var (quietTaken, quietThrown) = OverlayLoop.SelectionFlare(quiet, CloseOf(quiet));
    Check("조용한 창에서 섬광을 만들어냄", !quietTaken.Found && !quietThrown.Found,
          $"{quietTaken.Slot}/{quietThrown.Slot}");

    Console.WriteLine(bad == 0
        ? "PASS  선택 섬광 타이밍 (리롤 섬광 기각, 실제 선택 통과)"
        : $"FAIL  선택 섬광 타이밍 {bad}건");
    return bad == 0 ? 0 : 1;
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
