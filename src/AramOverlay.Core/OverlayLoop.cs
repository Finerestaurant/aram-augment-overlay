namespace AramOverlay.Core;

/// <summary>One card's best reading, and what the matcher made of it.</summary>
public sealed class CardReading
{
    public string? Name;
    public double Score;
    public string Raw = "";
    public double ItemScore;
    public int Scale;
    public Augment? Aug;
}

/// <summary>
/// The tooltip frame carried between the probe task and the loop.
///
/// Built as a type rather than a bag of locals so resetting it on a new window
/// cannot quietly drop a field the loop then reads.
/// </summary>
public sealed class Kept
{
    public Dictionary<string, CardReading> Cards = new();
    public List<(string Slot, string? Was, string? Now)> Rerolls = new();
    public Frame? Frame;
    public Frame? Last;
    public DateTime At;
    public DateTime FullAt;
    public string? Hover;
    public string HoverRaw = "";
    // When the frame that tooltip came off was grabbed. Hover is never cleared
    // -- a cursor leaving the cards just stops updating it -- so without a time
    // beside it a hover from the start of the window looks exactly like one
    // from the moment of the click.
    public DateTime HoverAt;
    public List<(string Raw, string Name, double Score)> TipMisses = new();
    /// <summary>What the panel finder made of the newest scanned frame.</summary>
    public string TipPanel = "";

    // The augment the tooltip named, kept whether or not it matched a card
    // title. Hover above only records it when it agrees with one of the three
    // readings, which is the wrong test when the panel is sitting on top of the
    // very title it would have to agree with.
    public Augment? TipAug;
    public string TipRaw = "";
    public double TipScore;
    /// <summary>The last panel state a debug frame was written for, and how many.</summary>
    public string TipDumped = "";
    public int TipDumps;
}

/// <summary>
/// One detection frame, kept as numbers rather than pixels.
///
/// <paramref name="Alive"/> is the window-still-open test, which the hide
/// button alone can hold up. <paramref name="CardsUp"/> is the narrower
/// question of whether the reroll gate could still see cards, and it is the one
/// brightness may be read from: the hide button survives the cards by over a
/// second, and every wrong pick traced back to a brightness reading has been a
/// measurement of whatever the map put there afterwards.
/// </summary>
/// <remarks>
/// <paramref name="Hide"/> is the hide button's template score on this frame.
/// It is carried so the flare can be asked the one question that separates the
/// selection animation from three patches of map: was the augment screen even
/// there. Recorded and logged, not yet judged on.
/// </remarks>
public readonly record struct Beat(
    DateTime At, Dictionary<string, Detect.CardStat> Stats, bool Alive, bool CardsUp,
    double Hide = 0);

/// <summary>Per-window record of why selection did or did not trigger.</summary>
public sealed class Diagnostics
{
    public int Frames;
    public int Settled;
    public double[] GateLast = { 0, 0, 0 };
    public double GateMin = 1.0;
    public double HideLast;
    public double HideMax = -1.0;
    public double Peak;
    public (double, double) PeakLosers;
}

/// <summary>
/// Watches the augment window and publishes picks to the widget.
///
/// Loop shape follows what the calibration runs showed: the Live Client API
/// gives game mode and level cheaply, so the screen is only watched during a
/// Mayhem game; detection frames are small JPEGs and a full-resolution frame is
/// pulled only for OCR; and the window gate needs hysteresis or it reads as shut
/// about a second in and everything after that is missed.
/// </summary>
public sealed class OverlayLoop
{
    // A full-resolution screenshot costs ~250 ms and runs on its own request, so
    // this pacing bounds how hard OBS is hit, not the detection loop.
    private static readonly TimeSpan TooltipProbe = TimeSpan.FromSeconds(0.35);

    private readonly AugmentDb _db;
    private readonly ItemNames _items;
    private readonly TooltipOcr _ocr;
    private readonly TemplateGate _gate;
    private readonly HideButton _hide;
    private readonly ObsCapture _obs;
    private readonly ObsCapture _probeObs;
    private readonly WidgetServer _server;
    private readonly RunState _state;

    // What the game renders at, as OBS reports the source. Decides the shape
    // of the detection frame and the size of the OCR grab; re-read every few
    // seconds because it changes when the player changes resolution mid-game.
    private (int W, int H)? _sourceSize;
    private DateTime _sourceSizeAt = DateTime.MinValue;
    private const double SourceSizeEveryS = 5.0;

    public OverlayLoop(AugmentDb db, ItemNames items, TooltipOcr ocr, TemplateGate gate,
                       HideButton hide, ObsCapture obs, ObsCapture probeObs, WidgetServer server)
    {
        _db = db;
        _items = items;
        _ocr = ocr;
        _gate = gate;
        _hide = hide;
        _obs = obs;
        _probeObs = probeObs;
        _server = server;
        _state = server.State;
    }

