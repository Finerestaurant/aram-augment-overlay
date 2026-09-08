using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AramOverlay.Core;

namespace AramOverlay.SelfTest;

/// <summary>
/// A 5x7 bitmap font, because the trace has to label itself.
///
/// WPF is not reachable from Core and WinRT's text rendering wants a whole
/// composition stack for what amounts to twenty characters of "0.94". Five
/// bytes per glyph, one bit per pixel, columns left to right and rows top to
/// bottom, covering 0x20..0x5F -- digits, capitals and the punctuation the
/// numbers need. Anything else is drawn as '?'.
/// </summary>
internal static class Glyphs
{
    private const int First = 0x20, Last = 0x5F;

    private static readonly byte[] Rom =
    {
        0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x5F,0x00,0x00, 0x00,0x07,0x00,0x07,0x00,
        0x14,0x7F,0x14,0x7F,0x14, 0x24,0x2A,0x7F,0x2A,0x12, 0x23,0x13,0x08,0x64,0x62,
        0x36,0x49,0x55,0x22,0x50, 0x00,0x05,0x03,0x00,0x00, 0x00,0x1C,0x22,0x41,0x00,
        0x00,0x41,0x22,0x1C,0x00, 0x14,0x08,0x3E,0x08,0x14, 0x08,0x08,0x3E,0x08,0x08,
        0x00,0x50,0x30,0x00,0x00, 0x08,0x08,0x08,0x08,0x08, 0x00,0x60,0x60,0x00,0x00,
        0x20,0x10,0x08,0x04,0x02, 0x3E,0x51,0x49,0x45,0x3E, 0x00,0x42,0x7F,0x40,0x00,
        0x42,0x61,0x51,0x49,0x46, 0x21,0x41,0x45,0x4B,0x31, 0x18,0x14,0x12,0x7F,0x10,
        0x27,0x45,0x45,0x45,0x39, 0x3C,0x4A,0x49,0x49,0x30, 0x01,0x71,0x09,0x05,0x03,
        0x36,0x49,0x49,0x49,0x36, 0x06,0x49,0x49,0x29,0x1E, 0x00,0x36,0x36,0x00,0x00,
        0x00,0x56,0x36,0x00,0x00, 0x00,0x08,0x14,0x22,0x41, 0x14,0x14,0x14,0x14,0x14,
        0x41,0x22,0x14,0x08,0x00, 0x02,0x01,0x51,0x09,0x06, 0x32,0x49,0x79,0x41,0x3E,
        0x7E,0x11,0x11,0x11,0x7E, 0x7F,0x49,0x49,0x49,0x36, 0x3E,0x41,0x41,0x41,0x22,
        0x7F,0x41,0x41,0x22,0x1C, 0x7F,0x49,0x49,0x49,0x41, 0x7F,0x09,0x09,0x01,0x01,
        0x3E,0x41,0x41,0x51,0x32, 0x7F,0x08,0x08,0x08,0x7F, 0x00,0x41,0x7F,0x41,0x00,
        0x20,0x40,0x41,0x3F,0x01, 0x7F,0x08,0x14,0x22,0x41, 0x7F,0x40,0x40,0x40,0x40,
        0x7F,0x02,0x04,0x02,0x7F, 0x7F,0x04,0x08,0x10,0x7F, 0x3E,0x41,0x41,0x41,0x3E,
        0x7F,0x09,0x09,0x09,0x06, 0x3E,0x41,0x51,0x21,0x5E, 0x7F,0x09,0x19,0x29,0x46,
        0x46,0x49,0x49,0x49,0x31, 0x01,0x01,0x7F,0x01,0x01, 0x3F,0x40,0x40,0x40,0x3F,
        0x1F,0x20,0x40,0x20,0x1F, 0x7F,0x20,0x18,0x20,0x7F, 0x63,0x14,0x08,0x14,0x63,
        0x03,0x04,0x78,0x04,0x03, 0x61,0x51,0x49,0x45,0x43, 0x00,0x00,0x7F,0x41,0x41,
        0x02,0x04,0x08,0x10,0x20, 0x41,0x41,0x7F,0x00,0x00, 0x04,0x02,0x01,0x02,0x04,
        0x40,0x40,0x40,0x40,0x40,
    };

