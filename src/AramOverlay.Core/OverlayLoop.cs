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
    public List<(string Raw, string Name, double Score)> TipMisses = new();
}

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
        var flareHistory = new List<(DateTime At, Dictionary<string, double> Means, bool Alive)>();
        var picksLock = new object();

        int sizedSeq = 0;
        bool anvil = false;
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
                    flareHistory.Clear();
                    anvil = false;
                    seenRaw = null;
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
                    await OnWindowClosedAsync(win, kept, diag, anvil, level, flareHistory, picksLock);
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

            var means = Detect.CardMeans(gray);
            flareHistory.Add((DateTime.UtcNow, means, weak));
            if (flareHistory.Count > 45)             // ~4 s at the loop's cadence
                flareHistory.RemoveRange(0, 10);

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

        (string, string, double)? miss = null;
        foreach (int scale in Config.CardScales)
        {
            string raw = await _ocr.ReadBoxAsync(shot, Config.HoverTooltip, scale);
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
    /// Which card lit up, found by walking back from the close.
    ///
    /// Reading this off one full-resolution frame was wrong: that frame is
    /// whenever the titles last happened to be read. On one window it caught the
    /// reroll animation instead -- the freshly rerolled card glowed at 1.69x and
    /// got published, while the augment actually taken was the dimmest of three.
    /// Only the stretch just before the close is searched, because a reroll
    /// earlier in the window flares just as brightly.
    /// </summary>
    private static (string? Slot, string Via) FlareSlot(
        List<(DateTime At, Dictionary<string, double> Means, bool Alive)> history, DateTime now)
    {
        (string Slot, double Ratio, double Ago)? best = null;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var (at, means, alive) = history[i];
            double ago = (now - at).TotalSeconds;
            if (ago > Config.FlareLookbackS)
                break;
            if (!alive)
                continue;
            var order = means.OrderByDescending(kv => kv.Value).ToArray();
            if (order.Length < 2 || order[1].Value <= 0)
                continue;
            double ratio = order[0].Value / order[1].Value;
            if (ratio >= Config.SelectFlare && (best is null || ratio > best.Value.Ratio))
                best = (order[0].Key, ratio, ago);
        }
        if (best is null)
            return (null, "");
        return (best.Value.Slot, Strings.Get("Loop.ViaFlare",
            best.Value.Ratio.ToString("F2"), best.Value.Ago.ToString("F1")));
    }

    /// <summary>
    /// The window shutting IS the pick.
    ///
    /// The original design watched for a confirmation animation -- the taken card
    /// flaring while the other two collapsed. That animation does not happen
    /// here: across three confirmed windows the winner crossed its threshold 39
    /// times while the losers never dropped below 0.96 of baseline, which is the
    /// signature of a hover, not a selection. What is reliable is the window
    /// itself, so the pick is read off the last tooltip seen before it went away.
    /// </summary>
    private async Task OnWindowClosedAsync(
        AugmentWindowState win, Kept kept, Diagnostics diag, bool anvil, int? level,
        List<(DateTime At, Dictionary<string, double> Means, bool Alive)> history,
        object picksLock)
    {
        if (anvil)
        {
            Log.Write(Strings.Get("Loop.AnvilSkipped"));
            return;
        }
        if (diag.Settled == 0)
        {
            Log.Write(Strings.Get("Loop.NeverSettled"));
            return;
        }

        // Two signals, both measured against known answers. Card brightness over
        // the window as a whole is not a third: it was wrong on the windows it
        // was asked to decide, and a confident wrong augment on stream is worse
        // than a gap.
        var (slot, via) = FlareSlot(history, DateTime.UtcNow);
        if (slot is null && kept.Hover is not null)
        {
            slot = kept.Hover;
            via = Strings.Get("Loop.ViaTooltip", kept.HoverRaw);
        }
        if (slot is null)
        {
            Log.Write(Strings.Get("Loop.Undecidable"));
            return;
        }

        if (!kept.Cards.TryGetValue(slot, out var card) || card.Score < Config.OcrMinScore)
        {
            string got = card is not null
                ? $"'{card.Raw}' {card.Score:F2}" : Strings.Get("Loop.NoReading");
            Log.Write(Strings.Get("Loop.TitleUnconfirmed", slot, got));
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
            return;

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
            });
        }
        _server.Save();

        Log.Write(Strings.Get("Loop.Picked", aug.Name, aug.Rarity,
            card.Score.ToString("F2"), card.Raw, card.Scale, slot, via));
        Log.Write(Strings.Get("Loop.Others", string.Join(", ",
            kept.Cards.Where(kv => kv.Key != slot)
                .Select(kv => $"{kv.Key}={kv.Value.Name}"))));
        await Task.CompletedTask;
    }
}