    public async Task RunAsync(CancellationToken token)
    {
        var win = new AugmentWindowState();
        var kept = new Kept();
        var diag = new Diagnostics();
        var baselineSamples = new List<double>();
        var history = new List<Beat>();
        var picksLock = new object();
        int traceIndex = 0;

        int sizedSeq = 0;
        bool anvil = false;
        // The frame the window was last seen alive in. Detection frames are
        // small JPEGs that were being thrown away as soon as their brightness
        // was taken, which left the moment the pick actually happened with no
        // picture at all -- the only dump was of whenever the titles last read,
        // which on a wrong pick is not the frame anyone wants to look at.
        Frame? lastAlive = null;
        var recent = new List<(DateTime At, Frame Frame)>();
        int? seenRaw = null;
        double? currentGameId = null;
        DateTime lastProbe = DateTime.MinValue;
        Task? probe = null;
        int? level = null;

        Log.Write(Strings.Get("Loop.Waiting"));

        // Black frames in a row, and whether this game has had its one repair.
        int blank = 0;
        bool repairTried = false;

        while (!token.IsCancellationRequested)
        {
            if (_server.Size.Seq != sizedSeq && _server.Size.H > 0)
            {
                sizedSeq = _server.Size.Seq;
                var resized = await _obs.ResizeBrowserSourcesAsync(
                    _server.Url, _server.Size.W, _server.Size.H);
                if (resized.Count > 0)
                    Log.Write(Strings.Get("Loop.SourceResized",
                        _server.Size.W, _server.Size.H));
            }

            var game = await LiveGame.PollAsync();
            if (game is null)
            {
                if (_state.Connected)
                    Log.Write(Strings.Get("Loop.GameOver"));
                _state.Connected = false;
                _state.Level = null;
                _state.CaptureBlank = false;
                _state.SourceWidth = 0;
                _state.SourceHeight = 0;
                _sourceSize = null;
                _sourceSizeAt = DateTime.MinValue;
                blank = 0;
                repairTried = false;
                win = new AugmentWindowState();
                Observe.EndWindow();
                await Task.Delay(3000, token);
                continue;
            }

            string mode = LiveGame.ModeOf(game);
            level = LiveGame.LevelOf(game);
            _state.Level = level;
            double gameId = LiveGame.TimeOf(game);

            if (!_state.Connected)
            {
                _state.Connected = true;
                _state.GameMode = mode;
                Log.Write(Strings.Get("Loop.GameDetected", mode, level));
                if (mode != Config.MayhemGameMode)
                    Log.Write(Strings.Get("Loop.NotMayhem"));
            }

            // When this game began. gameTime counts up, so it cannot identify a
            // game across a restart; the moment it started can.
            double startedAt = gameId > 0
                ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - gameId
                : 0;

            // A fresh game rewinds gameTime; start a new list.
            if (currentGameId is not null && gameId + 5 < currentGameId)
            {
                Log.Write(Strings.Get("Loop.NewGame"));
                lock (picksLock) _state.Picks.Clear();
                _state.GameStartedAt = startedAt;
                _server.Save();
            }
            currentGameId = gameId;

            // Picks taken before a restart are on disk; put them back if this is
            // still the game they belong to. Tried once, and only into an empty
            // list -- see WidgetServer.RestoreIfSameGame.
            if (startedAt > 0 && _state.GameStartedAt <= 0)
            {
                _state.GameStartedAt = startedAt;
                if (_server.RestoreIfSameGame(startedAt))
                {
                    Log.Write(Strings.Get("Loop.PicksRestored", _state.Picks.Count));
                    _server.Save();
                }
            }

            if (mode != Config.MayhemGameMode)
            {
                await Task.Delay(3000, token);
                continue;
            }

            if ((DateTime.UtcNow - _sourceSizeAt).TotalSeconds > SourceSizeEveryS)
            {
                _sourceSizeAt = DateTime.UtcNow;
                var size = await _obs.SourceSizeAsync();
                if (size != _sourceSize)
                {
                    _sourceSize = size;
                    _state.SourceWidth = size?.W ?? 0;
                    _state.SourceHeight = size?.H ?? 0;
                    if (size is { } s)
                    {
                        var d = Config.DetSizeFor(s.W, s.H);
                        Log.Write(Strings.Get("Loop.SourceSize", s.W, s.H, d.W, d.H));
                    }
                }
            }
            // The frame keeps the source's shape; OBS would otherwise stretch
            // a 16:10 or 4:3 screen onto 16:9 and put every box off the cards.
            var detSize = _sourceSize is { } src ? Config.DetSizeFor(src.W, src.H) : (Config.DetW, Config.DetH);
            var frame = await _obs.GrabAsync(detSize.Item1, detSize.Item2);
            if (frame is null)
            {
                await Task.Delay(1000, token);
                continue;
            }

            var gray = Cv.ToGray(frame);

            // A hooked capture is never black; an unhooked one comes back black
            // at the asked-for size rather than as an error (OBS 32), which
            // looked exactly like watching a game that never opened a window.
            // A few in a row mean the source is not on the game: say so, and
            // once per game try pointing it at the window OBS can see.
            if (Detect.IsBlank(gray))
            {
                blank++;
                if (blank == Config.BlankFramesBeforeRepair)
                {
                    _state.CaptureBlank = true;
                    Log.Write(Strings.Get("Loop.CaptureBlank", Config.ObsSource));
                    if (!repairTried)
                    {
                        repairTried = true;
                        try
                        {
                            if (await _obs.RepairGameCaptureAsync() is { } window)
                                Log.Write(Strings.Get("Core.GameCaptureRepaired", Config.ObsSource, window));
                        }
                        catch
                        {
                            // Left to the status line, which now says the picture is missing.
                        }
                    }
                }
                await Task.Delay(1000, token);
                continue;
            }
            if (_state.CaptureBlank)
                Log.Write(Strings.Get("Loop.CaptureBack"));
            _state.CaptureBlank = false;
            blank = 0;

            var scores = _gate.Scores(gray);
            bool strong = scores.Min() >= Config.GateOpen;
            // The screen can be tucked away: the cards and reroll buttons go, the
            // hide button stays. Closing on the reroll buttons alone read that as
            // a finished pick and the screen coming back as a new one -- a single
            // augment logged three times, once per card the cursor crossed.
            double hideScore = _hide.Score(gray);
            bool weak = scores.Min() >= Config.GateStay
                        || scores.Count(s => s >= Config.GateStayTwo) >= 2
                        || hideScore >= Config.HidePresent;

            if (!win.Open)
            {
                win.OpenStreak = strong ? win.OpenStreak + 1 : 0;
                if (win.OpenStreak >= 2)
                {
                    win = new AugmentWindowState { Open = true, OpenedAt = Now() };
                    baselineSamples.Clear();
                    kept = new Kept();
                    diag = new Diagnostics();
                    history.Clear();
                    lastAlive = null;
                    recent.Clear();
                    anvil = false;
                    seenRaw = null;
                    traceIndex = 0;
                    Observe.BeginWindow(level);
                    Log.Write(Strings.Get("Loop.WindowOpen", level,
                        string.Join(", ", scores.Select(s => s.ToString("F2")))));
                }
            }
            else
            {
                win.MissStreak = weak ? 0 : win.MissStreak + 1;
                if (win.MissStreak >= Config.CloseMisses)
                {
                    Log.Write(Strings.Get("Loop.WindowClosed"));
                    Log.Write($"    {Summary(diag, win)}");
                    await OnWindowClosedAsync(win, kept, diag, anvil, level, history,
                                             picksLock, probe, lastAlive, recent);
                    win = new AugmentWindowState();
                    kept = new Kept();
                    await Task.Delay(300, token);
                    continue;
                }
            }

            if (!win.Open)
            {
                await Task.Delay(350, token);
                continue;
            }

            diag.GateLast = scores;
            diag.GateMin = Math.Min(diag.GateMin, scores.Min());
            diag.HideLast = hideScore;
            diag.HideMax = Math.Max(diag.HideMax, hideScore);
            diag.Frames++;

            var stats = Detect.CardStats(gray);
            var means = stats.ToDictionary(kv => kv.Key, kv => kv.Value.Mean);
            var frameAt = DateTime.UtcNow;
            // Cards up is the reroll gate on its own. The hide button is what
            // keeps `weak` true after the cards have gone, which is right for
            // deciding the window is over and wrong for deciding what a card
            // looked like.
            bool cardsUp = scores.Min() >= Config.GateStay ||
                           scores.Count(s => s >= Config.GateStayTwo) >= 2;
            history.Add(new Beat(frameAt, stats, weak, cardsUp, hideScore));
            if (weak)
                lastAlive = frame;

            // With the log on, keep a thin strip of recent frames so the one the
            // flare was decided from can be written out beside the verdict. The
            // close frame alone does not answer "what was on screen when that
            // card lit up", and that is the question three wrong picks on
            // 2026-09-10 all turned on. Roughly ten a second, which is enough to
            // land inside an 83 ms event, and about 25 MB held at any time.
            if (Config.DebugMode &&
                (recent.Count == 0 || (frameAt - recent[^1].At).TotalMilliseconds >= 100))
                recent.Add((frameAt, frame));
            while (recent.Count > 0 &&
                   (frameAt - recent[0].At).TotalSeconds > Config.FlareWideWindowS + 0.5)
                recent.RemoveAt(0);
            // Trimmed by age, not by count: the loop turns over in about 25 ms,
            // so the old cap of 45 entries held barely a second and the 2.5 s
            // lookback below could never actually reach that far back.
            double keepFor = Math.Max(Config.FlareLookbackS, Config.FlareWindowS) + 1.5;
            while (history.Count > 0 && (frameAt - history[0].At).TotalSeconds > keepFor)
                history.RemoveAt(0);

            bool settled = Detect.InteriorDarkness(gray) < 60 &&
                           Now() - win.OpenedAt > Config.EntryAnimS;
            if (settled)
            {
                diag.Settled++;
                baselineSamples.Add(Detect.Median(means.Values.ToArray()));
                if (baselineSamples.Count > 30)
                    baselineSamples.RemoveAt(0);
                win.Baseline = Detect.Median(baselineSamples.ToArray());

                var (hover, _) = Detect.HoveredCard(means);
                if (hover is not null)
                {
                    win.HoverHistory.Add(hover);
                    win.LastHover = hover;
                }
            }

            if (Observe.WantsFrames)
            {
                var (fslot, ftop, fratio) = Detect.Flare(stats);
                Observe.Frame(frame, new TraceSample
                {
                    Index = traceIndex++,
                    T = Now() - win.OpenedAt,
                    Gate = scores,
                    Hide = hideScore,
                    Alive = weak,
                    CardsUp = cardsUp,
                    Settled = settled,
                    Stats = stats,
                    TooltipSlot = kept.Hover,
                    TooltipRaw = kept.HoverRaw,
                    TooltipAge = kept.HoverAt == default
                        ? 0 : (frameAt - kept.HoverAt).TotalSeconds,
                    // The ratio for every frame, flagged when it clears the bar.
                    // The rise test needs the window's history and is settled
                    // at the close; trace.txt carries that verdict.
                    Flare = $"{ftop} {fratio:F2}X{(fslot is null ? "" : " HIT")}",
                    Titles = kept.Cards.ToDictionary(kv => kv.Key, kv => kv.Value.Name ?? ""),
                });
            }

            // Keep the newest frame that still shows a tooltip, for as long as the
            // window is open. Firing only on a *new* hover missed the player who
            // settled on a card and clicked before the next probe was allowed: 6
            // of 13 recorded picks fell back to a post-click grab, and every one
            // of those was useless.
            if ((probe is null || probe.IsCompleted) && DateTime.UtcNow - lastProbe > TooltipProbe)
            {
                lastProbe = DateTime.UtcNow;
                var target = kept;
                probe = Task.Run(() => ScanCardsAsync(target), token);
            }

            // Classify the window from its own tooltip while it is still open.
            // Mayhem also hands out item anvils behind a near-identical screen,
            // and deciding after a selection is detected is too late.
            if (kept.Cards.Count > 0 && kept.Cards.Count != seenRaw)
            {
                seenRaw = kept.Cards.Count;
                int itemsWin = kept.Cards.Values.Count(
                    c => c.ItemScore > c.Score && c.ItemScore >= Config.OcrMinScore);
                if (itemsWin >= 2 && !anvil)
                {
                    anvil = true;
                    Log.Write(Strings.Get("Loop.Anvil", string.Join(", ",
                        kept.Cards.Select(kv => $"{kv.Key}='{kv.Value.Raw}'"))));
                }
            }

            if (anvil)
                continue;

            // Card brightness is tracked as telemetry only; it no longer decides
            // anything -- see OnWindowClosedAsync.
            if (win.Baseline > 0)
            {
                var ranked = means.Values.OrderByDescending(v => v).ToArray();
                double ratio = ranked[0] / win.Baseline;
                if (ratio > diag.Peak)
                {
                    diag.Peak = ratio;
                    diag.PeakLosers = (ranked[1] / win.Baseline, ranked[2] / win.Baseline);
                }
            }
        }
    }

