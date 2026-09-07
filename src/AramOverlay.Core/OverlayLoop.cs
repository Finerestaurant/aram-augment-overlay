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
public readonly record struct Beat(
    DateTime At, Dictionary<string, Detect.CardStat> Stats, bool Alive, bool CardsUp);

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
        int? seenRaw = null;
        double? currentGameId = null;
        DateTime lastProbe = DateTime.MinValue;
        Task? probe = null;
        int? level = null;

        Log.Write(Strings.Get("Loop.Waiting"));

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
                win = new AugmentWindowState();
                Trace.End();
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

            // A fresh game rewinds gameTime; start a new list.
            if (currentGameId is not null && gameId + 5 < currentGameId)
            {
                Log.Write(Strings.Get("Loop.NewGame"));
                lock (picksLock) _state.Picks.Clear();
                _server.Save();
            }
            currentGameId = gameId;

            if (mode != Config.MayhemGameMode)
            {
                await Task.Delay(3000, token);
                continue;
            }

            var frame = await _obs.GrabAsync();
            if (frame is null)
            {
                await Task.Delay(1000, token);
                continue;
            }

            var gray = Cv.ToGray(frame);
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
                    anvil = false;
                    seenRaw = null;
                    traceIndex = 0;
                    Trace.Begin(level);
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
                                             picksLock, probe, lastAlive);
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
            history.Add(new Beat(frameAt, stats, weak, cardsUp));
            if (weak)
                lastAlive = frame;
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

            if (Trace.Active)
            {
                var (fslot, ftop, fratio) = Detect.Flare(stats);
                Trace.Add(frame, new TraceSample
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
    /// was still up in.
    ///
    /// A wrong pick is a claim about which card was brighter, and the numbers
    /// alone do not settle it -- a cursor resting on a card looks the same in a
    /// ratio as a card being taken. These are the pictures that do, and it takes
    /// both: the readable one is not the deciding moment, and the deciding
    /// moment is not readable.
    /// </summary>
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
            shot = await _probeObs.GrabAsync(Config.BaseW, Config.BaseH, Config.OcrQuality);
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
    public static (string? Slot, DateTime At, double Ratio, double Rise) SelectionFlare(
        List<Beat> history, DateTime closedAt)
    {
        DateTime anchor = CardsLastSeen(history, closedAt);
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var beat = history[i];
            // The flare straddles the moment the cards go: it starts on the
            // frame after the gate dies and runs for about 80 ms, so the search
            // reaches both sides of the anchor rather than only backwards.
            if (Math.Abs((beat.At - anchor).TotalSeconds) > Config.FlareWindowS)
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
                if (Math.Abs((other.At - anchor).TotalSeconds) > Config.FlareWindowS)
                    continue;
                var (otherSlot, _, otherRatio) = Detect.Flare(other.Stats);
                if (otherSlot != slot || otherRatio <= peakRatio)
                    continue;
                peakRatio = otherRatio;
                peakRise = other.Stats[slot].Inner / baseline;
            }
            return (slot, beat.At, peakRatio, peakRise);
        }
        return (null, default, 0, 0);
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
        List<Beat> history, object picksLock, Task? probe, Frame? lastAlive)
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
            await Trace.FinishAsync(why);
            return;
        }
        if (diag.Settled == 0)
        {
            Log.Write(Strings.Get("Loop.NeverSettled"));
            why.Add("abandoned: the cards never settled");
            await Trace.FinishAsync(why);
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
        var (flareSlot, flareAt, flareRatio, flareRise) = SelectionFlare(history, closedAt);
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

        Note(flareSlot is null
            ? "flare      none"
            : $"flare      {flareSlot}  peak inner {flareRatio:F2}x the next card, {flareRise:F2}x its " +
              $"own baseline; decided off the frame {(closedAt - flareAt).TotalSeconds:F2}s before the close");
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
                flareRatio.ToString("F1"), flareRise.ToString("F1"));
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
            await Trace.FinishAsync(why);
            return;
        }

        if (!kept.Cards.TryGetValue(slot, out var card) || card.Score < Config.OcrMinScore)
        {
            string got = card is not null
                ? $"'{card.Raw}' {card.Score:F2}" : Strings.Get("Loop.NoReading");
            Log.Write(Strings.Get("Loop.TitleUnconfirmed", slot, got));
            why.Add($"abandoned: {slot} title never read confidently ({got})");
            await Trace.FinishAsync(why);
            return;
        }

        // The name is what the rarity is read off, not the card border. Border
        // colour has been wrong repeatedly, while an exact name gives the real
        // rarity from the data. The border only settles ties: 처형자 exists as
        // both gold and silver, and nothing but the screen can say which.
        var aug = card.Aug;
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
            why.Add($"abandoned: '{card.Raw}' matched no augment");
            await Trace.FinishAsync(why);
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
                Confidence = Math.Round(card.Score, 2), OcrRaw = card.Raw,
                Via = via,
                Tooltip = kept.Hover is null ? "" : $"{kept.Hover}={kept.HoverRaw}",
                Others = string.Join(", ", kept.Cards.Where(kv => kv.Key != slot)
                    .Select(kv => $"{kv.Key}={kv.Value.Name}")),
            });
        }
        _server.Save();

        Log.Write(Strings.Get("Loop.Picked", aug.Name, aug.Rarity,
            card.Score.ToString("F2"), card.Raw, card.Scale, slot, via));
        Log.Write(Strings.Get("Loop.Others", string.Join(", ",
            kept.Cards.Where(kv => kv.Key != slot)
                .Select(kv => $"{kv.Key}={kv.Value.Name}"))));

        await DumpDecisionAsync(kept.Frame, slot, level, "read");
        await DumpDecisionAsync(lastAlive, slot, level, "close");

        why.Add($"published   {aug.Name} ({aug.Rarity}) from {slot}, via {via}");
        why.Add("others      " + string.Join(", ", kept.Cards.Where(kv => kv.Key != slot)
            .Select(kv => $"{kv.Key}={kv.Value.Name}")));
        foreach (var (slot_, was, now) in kept.Rerolls)
            why.Add($"reroll      {slot_} {was} -> {now}");
        await Trace.FinishAsync(why);
    }
}