    public const int W = 5, H = 7, Advance = 6;

    /// <summary>Draw one line, scaled, with a one-pixel shadow so it survives a bright background.</summary>
    public static void Text(Frame frame, int x, int y, string text, int scale,
                            byte b, byte g, byte r)
    {
        foreach (char raw in text)
        {
            char c = char.ToUpperInvariant(raw);
            int code = c < First || c > Last ? '?' : c;
            int off = (code - First) * W;
            for (int col = 0; col < W; col++)
            {
                byte bits = Rom[off + col];
                for (int row = 0; row < H; row++)
                {
                    if ((bits & (1 << row)) == 0)
                        continue;
                    for (int sy = 0; sy < scale; sy++)
                        for (int sx = 0; sx < scale; sx++)
                        {
                            int px = x + col * scale + sx, py = y + row * scale + sy;
                            frame.Set(px + 1, py + 1, 0, 0, 0);
                            frame.Set(px, py, b, g, r);
                        }
                }
            }
            x += Advance * scale;
        }
    }

    public static int Width(string text, int scale) => text.Length * Advance * scale;
}

/// <summary>
/// Every frame of a window, written out with its own numbers drawn on it.
///
/// The decision dumps answer "what did the winning frame look like"; this
/// answers "what did every frame look like, and in what order" -- which is the
/// question a wrong pick actually raises. Frames are annotated and encoded on a
/// background task so trace mode does not change the loop's timing; if the
/// writer falls behind, frames are dropped rather than throttling the loop, and
/// the count of dropped frames goes in the summary so the gap is never silent.
/// </summary>
public static class Trace
{
    private sealed record Item(string Path, Frame Frame, TraceSample Sample);

    private static Channel<Item>? _queue;
    private static Task? _writer;
    private static readonly List<TraceSample> Samples = new();
    private static readonly object Lock = new();
    private static int _written, _dropped;

    public static string? Dir { get; private set; }
    public static bool Active => Dir is not null;

    /// <summary>How many frames one window may sample before it stops.</summary>
    private const int MaxFrames = 1500;
    /// <summary>JPEG quality for the annotated frames.</summary>
    private const int Quality = 82;

    public static void Begin(int? level)
    {
        // Attaching the sink is the switch. There is no setting to leave on by
        // accident because there is no setting: this code is not in the exe.
        End();
        Dir = Path.Combine(Config.State, "trace",
                           $"{DateTime.Now:yyyyMMdd-HHmmss}_lv{level}");
        Directory.CreateDirectory(Dir);
        lock (Lock)
        {
            Samples.Clear();
            _written = 0;
            _dropped = 0;
        }
        _queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(12)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
        _writer = Task.Run(WriteLoopAsync);
    }

    public static void Add(Frame frame, TraceSample sample)
    {
        if (Dir is null || _queue is null)
            return;
        lock (Lock)
        {
            if (Samples.Count >= MaxFrames)
                return;
            Samples.Add(sample);
            string path = Path.Combine(Dir, $"f{_written:D5}.jpg");
            if (_queue.Writer.TryWrite(new Item(path, frame.Clone(), sample)))
                _written++;
            else
                _dropped++;
        }
    }