    /// <summary>
    /// One frame with the chosen card outlined in green and the other two in
    /// grey. Two get written per pick: <c>read</c>, the full-resolution frame
    /// the titles came off, and <c>close</c>, the last small frame the window
    /// was still <em>alive</em> in.
    ///
    /// Alive is not the same as having cards on it, and the name has misled
    /// before. Alive is held up by the hide button, which goes on matching after
    /// the cards have gone -- for over a second and a half in one capture -- so
    /// <c>close</c> is routinely a picture of the map with no cards in it. That
    /// is a true record of where the window ended and a poor one of what was
    /// chosen. Neither dump is the frame the flare decided on; the inspector
    /// serves that one.
    ///
    /// A wrong pick is a claim about which card was brighter, and the numbers
    /// alone do not settle it -- a cursor resting on a card looks the same in a
    /// ratio as a card being taken. These are the pictures that do, and it takes
    /// both: the readable one is not the deciding moment, and the deciding
    /// moment is not readable.
    /// </summary>
    /// <summary>
    /// The kept frame closest in time to a flare verdict, marked with the card
    /// that verdict named. Nothing is kept unless the log is on, and the strip
    /// only reaches back as far as the search does, so a verdict older than
    /// that writes nothing rather than the wrong picture.
    /// </summary>
    private static async Task DumpNearestAsync(
        List<(DateTime At, Frame Frame)> recent, FlareFound flare, int? level, string tag)
    {
        if (!Config.DebugMode || !flare.Found || recent.Count == 0)
            return;
        Frame? best = null;
        double bestGap = double.MaxValue;
        foreach (var (at, frame) in recent)
        {
            double gap = Math.Abs((at - flare.At).TotalSeconds);
            if (gap >= bestGap)
                continue;
            bestGap = gap;
            best = frame;
        }
        if (best is null || bestGap > 0.25)
            return;
        await DumpDecisionAsync(best, flare.Slot!, level, tag);
    }

