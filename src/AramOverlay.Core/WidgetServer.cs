using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AramOverlay.Core;

public sealed class Pick
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("rarity")] public string Rarity { get; set; } = "";
    [JsonPropertyName("icon_url")] public string IconUrl { get; set; } = "";
    [JsonPropertyName("level")] public int? Level { get; set; }
    [JsonPropertyName("slot")] public string Slot { get; set; } = "";
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("ocr_raw")] public string OcrRaw { get; set; } = "";

    /// <summary>How the card was picked out -- brightness flare or tooltip.
    /// Kept with the pick so a wrong one can be explained after the fact,
    /// even when the log has scrolled or a new game cleared it.</summary>
    [JsonPropertyName("via")] public string Via { get; set; } = "";

    [JsonPropertyName("others")] public string Others { get; set; } = "";

    /// <summary>What the tooltip said, even when the flare decided. The two
    /// disagreeing is the signature of a wrong pick.</summary>
    [JsonPropertyName("tooltip")] public string Tooltip { get; set; } = "";
}

public sealed class RunState
{
    [JsonPropertyName("picks")] public List<Pick> Picks { get; set; } = new();
    [JsonPropertyName("game_mode")] public string GameMode { get; set; } = "";
    [JsonPropertyName("connected")] public bool Connected { get; set; }
    [JsonPropertyName("level")] public int? Level { get; set; }
}

/// <summary>
/// Local HTTP server for the OBS browser source.
///
/// Keeps the picked-augment list in memory, writes it to disk so a crash
/// mid-game does not lose the run, and serves both the widget page and its
/// state. Row count and width cap are handed to the page rather than duplicated
/// in its script.
/// </summary>
public sealed class WidgetServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpListener _listener = new();
    private readonly string _pageTemplate;
    private CancellationTokenSource? _cts;

    public RunState State { get; }
    public string Host { get; }
    public int Port { get; }
    public string Url => $"http://{Host}:{Port}/";

    /// <summary>What the page says it renders as, so the OBS source can follow.</summary>
    public (int W, int H, int Seq) Size { get; private set; }

    public WidgetServer(RunState state, string pageTemplate, string? host = null, int? port = null)
    {
        State = state;
        _pageTemplate = pageTemplate;
        Host = host ?? Config.WidgetHost;
        Port = port ?? Config.WidgetPort;
    }

    public string Start()
    {
        _listener.Prefixes.Add($"http://{Host}:{Port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Url;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch when (token.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            _ = Task.Run(() => Handle(ctx), token);
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (ctx.Request.HttpMethod == "GET")
            {
                if (path.StartsWith("/state.json"))
                    Send(ctx, StateJson(), "application/json; charset=utf-8");
                else if (path is "/" or "/index.html" or "/widget.html")
                    Send(ctx, Encoding.UTF8.GetBytes(Page()), "text/html; charset=utf-8");
                else
                    ctx.Response.StatusCode = 404;
            }
            else if (ctx.Request.HttpMethod == "POST")
            {
                if (path.StartsWith("/size"))
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = JsonDocument.Parse(reader.ReadToEnd());
                    int w = body.RootElement.GetProperty("w").GetInt32();
                    int h = body.RootElement.GetProperty("h").GetInt32();
                    if ((w, h) != (Size.W, Size.H))
                        Size = (w, h, Size.Seq + 1);
                    Send(ctx, "{\"ok\":true}"u8.ToArray(), "application/json");
                }
                else if (path.StartsWith("/reset"))
                {
                    // Reached from the widget's own button, which OBS exposes
                    // through the source's Interact window. A new game clears the
                    // list on its own; this is for a remake, a restart mid-game,
                    // or a stray false positive.
                    lock (State) State.Picks.Clear();
                    Save();
                    Send(ctx, StateJson(), "application/json; charset=utf-8");
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                }
            }
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; } catch { /* client gone */ }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* client gone */ }
        }
    }

    private string Page()
    {
        string cfg = JsonSerializer.Serialize(new
        {
            rows = Config.WidgetRows,
            maxWidth = Config.WidgetMaxW,
            text = new
            {
                silver = Strings.Get("Rarity.silver"),
                gold = Strings.Get("Rarity.gold"),
                prismatic = Strings.Get("Rarity.prismatic"),
                unknown = Strings.Get("Rarity.unknown"),
                // "{0}" is the rarity and "{1}" the level, so a language that
                // puts the level first still reads correctly.
                withLevel = Strings.Get("Rarity.WithLevel", "{0}", "{1}"),
                reset = Strings.Get("Widget.Reset"),
                resetTitle = Strings.Get("Widget.ResetTitle"),
            },
        }, Json);
        return _pageTemplate.Replace("<!--CONFIG-->", $"<script>window.__CFG__ = {cfg};</script>");
    }

    private byte[] StateJson()
    {
        lock (State)
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(State, Json));
    }

    private static void Send(HttpListenerContext ctx, byte[] body, string contentType)
    {
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.OutputStream.Write(body, 0, body.Length);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Config.State);
            File.WriteAllBytes(Path.Combine(Config.State, "run.json"), StateJson());
        }
        catch
        {
            // Losing the crash-recovery copy must not take the run down with it.
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { /* already down */ }
        _listener.Close();
        _cts?.Dispose();
    }
}
