using System.Text.Json;
using System.Text.Json.Serialization;

namespace AramOverlay.Core;

/// <summary>The three card slots, in the order every array in a recording uses.</summary>
public static class Slots
{
    public static readonly string[] All = { "L", "M", "R" };
}

/// <summary>
/// One frame of a recorded window: the picture's index and every number the
/// loop had when it looked at it.
///
/// Names are short because there is one of these per frame and a long window
/// runs past five hundred of them; the page reads them once and never shows a
/// key to the user.
/// </summary>
public sealed class InspectFrame
{
    [JsonPropertyName("i")] public int Index { get; set; }
    [JsonPropertyName("t")] public double T { get; set; }
    [JsonPropertyName("gate")] public double[] Gate { get; set; } = Array.Empty<double>();
    [JsonPropertyName("hide")] public double Hide { get; set; }
    [JsonPropertyName("up")] public bool CardsUp { get; set; }
    [JsonPropertyName("alive")] public bool Alive { get; set; }
    [JsonPropertyName("settled")] public bool Settled { get; set; }
    [JsonPropertyName("base")] public double Baseline { get; set; }
    [JsonPropertyName("spread")] public double Spread { get; set; }
    [JsonPropertyName("mean")] public double[] Mean { get; set; } = Array.Empty<double>();
    [JsonPropertyName("inner")] public double[] Inner { get; set; } = Array.Empty<double>();
    [JsonPropertyName("bright")] public double[] Bright { get; set; } = Array.Empty<double>();

    /// <summary>Tooltip title box the finder settled on, in 1920x1080 space, or null.</summary>
    [JsonPropertyName("tip")] public int[]? Tip { get; set; }
    [JsonPropertyName("tipflip")] public bool TipFlipped { get; set; }

    /// <summary>
    /// How old that box is on this frame. Never zero: the panel is found on the
    /// OCR grab, a different frame arriving about three times a second.
    /// </summary>
    [JsonPropertyName("tipboxage")] public double TipBoxAge { get; set; }

    /// <summary>What the newest tooltip read said, and how old it was on this frame.</summary>
    [JsonPropertyName("tipslot")] public string? TipSlot { get; set; }
    [JsonPropertyName("tipraw")] public string? TipRaw { get; set; }
    [JsonPropertyName("tipage")] public double TipAge { get; set; }
}

/// <summary>Something that happened at a moment rather than over one.</summary>
public sealed class InspectEvent
{
    [JsonPropertyName("t")] public double T { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

/// <summary>A whole recorded window, as the page receives it.</summary>
public sealed class InspectSession
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("level")] public int? Level { get; set; }
    [JsonPropertyName("started")] public string Started { get; set; } = "";
    [JsonPropertyName("frame_w")] public int FrameW { get; set; }
    [JsonPropertyName("frame_h")] public int FrameH { get; set; }
    [JsonPropertyName("base_w")] public int BaseW { get; set; } = Config.BaseW;
    [JsonPropertyName("base_h")] public int BaseH { get; set; } = Config.BaseH;
    [JsonPropertyName("slots")] public string[] SlotOrder { get; set; } = Slots.All;
    [JsonPropertyName("dropped")] public int Dropped { get; set; }
    [JsonPropertyName("frames")] public List<InspectFrame> Frames { get; set; } = new();
    [JsonPropertyName("events")] public List<InspectEvent> Events { get; set; } = new();

    /// <summary>The close-out lines, exactly as they were written to the log.</summary>
    [JsonPropertyName("verdict")] public List<string> Verdict { get; set; } = new();

    /// <summary>Every box the detector uses, so the page never hardcodes one.</summary>
    [JsonPropertyName("boxes")] public Dictionary<string, int[]> Boxes { get; set; } = new();

    /// <summary>Every cutoff a number on screen is judged against.</summary>
    [JsonPropertyName("limits")] public Dictionary<string, double> Limits { get; set; } = new();
}