    private static async Task DumpDecisionAsync(Frame? frame, string slot, int? level, string tag)
    {
        if (!Config.DebugMode || frame is null)
            return;
        try
        {
            var marked = new Frame(frame.Width, frame.Height,
                                   (byte[])frame.Bgra.Clone());
            foreach (var (key, box) in Config.Cards)
            {
                if (key == slot)
                    marked.DrawBox(box, 80, 220, 60, 5);      // chosen
                else
                    marked.DrawBox(box, 130, 130, 130, 3);
            }
            marked.DrawBox(Config.HoverTooltip, 255, 170, 60, 3);

            string path = Path.Combine(Config.State, "debug",
                $"{DateTime.Now:yyyyMMdd-HHmmss}_lv{level}_{slot}_{tag}.png");
            await Imaging.SavePngAsync(marked, path);
            Log.Write(Strings.Get("Loop.DumpSaved", path));
        }
        catch (Exception exc)
        {
            Log.Write(Strings.Get("Loop.DumpFailed", exc.Message));
        }
    }

    /// <summary>
    /// One frame showing where the tooltip finder decided the panel is.
    ///
    /// Written only when the verdict changes, and at most a handful per window:
    /// the scan runs three times a second and a picture of every one of those
    /// is a lot of disk to say the same thing. What the picture has to settle
    /// is whether the box being handed to OCR sits on the title, so the old
    /// fixed box is drawn beside the new one -- on the ordinary window they
    /// nearly coincide, and on a flipped one they are nowhere near each other.
    /// </summary>
    private async Task DumpTooltipAsync(Frame shot, Detect.TooltipPanel? panel, Kept kept)
    {
        if (!Config.DebugMode)
            return;
        string state = panel is { } p ? (p.Flipped ? "flipped" : "below") : "none";
        if (state == kept.TipDumped || kept.TipDumps >= 6)
            return;
        kept.TipDumped = state;
        kept.TipDumps++;
        try
        {
            var marked = shot.Clone();
            Detect.Mark(marked, panel);
            string path = Path.Combine(Config.State, "debug",
                $"{DateTime.Now:yyyyMMdd-HHmmss}_lv{_state.Level}_tip_{state}.png");
            await Imaging.SavePngAsync(marked, path);
            Log.Write(Strings.Get("Loop.TipDump", state, path));
        }
        catch (Exception exc)
        {
            Log.Write(Strings.Get("Loop.DumpFailed", exc.Message));
        }
    }

    private static double Now() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    private static string Summary(Diagnostics d, AugmentWindowState win) =>
        Strings.Get("Loop.Summary", d.Frames, d.Settled, d.GateMin.ToString("F2"),
            d.HideMax.ToString("F2"), d.Peak.ToString("F2"), win.Baseline.ToString("F1"));

    /// <summary>Best reading of one card's title, escalating scale until confident.</summary>
    private async Task<CardReading> ReadCardAsync(Frame shot, string slot)
    {
        var best = new CardReading();
        foreach (int scale in Config.CardScales)
        {
            string raw = await _ocr.ReadBoxAsync(shot, Config.CardTitles[slot], scale);
            if (raw.Length == 0)
                continue;
            var (aug, score) = _db.Match(raw);
            if (score > best.Score)
                best = new CardReading
                {
                    Name = aug?.Name, Score = score, Raw = raw,
                    ItemScore = _items.BestScore(raw), Scale = scale, Aug = aug,
                };
            if (best.Score >= Config.OcrMinScore)
                break;
        }
        return best;
    }

