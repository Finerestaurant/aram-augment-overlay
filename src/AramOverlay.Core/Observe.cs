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
}

/// <summary>
/// Somewhere for the loop to hand what it saw, with nothing behind it.
///
/// Everything that exists to explain a verdict after the fact lives in the
/// SelfTest tool and attaches here. None of it is in the published exe, so
/// there is no setting to leave switched on by accident and no code path a user
/// can reach: the trace was 300 lines of frame annotation and JPEG encoding
/// riding along in an app that never had a use for it.
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

    public static void BeginWindow(int? level) => Sink?.BeginWindow(level);
    public static void Frame(Frame frame, TraceSample sample) => Sink?.Frame(frame, sample);
    public static Task FinishAsync(IReadOnlyList<string> verdict) =>
        Sink?.FinishAsync(verdict) ?? Task.CompletedTask;
    public static void EndWindow() => Sink?.EndWindow();
}