    /// <summary>Close the trace out, writing the manifest and the verdict beside the frames.</summary>
    public static async Task FinishAsync(IReadOnlyList<string> verdict)
    {
        string? dir = Dir;
        if (dir is null)
            return;
        _queue?.Writer.TryComplete();
        if (_writer is not null)
            await Task.WhenAny(_writer, Task.Delay(TimeSpan.FromSeconds(20)));

        TraceSample[] samples;
        int written, dropped;
        lock (Lock)
        {
            samples = Samples.ToArray();
            written = _written;
            dropped = _dropped;
        }

        var text = new StringBuilder();
        text.AppendLine($"frames sampled  {samples.Length}");
        text.AppendLine($"frames written  {written}");
        text.AppendLine($"frames dropped  {dropped}   (writer behind; timing of the loop was not changed)");
        text.AppendLine();
        text.AppendLine("--- verdict, in the order it was decided ---");
        foreach (string line in verdict)
            text.AppendLine(line);
        text.AppendLine();
        text.AppendLine("--- per frame ---");
        text.AppendLine("  idx      t  gate1 gate2 gate3  hide  alive cards  " +
                        "L:mean/inner/brt   M:mean/inner/brt   R:mean/inner/brt  flare");
        foreach (var s in samples)
        {
            var cells = new List<string>();
            foreach (string slot in new[] { "L", "M", "R" })
            {
                var v = s.Stats.TryGetValue(slot, out var st) ? st : default;
                cells.Add($"{v.Mean,5:F1}/{v.Inner,5:F1}/{v.Bright,5:F1}");
            }
            text.AppendLine(
                $"{s.Index,5} {s.T,6:F2}  {s.Gate[0],5:F2} {s.Gate[1],5:F2} {s.Gate[2],5:F2} " +
                $"{s.Hide,5:F2}  {(s.Alive ? "Y" : "n"),5} {(s.CardsUp ? "Y" : "n"),5}  " +
                $"{string.Join("  ", cells)}  {s.Flare}");
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "trace.txt"), text.ToString());

        // IncludeFields, because TraceSample is fields all the way down and the
        // serializer's default is properties only -- which writes a manifest of
        // the right length full of empty objects, and says nothing about it.
        await File.WriteAllTextAsync(Path.Combine(dir, "trace.json"),
            JsonSerializer.Serialize(new { verdict, written, dropped, samples },
                new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));

        // The app carries no encoder and is not going to grow one for a debug
        // feature. This just hands the frames to ffmpeg if the machine has it.
        await File.WriteAllTextAsync(Path.Combine(dir, "make_video.cmd"),
            "@echo off\r\n" +
            "rem Assemble this trace into trace.mp4. Needs ffmpeg on PATH.\r\n" +
            "ffmpeg -y -framerate 12 -i \"%~dp0f%%05d.jpg\" -c:v libx264 " +
            "-pix_fmt yuv420p -crf 20 \"%~dp0trace.mp4\"\r\n");