    /// <summary>
    /// Read all three card titles off one full-resolution frame.
    ///
    /// Rescans for as long as the window is open. Stopping once all three had
    /// been read assumed the offer is fixed for the life of the window -- it is
    /// not, because rerolling swaps cards and the reroll button is on that very
    /// screen. Per card, the newest confident reading wins: each card has its own
    /// reroll button, and discarding a whole frame because one title would not
    /// read left the others stale.
    /// </summary>
    private async Task ScanCardsAsync(Kept kept)
    {
        Frame? shot;
        try
        {
            // The source's own pixels when OBS has told us its size; the
            // configured size is the fallback for when it has not.
            shot = _sourceSize is not null
                ? await _probeObs.GrabNativeAsync(Config.OcrQuality)
                : await _probeObs.GrabAsync(Config.OcrW, Config.OcrH, Config.OcrQuality);
        }
        catch
        {
            return;
        }
        if (shot is null)
            return;
        var shotAt = DateTime.UtcNow;
        kept.Last = shot;

        int readNow = 0;
        foreach (string slot in Config.CardTitles.Keys)
        {
            var got = await ReadCardAsync(shot, slot);
            if (got.Score < Config.OcrMinScore)
                continue;
            readNow++;
            if (kept.Cards.TryGetValue(slot, out var was) && was.Name != got.Name)
                kept.Rerolls.Add((slot, was.Name, got.Name));
            kept.Cards[slot] = got;
            kept.Frame = shot;
            kept.At = DateTime.UtcNow;
        }
        bool gotAll = kept.Cards.Values.Count(c => c.Score >= Config.OcrMinScore)
                      == Config.CardTitles.Count;
        if (gotAll && readNow == Config.CardTitles.Count)
            kept.FullAt = DateTime.UtcNow;   // every title read on THIS frame

        // Which card the cursor is on, read off the tooltip rather than guessed
        // from brightness. Only counts when the tooltip names one of the three
        // titles being held, so a stale read cannot claim a slot.
        var names = kept.Cards.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        if (names.Count == 0)
            return;

        // Where the tooltip actually is on this frame, rather than where a
        // fixed box hopes it will be. The panel moves and resizes with the
        // text, and a long description pushes it above the anchor entirely --
        // which is how a level 3 window read 'Drop' off a card title the panel
        // was sitting on top of, and published DropBear over Dropkick.
        var panel = Detect.FindTooltip(Cv.ToGray(shot));
        var tipBox = panel?.Title ?? Config.HoverTooltip;
        kept.TipPanel = panel is { } p
            ? $"{(p.Flipped ? "flipped" : "below")} top={p.Top} x={p.X0}~{p.X1}"
            : "not found, using the fixed box";
        await DumpTooltipAsync(shot, panel, kept);

        (string, string, double)? miss = null;
        foreach (int scale in Config.CardScales)
        {
            string raw = await _ocr.ReadBoxAsync(shot, tipBox, scale);
            if (raw.Length == 0)
                continue;
            var (aug, score) = _db.Match(raw);
            if (aug is not null && score >= Config.OcrMinScore)
            {
                // Held whatever it turns out to name. The tooltip is a bigger,
                // unobstructed rendering of a name the card itself may be
                // covered by, and throwing it away unless it agrees with the
                // covered reading is how Dropkick was read as 'Drop' and
                // published as DropBear while the tooltip said Dropkick.
                kept.TipAug = aug;
                kept.TipRaw = raw;
                kept.TipScore = score;
            }
            string? slot = aug is not null && score >= Config.OcrMinScore
                ? names.FirstOrDefault(kv => kv.Value == aug.Name).Key
                : null;
            if (slot is not null)
            {
                kept.Hover = slot;
                kept.HoverRaw = raw;
                kept.HoverAt = shotAt;
                miss = null;
                break;
            }
            miss = (raw, aug?.Name ?? "-", Math.Round(score, 2));
        }
        if (miss is not null &&
            (kept.TipMisses.Count == 0 || kept.TipMisses[^1] != miss.Value))
            kept.TipMisses.Add(miss.Value);
    }

    /// <summary>
    /// Which card the cursor was on when the window went, read off brightness.
    ///
    /// Reading this off one full-resolution frame was wrong: that frame is
    /// whenever the titles last happened to be read. On one window it caught the
    /// reroll animation instead -- the freshly rerolled card glowed at 1.69x and
    /// got published, while the augment actually taken was the dimmest of three.
    /// Only the stretch just before the close is searched, because a reroll
    /// earlier in the window flares just as brightly.
    ///
    /// Within that stretch the *newest* qualifying frame wins, not the
    /// brightest. Taking the maximum published 閃光 for a window where 歯の妖精
    /// was taken: the player rerolled the left card and took the middle one, and
    /// the reroll animation at 1.56x, 0.7 s before the close, outshouted the
    /// 1.28x the hovered card held right up to the click. Brightness here is not
    /// a selection animation -- there is none -- it is where the cursor is
    /// resting, so the reading closest to the click is the one that matters.
    /// </summary>
    /// <summary>
    /// What one card's interior sat at earlier in this window, before anything
    /// that happens at the close could have moved it.
    ///
    /// A median, not a mean: a reroll inside the sampled stretch would drag a
    /// mean up and quietly raise the bar the flare has to clear.
    /// </summary>
    public static double BaselineInner(List<Beat> history, DateTime anchor, string slot)
    {
        var values = new List<double>();
        foreach (var beat in history)
        {
            double before = (anchor - beat.At).TotalSeconds;
            if (before > Config.FlareBaselineFromS || before < Config.FlareBaselineToS)
                continue;
            if (beat.CardsUp && beat.Stats.TryGetValue(slot, out var stat))
                values.Add(stat.Inner);
        }
        return values.Count == 0 ? 0.0 : Detect.Median(values.ToArray());
    }

