using System.Text.Json.Serialization;

namespace AramOverlay.Core;

/// <summary>One frame the inspector is holding, with what it measured.</summary>
public sealed class InspectFrame
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>read, close, flare, or a strip index.</summary>
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    /// <summary>Seconds from the anchor. Negative is before it.</summary>
    [JsonPropertyName("t")] public double T { get; set; }
    [JsonPropertyName("cards_up")] public bool CardsUp { get; set; }
    [JsonPropertyName("alive")] public bool Alive { get; set; }
    /// <summary>Interior brightness per slot, the numbers the flare test runs on.</summary>
    [JsonPropertyName("inner")] public Dictionary<string, double> Inner { get; set; } = new();
    [JsonPropertyName("mean")] public Dictionary<string, double> Mean { get; set; } = new();
    /// <summary>Top interior over the next one. The flare's first axis.</summary>
    [JsonPropertyName("ratio")] public double Ratio { get; set; }
    /// <summary>Top slot over its own baseline. The second axis.</summary>
    [JsonPropertyName("rise")] public double Rise { get; set; }
    [JsonPropertyName("top")] public string Top { get; set; } = "";
    /// <summary>True where this frame would pass both flare axes on its own.</summary>
    [JsonPropertyName("passes")] public bool Passes { get; set; }
    /// <summary>The frame the flare search actually settled on.</summary>
    [JsonPropertyName("decided")] public bool Decided { get; set; }
    /// <summary>Inside the flare search range. Frames outside it are drawn as
    /// out of play, which is the whole story when a verdict came off one.</summary>
    [JsonPropertyName("in_window")] public bool InWindow { get; set; }

    [JsonIgnore] public byte[] Bytes { get; set; } = Array.Empty<byte>();
}

/// <summary>What one augment window decided, and the frames it decided from.</summary>
public sealed class InspectWindow
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("at")] public string At { get; set; } = "";
    [JsonPropertyName("level")] public int? Level { get; set; }

    /// <summary>The published augment, or empty when the window was abandoned.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("rarity")] public string Rarity { get; set; } = "";
    [JsonPropertyName("slot")] public string Slot { get; set; } = "";
    [JsonPropertyName("via")] public string Via { get; set; } = "";
    [JsonPropertyName("abandoned")] public string Abandoned { get; set; } = "";

    /// <summary>What each signal said, whether or not it won. A wrong pick is
    /// two of them disagreeing, and that cannot be seen unless the losers are
    /// written down too.</summary>
    [JsonPropertyName("flare")] public string Flare { get; set; } = "";
    [JsonPropertyName("flare_slot")] public string FlareSlot { get; set; } = "";
    [JsonPropertyName("tooltip")] public string Tooltip { get; set; } = "";
    [JsonPropertyName("tooltip_slot")] public string TooltipSlot { get; set; } = "";
    [JsonPropertyName("glow")] public string Glow { get; set; } = "";
    [JsonPropertyName("glow_slot")] public string GlowSlot { get; set; } = "";

    /// <summary>Set when the winning signal named a different card than another
    /// signal did. The one thing worth seeing from across the room.</summary>
    [JsonPropertyName("disputed")] public bool Disputed { get; set; }

    [JsonPropertyName("cards")] public Dictionary<string, string> Cards { get; set; } = new();
    [JsonPropertyName("rerolls")] public List<string> Rerolls { get; set; } = new();
    [JsonPropertyName("why")] public List<string> Why { get; set; } = new();
    [JsonPropertyName("frames")] public List<InspectFrame> Frames { get; set; } = new();
}

/// <summary>
/// The last few augment windows, with the frames their verdicts came off.
///
/// This exists because the log says which reading won and cannot say what was
/// on screen when it did. A false flare reads as an ordinary flare in the
/// numbers -- the frame that produced it is the only thing that tells them
/// apart, and until now that frame was decoded, measured and dropped.
///
/// Everything is held in memory and nothing is written to disk. The window that
/// matters is the one that just happened, the ring is short, and a debug tool
/// that fills a drive is worse than no debug tool.
/// </summary>
public static class Inspector
{
    private static readonly object Gate = new();
    private static readonly List<InspectWindow> Windows = new();
    private static int _seq;

    /// <summary>Bumped on every change, so the page can skip a redraw.</summary>
    public static int Version { get; private set; }

    public static void Add(InspectWindow window)
    {
        lock (Gate)
        {
            window.Id = $"w{++_seq}";
            foreach (var frame in window.Frames)
                frame.Id = $"{window.Id}-{frame.Id}";
            Windows.Insert(0, window);
            while (Windows.Count > Config.InspectWindows)
                Windows.RemoveAt(Windows.Count - 1);
            Version++;
        }
    }

    public static List<InspectWindow> Snapshot()
    {
        lock (Gate)
            return new List<InspectWindow>(Windows);
    }

    public static byte[]? Frame(string id)
    {
        lock (Gate)
        {
            foreach (var window in Windows)
                foreach (var frame in window.Frames)
                    if (frame.Id == id)
                        return frame.Bytes;
            return null;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Windows.Clear();
            Version++;
        }
    }
}
