namespace AramOverlay.Core;

/// <summary>One frame's worth of everything the loop measured.</summary>
public sealed class TraceSample
{
    public int Index;
    public double T;
    public double[] Gate = { 0, 0, 0 };
    public double Hide;
    public bool Alive;
    public bool CardsUp;
    public bool Settled;
    public Dictionary<string, Detect.CardStat> Stats = new();
    public string? TooltipSlot;
    public string TooltipRaw = "";
    public double TooltipAge;
    public string Flare = "";
    public Dictionary<string, string> Titles = new();

    /// <summary>
    /// The window's running baseline on this frame -- the denominator of the
    /// rise test. The trace never needed it because trace.txt writes the verdict
    /// that already used it; the inspector recomputes the test frame by frame
    /// and cannot without it.
    /// </summary>
    public double Baseline;

    /// <summary>Brightest card minus dimmest, the hover test's own quantity.</summary>
    public double HoverSpread;

    /// <summary>Where the tooltip finder last put the panel, null while it found none.</summary>
    public Detect.TooltipPanel? TipPanel;

    /// <summary>
    /// How long ago that was. The panel is found on the OCR grab, so it is
    /// always from another frame -- drawing it without this says a tooltip was
    /// on a frame that never had one.
    /// </summary>
    public double TipPanelAge;
}

/// <summary>
/// Somewhere for the loop to hand what it saw, with nothing behind it.
///
/// The loop states what it measured and knows nothing about who is listening,
/// so a second tool costs the loop no branches of its own. Two things attach
/// here: SelfTest's <c>--trace</c>, which is not in the published exe at all,
/// and the inspector, which is -- behind a setting that is off by default and
/// writes nothing until someone turns it on. The inspector earns that place:
/// three wrong picks in one evening could be argued about from the log and not
/// looked at, because by the time anything was written the deciding frame was
/// gone.
///
/// Every call is null-checked, so an unattached loop pays a branch and a
/// return. <see cref="ILoopObserver.WantsFrames"/> is the one the loop asks
/// before doing work rather than after, since a frame kept for an observer that
/// does not exist is pure waste.
/// </summary>
public interface ILoopObserver
{
    /// <summary>True when this observer keeps pictures and not just numbers.</summary>
    bool WantsFrames { get; }

    void BeginWindow(int? level);
    void Frame(Frame frame, TraceSample sample);

    /// <summary>
    /// Something that happened at an instant rather than over one, placed on the
    /// window's own clock: a card rerolled, the flare the search took, the one
    /// it threw out, the last frame the cards were up. These are not derivable
    /// from the per-frame numbers -- a reroll is a title changing between two
    /// OCR scans, and which flare was taken is the search's answer, not the
    /// data's -- so the loop has to say them out loud.
    /// </summary>
    void Mark(double t, string kind, string text);

    /// <summary>Close the window out, verdict included. Runs on abandoned windows too.</summary>
    Task FinishAsync(IReadOnlyList<string> verdict);

    void EndWindow();
}

/// <summary>The attachment point. Null unless a developer tool put itself here.</summary>
public static class Observe
{
    public static ILoopObserver? Sink { get; set; }

    public static bool Active => Sink is not null;
    public static bool WantsFrames => Sink?.WantsFrames ?? false;

    /// <summary>
    /// Put the inspector on the hook, or take it off, following its setting.
    ///
    /// A trace attached by SelfTest is never disturbed: it is the deeper tool of
    /// the two and it was attached on purpose by someone at a command line, so a
    /// setting read at startup has no business replacing it.
    /// </summary>
    public static void Attach(bool inspector)
    {
        if (inspector)
        {
            if (Sink is null)
                Sink = new InspectorSink();
        }
        else if (Sink is InspectorSink)
        {
            Sink = null;
        }
    }

    public static void BeginWindow(int? level) => Sink?.BeginWindow(level);
    public static void Frame(Frame frame, TraceSample sample) => Sink?.Frame(frame, sample);
    public static void Mark(double t, string kind, string text) => Sink?.Mark(t, kind, text);
    public static Task FinishAsync(IReadOnlyList<string> verdict) =>
        Sink?.FinishAsync(verdict) ?? Task.CompletedTask;
    public static void EndWindow() => Sink?.EndWindow();
}