    /// <summary>
    /// The last frame the reroll gate could still see cards in.
    ///
    /// This, not the close, is what the flare search is measured from. The two
    /// are not the same moment and the gap between them is not bounded: the
    /// close waits on the hide button, which one capture kept matching at 1.00
    /// for a second and a half after the cards had gone because the deck button
    /// under them stayed on screen through the shop being opened. Anchoring the
    /// search on the close would let that gap push the flare out of range.
    /// </summary>
    private static DateTime CardsLastSeen(List<Beat> history, DateTime fallback)
    {
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].CardsUp)
                return history[i].At;
        return fallback;
    }

    /// <summary>
    /// The newest frame just before the close that is a selection flare.
    ///
    /// Both tests have to pass. The ratio says this card is far brighter than
    /// the other two right now; the rise says it is far brighter than it was
    /// itself a moment ago. The shop is the case that shows why one is not
    /// enough on its own -- it clears the rise comfortably at 2.3-2.6x, because
    /// a panel edge cutting across the card boxes lifts one of them and holds
    /// it there, and only the ratio catches that for the static thing it is.
    /// </summary>
    /// <summary>True where a frame is close enough to the anchor to be a flare.</summary>
    private static bool InFlareWindow(DateTime at, DateTime anchor)
    {
        double off = (at - anchor).TotalSeconds;
        return off <= Config.FlareWindowAfterS && off >= -Config.FlareWindowS;
    }

    /// <summary>
    /// The same reach the search used before the anchor rule was added. Nothing
    /// is decided from it; a candidate found only out here is written into the
    /// log so that a wrong pick can be told apart from a signal that never
    /// existed. See <see cref="Config.FlareWindowS"/>.
    /// </summary>
    private static bool InWideFlareWindow(DateTime at, DateTime anchor)
    {
        double off = (at - anchor).TotalSeconds;
        return off <= Config.FlareWindowAfterS && off >= -Config.FlareWideWindowS;
    }

    /// <summary>
    /// What the flare search found, and the numbers needed to judge it later.
    ///
    /// <paramref name="CardsUpAfterS"/> is how long the reroll gate went on
    /// seeing cards after the winning frame; a selection wipes the screen, so
    /// on a real one this is zero. <paramref name="LoserRise"/> is what the two
    /// other cards were doing against their own earlier selves at that moment:
    /// a real pick drags them down (FINDINGS section 6 measured 0.51-0.70 of
    /// baseline), a reroll lights one card and leaves the others alone. That
    /// second number is recorded and not yet judged on -- section 6's figures
    /// are whole-card means against a shared baseline and do not transfer to
    /// this quantity, so the threshold has to be measured on these logs first.
    /// </summary>
    public readonly record struct FlareFound(
        string? Slot, DateTime At, double Ratio, double Rise,
        double CardsUpAfterS, double[] LoserRise, double Hide)
    {
        public bool Found => Slot is not null;

        /// <summary>One line for the log: the numbers, in the order they matter.</summary>
        public string Describe(DateTime closedAt) =>
            $"{Slot}  peak inner {Ratio:F2}x the next card, {Rise:F2}x its own baseline; " +
            $"frame {(closedAt - At).TotalSeconds:F2}s before the close, cards up for " +
            $"{CardsUpAfterS:F2}s after it; losers at " +
            $"{string.Join("/", LoserRise.Select(r => r.ToString("F2")))}x their own; " +
            $"hide {Hide:F2}";
    }

    private static readonly double[] NoLosers = Array.Empty<double>();

    /// <summary>
    /// The flare, and -- when the anchor rule threw one out -- what it threw out.
    /// </summary>
    public static (FlareFound Taken, FlareFound Rejected) SelectionFlare(
        List<Beat> history, DateTime closedAt)
    {
        DateTime anchor = CardsLastSeen(history, closedAt);
        // Nothing found: a slot of null with the arrays real, so a caller that
        // logs before checking Found does not fall over.
        var none = new FlareFound(null, default, 0, 0, 0, NoLosers, 0);
        FlareFound taken = none, rejected = none;

        for (int i = history.Count - 1; i >= 0; i--)
        {
            var beat = history[i];
            // The flare straddles the moment the cards go: it starts on the
            // frame after the gate dies and runs for about 80 ms, so the search
            // reaches both sides of the anchor rather than only backwards -- but
            // not equally far. Past the anchor there is only the length of the
            // event to cover; see Config.FlareWindowAfterS for what the surplus
            // let through, and Config.FlareWindowS for what the reach backwards
            // let through before it was cut to the length of the event.
            if (!InWideFlareWindow(beat.At, anchor))
                continue;
            var (slot, _, ratio) = Detect.Flare(beat.Stats);
            if (slot is null)
                continue;
            double baseline = BaselineInner(history, anchor, slot);
            if (baseline <= 0)
                continue;
            double rise = beat.Stats[slot].Inner / baseline;
            if (rise < Config.FlareRise)
                continue;

            bool inTime = InFlareWindow(beat.At, anchor);
            if (!inTime && rejected.Found)
                continue;                       // the newest near-miss is enough

            // The newest qualifying frame is what decides the slot, because a
            // reroll earlier in the window must not get to answer. But it is a
            // terrible thing to quote. The flare's light bleeds into the
            // neighbouring cards as it spreads, so the ratio falls frame by
            // frame, and the newest one over the bar is by construction the
            // last one before it drops under: eight real windows all reported
            // 3.07-3.36 against a threshold of 3.00 and read as though they
            // were scraping through, while their peaks were 3.7-6.2. Quote the
            // peak, or the log invites exactly the wrong conclusion about how
            // much room this test has.
            double peakRatio = ratio, peakRise = rise;
            foreach (var other in history)
            {
                if (!InWideFlareWindow(other.At, anchor))
                    continue;
                var (otherSlot, _, otherRatio) = Detect.Flare(other.Stats);
                if (otherSlot != slot || otherRatio <= peakRatio)
                    continue;
                peakRatio = otherRatio;
                peakRise = other.Stats[slot].Inner / baseline;
            }

            // What the other two cards were doing against their own earlier
            // selves on this frame. Written down, not judged on; see FlareFound.
            var loserRise = new List<double>();
            foreach (var (otherSlot, stat) in beat.Stats)
            {
                if (otherSlot == slot)
                    continue;
                double own = BaselineInner(history, anchor, otherSlot);
                loserRise.Add(own > 0 ? stat.Inner / own : 0);
            }
            var found = new FlareFound(slot, beat.At, peakRatio, peakRise,
                Math.Max(0, (anchor - beat.At).TotalSeconds), loserRise.ToArray(), beat.Hide);
            if (inTime)
                return (found, rejected);
            rejected = found;
        }
        return (taken, rejected);
    }

    /// <summary>
    /// Which card the cursor was resting on when the window went, read off
    /// brightness. The fallback of last resort, for the player who clicks
    /// without a tooltip ever being read and without a flare being caught.
    ///
    /// Reading this off one full-resolution frame was wrong: that frame is
    /// whenever the titles last happened to be read. On one window it caught the
    /// reroll animation instead -- the freshly rerolled card glowed at 1.69x and
    /// got published, while the augment actually taken was the dimmest of three.
    /// Only the stretch just before the close is searched, because a reroll
    /// earlier in the window flares just as brightly.
    ///
    /// Within that stretch the *newest* qualifying frame wins, not the
    /// brightest. Taking the maximum published 閃光 for a window where 歯の妖精
    /// was taken: the player rerolled the left card and took the middle one, and
    /// the reroll animation at 1.56x, 0.7 s before the close, outshouted the
    /// 1.28x the hovered card held right up to the click. Brightness here is not
    /// a selection animation -- that is <see cref="SelectionFlare"/> -- it is
    /// where the cursor is resting, so the reading closest to the click is the
    /// one that matters.
    ///
    /// The frame must be one the reroll gate could still see cards in.
    /// Accepting any frame the window was merely "alive" in is what published
    /// Marksmage over Transmute: Prismatic: the cards had gone, the hide button
    /// had not, and 1.23x was the ratio between two patches of map.
    /// </summary>
    public static (string? Slot, DateTime At, string Via) HoverGlowSlot(
        List<Beat> history, DateTime closedAt)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var beat = history[i];
            double ago = (closedAt - beat.At).TotalSeconds;
            if (ago > Config.FlareLookbackS)
                break;
            if (!beat.CardsUp)
                continue;
            var order = beat.Stats.OrderByDescending(kv => kv.Value.Mean).ToArray();
            if (order.Length < 2 || order[1].Value.Mean <= 0)
                continue;
            double ratio = order[0].Value.Mean / order[1].Value.Mean;
            if (ratio < Config.SelectFlare)
                continue;
            return (order[0].Key, beat.At, Strings.Get("Loop.ViaFlare",
                ratio.ToString("F2"), ago.ToString("F1")));
        }
        return (null, default, "");
    }

    /// <summary>
    /// The window shutting IS the pick, and there are three ways to say which card.
    ///
    /// The original design watched for a confirmation animation and an early
    /// reading concluded there was none: across three windows the winner crossed
    /// its threshold 39 times while the losers never dropped below 0.96 of
    /// baseline, which is the signature of a hover. That conclusion was wrong,
    /// and it was wrong because three windows of region averages is not enough
    /// to see an 83 ms event. A frame-by-frame capture has it plainly -- see
    /// <see cref="SelectionFlare"/> -- so the flare is now the first thing
    /// asked, ahead of the tooltip.
    ///
    /// Order matters and this is the reasoning for it. The flare is the game
    /// stating what was taken, at the instant it was taken. The tooltip is the
    /// game naming what the cursor was over, at whatever moment the last scan
    /// managed to catch, which can be a second stale and on the wrong card by
    /// then -- a level 8 window published Hextech Soul off a tooltip 1.0 s old
    /// while the player had moved on and taken Stackosaurus Rex. Hover
    /// brightness is neither; it is an inference, and it is last.
    /// </summary>
    private async Task OnWindowClosedAsync(
        AugmentWindowState win, Kept kept, Diagnostics diag, bool anvil, int? level,
        List<Beat> history, object picksLock, Task? probe, Frame? lastAlive,
        List<(DateTime At, Frame Frame)> recent)
    {
        var why = new List<string>();
        void Note(string line)
        {
            why.Add(line);
            Log.Write("    · " + line);
        }

        if (anvil)
        {
            Log.Write(Strings.Get("Loop.AnvilSkipped"));
            why.Add("abandoned: item anvil screen, not an augment window");
            await Observe.FinishAsync(why);
            return;
        }
        if (diag.Settled == 0)
        {
            Log.Write(Strings.Get("Loop.NeverSettled"));
            why.Add("abandoned: the cards never settled");
            await Observe.FinishAsync(why);
            return;
        }

        // A scan is usually still in flight when the close is noticed, and it is
        // the one holding the tooltip from the moment of the click. Deciding
        // without it threw that away: the window that published 閃光 had the
        // correct 歯の妖精 tooltip land a moment later, in time for the debug
        // dump and too late for the pick. A scan that grabbed its frame after
        // the window went reads nothing and changes nothing, so waiting is only
        // ever an improvement.
        if (probe is not null && !probe.IsCompleted)
            await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(1.5)));

        // Ages are measured from the last frame the window was still up, not
        // from now: the close is only declared after CloseMisses frames of
        // absence, so "now" is over a second late and would make every reading
        // look stale by the same amount.
        DateTime closedAt = DateTime.UtcNow;
        Dictionary<string, Detect.CardStat>? closeStats = null;
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].Alive) { closedAt = history[i].At; closeStats = history[i].Stats; break; }

        // What the cards looked like at the close, written down whether or not
        // brightness ends up deciding. It is one line and it is the line that
        // says whether a wrong pick was brightness being overruled or brightness
        // being right and ignored.
        if (closeStats is not null)
        {
            var byMean = closeStats.OrderByDescending(kv => kv.Value.Mean).ToArray();
            Log.Write(Strings.Get("Loop.CloseMeans",
                string.Join(", ", closeStats.Select(kv => $"{kv.Key}={kv.Value.Mean:F1}")),
                byMean[0].Key,
                byMean[1].Value.Mean > 0
                    ? (byMean[0].Value.Mean / byMean[1].Value.Mean).ToString("F2") : "-"));
        }

        // All three signals are worked out every time, not just the one that
        // wins. A wrong pick is almost always two of them disagreeing, and that
        // cannot be seen after the fact unless the losers are written down too.
        var (flare, flareRejected) = SelectionFlare(history, closedAt);
        string? flareSlot = flare.Slot;
        var (glowSlot, glowAt, glowVia) = HoverGlowSlot(history, closedAt);

        // The tooltip is exact OCR of the card under the cursor -- but only
        // while it is current. Hover is never cleared, so an old one keeps
        // naming a card the player moved off seconds ago, and that is precisely
        // how ピンボール came to be nominated for a window in which the cursor
        // ended on 歯の妖精.
        string? hoverSlot = kept.Hover;
        double hoverAge = (closedAt - kept.HoverAt).TotalSeconds;
        if (hoverSlot is not null && hoverAge > Config.HoverTrustS)
        {
            Log.Write(Strings.Get("Loop.HoverStale", hoverSlot, hoverAge.ToString("F1")));
            Note($"tooltip '{kept.HoverRaw}' on {hoverSlot} discarded, {hoverAge:F1}s old");
            hoverSlot = null;
        }

        Note(flare.Found ? "flare      " + flare.Describe(closedAt) : "flare      none");
        // A candidate the anchor rule threw out is the single most useful line
        // in this log: it says a card lit up and the screen carried on, which
        // is a reroll, and it names the card so a wrong pick can be traced.
        if (flareRejected.Found)
            Note("flare xx   " + flareRejected.Describe(closedAt) + "  -> not a selection");
        Note(hoverSlot is null
            ? "tooltip    none current"
            : $"tooltip    {hoverSlot}  '{kept.HoverRaw}', {hoverAge:F1}s old");
        Note($"tip panel  {(kept.TipPanel.Length > 0 ? kept.TipPanel : "no scan ran")}");
        Note(glowSlot is null
            ? "hover glow none"
            : $"hover glow {glowSlot}  {glowVia}");

        string? slot;
        string via;
        if (flareSlot is not null)
        {
            if (hoverSlot is not null && hoverSlot != flareSlot)
                Log.Write(Strings.Get("Loop.FlareOverTooltip", flareSlot, hoverSlot,
                    kept.HoverRaw, hoverAge.ToString("F1")));
            slot = flareSlot;
            via = Strings.Get("Loop.ViaSelectFlare",
                flare.Ratio.ToString("F1"), flare.Rise.ToString("F1"));
            Note($"taken      {slot} on the flare");
        }
        else if (hoverSlot is not null)
        {
            if (glowSlot is not null && glowSlot != hoverSlot)
                Log.Write(Strings.Get("Loop.SignalsDisagree", glowSlot, hoverSlot, kept.HoverRaw,
                    hoverAge.ToString("F1")));
            slot = hoverSlot;
            via = Strings.Get("Loop.ViaTooltip", kept.HoverRaw);
            Note($"taken      {slot} on the tooltip");
        }
        else if (glowSlot is not null)
        {
            slot = glowSlot;
            via = glowVia;
            Note($"taken      {slot} on hover brightness (last resort)");
        }
        else
        {
            Log.Write(Strings.Get("Loop.Undecidable"));
            why.Add("abandoned: no signal named a card");
            await Observe.FinishAsync(why);
            return;
        }

        kept.Cards.TryGetValue(slot, out var card);
        var aug = card?.Aug;
        string ocrRaw = card?.Raw ?? "";
        double ocrScore = card?.Score ?? 0.0;
        int ocrScale = card?.Scale ?? 0;

        // A card whose own title did not read cleanly is usually one the
        // tooltip is sitting on top of, and the tooltip is a better reading of
        // the same name. Taking it needs one check, because the tooltip belongs
        // to wherever the cursor was when the last scan ran and that is 0.2 to
        // 0.5 s before the click -- long enough to have been over a different
        // card. The cursor is on the taken card at the moment of the click, but
        // this reading is not from that moment.
        //
        // The check is free: a tooltip covers one card, so the other two are
        // always legible. If the name it gives is one of those two, the cursor
        // was still there and the reading is not about this slot. If it is
        // neither, there is nowhere else for it to have come from.
        if (ocrScore < Config.OcrConfident && kept.TipAug is not null)
        {
            var elsewhere = kept.Cards.FirstOrDefault(kv =>
                kv.Key != slot && kv.Value.Score >= Config.OcrConfident &&
                kv.Value.Name == kept.TipAug.Name);
            if (elsewhere.Key is null)
            {
                Note($"title      {slot} read '{ocrRaw}' at {ocrScore:F2}; tooltip says " +
                     $"'{kept.TipRaw}' and neither other card is that -> taking the tooltip");
                aug = kept.TipAug;
                ocrRaw = kept.TipRaw;
                ocrScore = kept.TipScore;
                ocrScale = 0;
            }
            else
            {
                Note($"title      tooltip '{kept.TipRaw}' is {elsewhere.Key}'s card, " +
                     $"not {slot}'s -> keeping {slot}'s own reading");
            }
        }

        if (aug is null || ocrScore < Config.OcrMinScore)
        {
            string got = card is not null
                ? $"'{ocrRaw}' {ocrScore:F2}" : Strings.Get("Loop.NoReading");
            Log.Write(Strings.Get("Loop.TitleUnconfirmed", slot, got));
            why.Add($"abandoned: {slot} title never read confidently ({got})");
            await Observe.FinishAsync(why);
            return;
        }

        // The name is what the rarity is read off, not the card border. Border
        // colour has been wrong repeatedly, while an exact name gives the real
        // rarity from the data. The border only settles ties: 처형자 exists as
        // both gold and silver, and nothing but the screen can say which.
        if (aug is not null)
        {
            var sameName = _db.Augments.Where(a => a.Name == aug.Name).ToArray();
            if (sameName.Select(a => a.Rarity).Distinct().Count() > 1 && kept.Frame is not null)
            {
                string seen = Detect.RarityOf(kept.Frame, slot);
                aug = sameName.FirstOrDefault(a => a.Rarity == seen) ?? aug;
            }
        }
        if (aug is null)
        {
            why.Add($"abandoned: '{ocrRaw}' matched no augment");
            await Observe.FinishAsync(why);
            return;
        }

        foreach (var (slot_, was, now) in kept.Rerolls)
            Log.Write(Strings.Get("Loop.RerollSeen", slot_, was, now));
        if (kept.TipMisses.Count > 0)
            Log.Write(Strings.Get("Loop.TooltipMisses",
                string.Join(" | ", kept.TipMisses.TakeLast(3)
                    .Select(m => $"'{m.Raw}'->{m.Name}({m.Score})")),
                kept.TipMisses.Count));
        else if (kept.Hover is null)
            Log.Write(Strings.Get("Loop.TooltipNever"));

        var confirmedAt = kept.FullAt == default ? kept.At : kept.FullAt;
        double age = (DateTime.UtcNow - confirmedAt).TotalSeconds;
        if (age > 2.0)
            Log.Write(Strings.Get("Loop.StaleOffer", age.ToString("F1")));

        lock (picksLock)
        {
            _state.Picks.Add(new Pick
            {
                Name = aug.Name, Rarity = aug.Rarity, IconUrl = aug.IconUrl,
                Level = level, Slot = slot,
                Confidence = Math.Round(ocrScore, 2), OcrRaw = ocrRaw,
                Via = via,
                Tooltip = kept.Hover is null ? "" : $"{kept.Hover}={kept.HoverRaw}",
                Others = string.Join(", ", kept.Cards.Where(kv => kv.Key != slot)
                    .Select(kv => $"{kv.Key}={kv.Value.Name}")),
            });
        }
        _server.Save();

        Log.Write(Strings.Get("Loop.Picked", aug.Name, aug.Rarity,
            ocrScore.ToString("F2"), ocrRaw, ocrScale, slot, via));
        Log.Write(Strings.Get("Loop.Others", string.Join(", ",
            kept.Cards.Where(kv => kv.Key != slot)
                .Select(kv => $"{kv.Key}={kv.Value.Name}"))));

        await DumpDecisionAsync(kept.Frame, slot, level, "read");
        await DumpDecisionAsync(lastAlive, slot, level, "close");
        // The frame the flare was decided from, and the one the anchor rule
        // threw out. Without these a wrong pick can be argued about but not
        // looked at: the close frame is up to a second later and shows only
        // where the map had got to by then.
        await DumpNearestAsync(recent, flare, level, "flare");
        await DumpNearestAsync(recent, flareRejected, level, "flarexx");

        why.Add($"published   {aug.Name} ({aug.Rarity}) from {slot}, via {via}");
        why.Add("others      " + string.Join(", ", kept.Cards.Where(kv => kv.Key != slot)
            .Select(kv => $"{kv.Key}={kv.Value.Name}")));
        foreach (var (slot_, was, now) in kept.Rerolls)
            why.Add($"reroll      {slot_} {was} -> {now}");
        await Observe.FinishAsync(why);
    }
}