/// <summary>
/// Records an augment window frame by frame and serves it back for inspection.
///
/// This exists because of what the debug dumps could not settle. They write two
/// pictures per pick -- the frame the titles were read from and the last frame
/// the window was alive in -- and neither is the frame a flare was decided on;
/// the second is routinely a picture of open map, because the hide button holds
/// the window "alive" for up to a second and a half after the cards have gone.
/// Three wrong picks on 2026-09-10 all turned on what was on screen at one
/// specific frame, and that frame was never kept.
///
/// So: every frame, with the measurements beside it, and a page that draws the
/// boxes back on. The pictures are the JPEGs OBS sent -- the same bytes the
/// gate scored -- so nothing is re-encoded and a window costs about what the
/// wire cost, 15 to 25 MB.
/// </summary>
public sealed class InspectorSink : ILoopObserver
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _lock = new();
    private List<InspectFrame> _rows = new();
    private List<byte[]> _shots = new();
    private List<InspectEvent> _events = new();
    private int? _level;
    private int _dropped;
    private DateTime _startedAt;

    /// <summary>The inspector keeps pictures; that is the whole point of it.</summary>
    public bool WantsFrames => true;

    public void BeginWindow(int? level)
    {
        lock (_lock)
        {
            _rows = new List<InspectFrame>();
            _shots = new List<byte[]>();
            _events = new List<InspectEvent>();
            _level = level;
            _dropped = 0;
            _startedAt = DateTime.Now;
        }
    }

    public void Mark(double t, string kind, string text)
    {
        lock (_lock)
            _events.Add(new InspectEvent { T = Math.Round(t, 4), Kind = kind, Text = text });
    }

    public void Frame(Frame frame, TraceSample sample)
    {
        // Only frames that arrived as bytes are kept. A frame built in memory
        // would have to be encoded here, in the loop, to be written later.
        if (frame.Encoded is not { } bytes)
            return;

        var row = new InspectFrame
        {
            T = Math.Round(sample.T, 4),
            Gate = sample.Gate.Select(Round2).ToArray(),
            Hide = Round3(sample.Hide),
            CardsUp = sample.CardsUp,
            Alive = sample.Alive,
            Settled = sample.Settled,
            Baseline = Round2(sample.Baseline),
            Spread = Round2(sample.HoverSpread),
            Mean = Slots.All.Select(s => Round2(Stat(sample, s).Mean)).ToArray(),
            Inner = Slots.All.Select(s => Round2(Stat(sample, s).Inner)).ToArray(),
            Bright = Slots.All.Select(s => Round2(Stat(sample, s).Bright)).ToArray(),
            TipSlot = sample.TooltipSlot,
            TipRaw = sample.TooltipRaw.Length > 0 ? sample.TooltipRaw : null,
            TipAge = Round2(sample.TooltipAge),
        };
        if (sample.TipPanel is { } panel)
        {
            row.Tip = new[] { panel.Title.X0, panel.Title.Y0, panel.Title.X1, panel.Title.Y1 };
            row.TipFlipped = panel.Flipped;
            row.TipBoxAge = Round2(sample.TipPanelAge);
        }

        lock (_lock)
        {
            if (_rows.Count >= Config.InspectorMaxFrames)
            {
                _dropped++;
                return;
            }
            row.Index = _rows.Count;
            _rows.Add(row);
            _shots.Add(bytes);
            _lastSize = (frame.Width, frame.Height);
        }
    }

    private (int W, int H) _lastSize;

    private static Detect.CardStat Stat(TraceSample sample, string slot) =>
        sample.Stats.TryGetValue(slot, out var stat) ? stat : default;

    private static double Round2(double v) => double.IsFinite(v) ? Math.Round(v, 2) : 0;
    private static double Round3(double v) => double.IsFinite(v) ? Math.Round(v, 3) : 0;

    public async Task FinishAsync(IReadOnlyList<string> verdict)
    {
        List<InspectFrame> rows;
        List<byte[]> shots;
        List<InspectEvent> events;
        int? level;
        int dropped;
        DateTime startedAt;
        (int W, int H) size;
        lock (_lock)
        {
            rows = _rows;
            shots = _shots;
            events = _events;
            level = _level;
            dropped = _dropped;
            startedAt = _startedAt;
            size = _lastSize;
            _rows = new List<InspectFrame>();
            _shots = new List<byte[]>();
            _events = new List<InspectEvent>();
        }
        if (rows.Count == 0)
            return;

        var session = new InspectSession
        {
            Id = $"{startedAt:yyyyMMdd-HHmmss}" + (level is { } lv ? $"_lv{lv}" : ""),
            Level = level,
            Started = startedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            FrameW = size.W,
            FrameH = size.H,
            Dropped = dropped,
            Frames = rows,
            Events = events,
            Verdict = verdict.ToList(),
            Boxes = BoxTable(),
            Limits = LimitTable(),
        };

        try
        {
            string dir = Path.Combine(Config.Inspect, session.Id);
            string frames = Path.Combine(dir, "frames");
            Directory.CreateDirectory(frames);
            for (int i = 0; i < shots.Count; i++)
                await File.WriteAllBytesAsync(Path.Combine(frames, $"{i:D5}.jpg"), shots[i]);
            await File.WriteAllBytesAsync(Path.Combine(dir, "session.json"),
                JsonSerializer.SerializeToUtf8Bytes(session, Json));
            Prune();
            Log.Write(Strings.Get("Loop.InspectSaved", session.Id, rows.Count));
        }
        catch (Exception exc)
        {
            Log.Write(Strings.Get("Loop.InspectFailed", exc.Message));
        }
    }

    public void EndWindow()
    {
        lock (_lock)
        {
            _rows = new List<InspectFrame>();
            _shots = new List<byte[]>();
            _events = new List<InspectEvent>();
        }
    }

    /// <summary>Every box the detector reads from, in 1920x1080 space.</summary>
    private static Dictionary<string, int[]> BoxTable()
    {
        var table = new Dictionary<string, int[]>();
        void Put(string name, Box b) => table[name] = new[] { b.X0, b.Y0, b.X1, b.Y1 };

        foreach (var slot in Slots.All)
        {
            Put("card_" + slot, Config.Cards[slot]);
            Put("inner_" + slot, Config.CardInteriors[slot]);
            Put("border_" + slot, Config.CardBorders[slot]);
            Put("title_" + slot, Config.CardTitles[slot]);
        }
        for (int i = 0; i < Config.RerollBoxes.Length; i++)
        {
            var (x, y) = Config.RerollBoxes[i];
            Put("reroll_" + Slots.All[Math.Min(i, Slots.All.Length - 1)],
                new Box(x, y, x + Config.RerollSize.W, y + Config.RerollSize.H));
        }
        Put("hide", Config.HideBox);
        Put("tooltip_fixed", Config.HoverTooltip);
        return table;
    }

    /// <summary>Every cutoff, so the page can draw the line a number is judged against.</summary>
    private static Dictionary<string, double> LimitTable() => new()
    {
        ["gate_open"] = Config.GateOpen,
        ["gate_stay"] = Config.GateStay,
        ["gate_stay_two"] = Config.GateStayTwo,
        ["close_misses"] = Config.CloseMisses,
        ["hide_present"] = Config.HidePresent,
        ["hover_spread"] = Config.HoverSpread,
        ["hover_trust_s"] = Config.HoverTrustS,
        ["tooltip_probe_s"] = Config.TooltipProbeS,
        ["flare_inner_ratio"] = Config.FlareInnerRatio,
        ["flare_rise"] = Config.FlareRise,
        ["flare_window_s"] = Config.FlareWindowS,
        ["flare_window_after_s"] = Config.FlareWindowAfterS,
        ["flare_wide_window_s"] = Config.FlareWideWindowS,
        ["flare_baseline_from_s"] = Config.FlareBaselineFromS,
        ["flare_baseline_to_s"] = Config.FlareBaselineToS,
        ["entry_anim_s"] = Config.EntryAnimS,
        ["select_flare"] = Config.SelectFlare,
    };

    /// <summary>
    /// Drop the oldest recordings past the keep count. A window is 15-25 MB and
    /// this runs unattended for as long as the setting is on, so something has
    /// to be counting.
    /// </summary>
    private static void Prune()
    {
        try
        {
            var dirs = new DirectoryInfo(Config.Inspect).GetDirectories()
                .OrderByDescending(d => d.Name)
                .Skip(Math.Max(1, Config.InspectorKeep))
                .ToArray();
            foreach (var dir in dirs)
                dir.Delete(true);
        }
        catch
        {
            // A recording that will not delete is not worth failing the save for.
        }
    }

    // --- reading, for the server ------------------------------------------

    /// <summary>Recorded windows, newest first: id, level and how many frames.</summary>
    public static byte[] ListJson()
    {
        var list = new List<object>();
        try
        {
            foreach (var dir in new DirectoryInfo(Config.Inspect).GetDirectories()
                         .OrderByDescending(d => d.Name))
            {
                var file = new FileInfo(Path.Combine(dir.FullName, "session.json"));
                if (!file.Exists)
                    continue;
                int frames = 0;
                try
                {
                    frames = new DirectoryInfo(Path.Combine(dir.FullName, "frames"))
                        .GetFiles("*.jpg").Length;
                }
                catch { /* a half-written recording still lists */ }
                list.Add(new
                {
                    id = dir.Name,
                    frames,
                    bytes = file.Length,
                    at = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                });
            }
        }
        catch
        {
            // Nothing recorded yet is an empty list, not an error.
        }
        return JsonSerializer.SerializeToUtf8Bytes(new { sessions = list }, Json);
    }

    /// <summary>One recording's numbers, or null when the id names nothing.</summary>
    public static byte[]? SessionJson(string id)
    {
        string path = Path.Combine(Config.Inspect, Safe(id), "session.json");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>One recorded frame, or null.</summary>
    public static byte[]? FrameJpeg(string id, string name)
    {
        string path = Path.Combine(Config.Inspect, Safe(id), "frames", Safe(name));
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// One path segment with no way out of it. The server is on the loopback
    /// interface and the only client is a browser on this machine, but a URL is
    /// still a string a user can type.
    /// </summary>
    private static string Safe(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Replace("..", "_");
    }
}