        Dir = null;
        _queue = null;
        _writer = null;
    }

    public static void End()
    {
        _queue?.Writer.TryComplete();
        Dir = null;
        _queue = null;
        _writer = null;
    }

    private static async Task WriteLoopAsync()
    {
        var queue = _queue;
        if (queue is null)
            return;
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync())
            {
                try
                {
                    Annotate(item.Frame, item.Sample);
                    await Imaging.SaveJpegAsync(item.Frame, item.Path, Quality);
                }
                catch
                {
                    // A trace that cannot write a frame is still worth the frames
                    // it did write; the count in the summary shows the shortfall.
                }
            }
        }
        catch (ChannelClosedException)
        {
        }
    }

    /// <summary>Draw the boxes the loop reads and the numbers it read out of them.</summary>
    private static void Annotate(Frame frame, TraceSample s)
    {
        double sx = (double)frame.Width / Config.BaseW, sy = (double)frame.Height / Config.BaseH;
        int scale = frame.Width >= 1600 ? 3 : 2;

        // Card boxes: green when the reroll gate says cards are up, grey when not.
        foreach (var (slot, box) in Config.Cards)
        {
            bool win = s.Flare.StartsWith(slot + " ", StringComparison.Ordinal);
            byte b = 130, g = 130, r = 130;
            if (win) { b = 60; g = 240; r = 255; }
            else if (s.CardsUp) { b = 90; g = 200; r = 90; }
            frame.DrawBox(box, b, g, r, win ? 5 : 2);
            frame.DrawBox(Config.CardInteriors[slot], 200, 160, 60, 1);
        }
        frame.DrawBox(Config.HoverTooltip, 60, 170, 255, 2);
        frame.DrawBox(Config.HideBox, 200, 80, 200, 2);
        foreach (var (bx, by) in Config.RerollBoxes)
            frame.DrawRaw((int)(bx * sx), (int)(by * sy),
                          (int)((bx + Config.RerollSize.W) * sx),
                          (int)((by + Config.RerollSize.H) * sy), 220, 220, 80, 1);

        var lines = new List<string>
        {
            $"FR {s.Index}  T {s.T:F2}S",
            $"GATE {s.Gate[0]:F2} {s.Gate[1]:F2} {s.Gate[2]:F2}  HIDE {s.Hide:F2}",
            $"ALIVE {(s.Alive ? "Y" : "N")}  CARDS {(s.CardsUp ? "Y" : "N")}" +
            $"  SETTLED {(s.Settled ? "Y" : "N")}",
            "     MEAN  INNER   BRT%",
        };
        foreach (string slot in new[] { "L", "M", "R" })
        {
            var v = s.Stats.TryGetValue(slot, out var st) ? st : default;
            lines.Add($" {slot}  {v.Mean,6:F1} {v.Inner,6:F1} {v.Bright,6:F1}");
        }
        lines.Add(s.Flare.Length > 0 ? $"FLARE {s.Flare}" : "FLARE -");
        lines.Add(s.TooltipSlot is null
            ? "TIP -"
            : $"TIP {s.TooltipSlot} '{Clip(s.TooltipRaw, 18)}' {s.TooltipAge:F1}S");
        foreach (string slot in new[] { "L", "M", "R" })
            if (s.Titles.TryGetValue(slot, out string? title) && title.Length > 0)
                lines.Add($" {slot}= {Clip(title, 20)}");

        // What each box on the picture is. A frame full of coloured rectangles
        // is not self-explanatory a week later, and the legend costs six lines.
        var key = new (byte B, byte G, byte R, string Label)[]
        {
            (90, 200, 90, "CARD L/M/R - GATE SEES CARDS"),
            (130, 130, 130, "CARD - GATE DOES NOT"),
            (60, 240, 255, "CARD TAKEN BY THE FLARE"),
            (200, 160, 60, "INNER BOX - FLARE READS THIS"),
            (60, 170, 255, "TOOLTIP OCR STRIP"),
            (220, 220, 80, "REROLL BUTTON GATE"),
            (200, 80, 200, "HIDE BUTTON"),
        };

        int pad = 6 * scale;
        int lineH = (Glyphs.H + 2) * scale;
        int keyScale = Math.Max(1, scale - 1);
        int keyH = (Glyphs.H + 3) * keyScale;
        int wide = 0;
        foreach (string line in lines)
            wide = Math.Max(wide, Glyphs.Width(line, scale));
        int swatch = Glyphs.H * keyScale;
        foreach (var (_, _, _, label) in key)
            wide = Math.Max(wide, swatch + 4 * keyScale + Glyphs.Width(label, keyScale));

        int x0 = pad, y0 = pad;
        int body = lines.Count * lineH;
        frame.FillBox(x0 - pad / 2, y0 - pad / 2, x0 + wide + pad,
                      y0 + body + key.Length * keyH + pad + keyH, 0, 0, 0, 0.62);
        for (int i = 0; i < lines.Count; i++)
            Glyphs.Text(frame, x0, y0 + i * lineH, lines[i], scale, 230, 240, 240);

        int ky = y0 + body + keyH / 2;
        for (int i = 0; i < key.Length; i++)
        {
            var (b, g, r, label) = key[i];
            int top = ky + i * keyH;
            frame.FillBox(x0, top, x0 + swatch, top + swatch, b, g, r, 1.0);
            Glyphs.Text(frame, x0 + swatch + 4 * keyScale, top, label, keyScale, 165, 178, 188);
        }
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max);
}

/// <summary>
/// Hangs <see cref="Trace"/> off the loop's observation hook.
///
/// The trace was written as a static because it was a mode the app could be
/// put into; it is a tool that gets attached now, and the thin instance is all
/// that difference amounts to.
/// </summary>
public sealed class TraceSink : ILoopObserver
{
    public bool WantsFrames => true;

    public void BeginWindow(int? level) => Trace.Begin(level);
    public void Frame(Frame frame, TraceSample sample) => Trace.Add(frame, sample);
    public Task FinishAsync(IReadOnlyList<string> verdict) => Trace.FinishAsync(verdict);
    public void EndWindow() => Trace.End();
}
