using System.Net;
using System.Text;
using System.Text.Json;

namespace AramOverlay.Core;

/// <summary>
/// Local HTTP server for the decision inspector.
///
/// Deliberately not the widget server. That one's URL is typed into an OBS
/// browser source and whatever it serves can end up on a stream; this one
/// serves raw game frames and the reasoning behind every pick, which belongs on
/// the developer's screen and nowhere else. Separate port, separate page, and
/// bound to loopback like the widget is.
/// </summary>
public sealed class InspectorServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpListener _listener = new();
    private readonly string _page;
    private CancellationTokenSource? _cts;

    public string Host { get; }
    public int Port { get; }
    public string Url => $"http://{Host}:{Port}/";

    public InspectorServer(string page, string? host = null, int? port = null)
    {
        _page = page;
        Host = host ?? Config.InspectHost;
        Port = port ?? Config.InspectPort;
    }

    /// <summary>
    /// Starts the server, or returns null if the port will not bind.
    ///
    /// A debug page failing to come up must not take the run down with it: the
    /// overlay's job is to record augments, and it can do that with no
    /// inspector at all.
    /// </summary>
    public string? Start()
    {
        try
        {
            _listener.Prefixes.Add($"http://{Host}:{Port}/");
            _listener.Start();
        }
        catch (Exception exc)
        {
            Log.Write(Strings.Get("Core.InspectFailed", exc.Message));
            return null;
        }
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
            if (ctx.Request.HttpMethod == "POST" && path.StartsWith("/clear"))
            {
                Inspector.Clear();
                Send(ctx, "{\"ok\":true}"u8.ToArray(), "application/json");
                return;
            }
            if (ctx.Request.HttpMethod != "GET")
            {
                ctx.Response.StatusCode = 405;
                ctx.Response.Close();
                return;
            }

            if (path is "/" or "/index.html")
            {
                Send(ctx, Encoding.UTF8.GetBytes(_page), "text/html; charset=utf-8");
            }
            // Just the counter, so the page can poll often and only fetch the
            // windows when one has actually changed. The frames are the
            // expensive part and they never change once written.
            else if (path.StartsWith("/version"))
            {
                Send(ctx, Encoding.UTF8.GetBytes($"{{\"version\":{Inspector.Version}}}"),
                     "application/json; charset=utf-8");
            }
            else if (path.StartsWith("/windows.json"))
            {
                // The thresholds travel with the data rather than being written
                // into the page. A chart that draws 3.0 from its own source is
                // a chart that keeps saying 3.0 after Config stops meaning it.
                var body = new
                {
                    version = Inspector.Version,
                    limits = new
                    {
                        ratio = Config.FlareInnerRatio,
                        rise = Config.FlareRise,
                        hover = Config.SelectFlare,
                        window_s = Config.FlareWindowS,
                        window_after_s = Config.FlareWindowAfterS,
                        lookback_s = Config.FlareLookbackS,
                        baseline_from_s = Config.FlareBaselineFromS,
                        baseline_to_s = Config.FlareBaselineToS,
                    },
                    windows = Inspector.Snapshot(),
                };
                Send(ctx, JsonSerializer.SerializeToUtf8Bytes(body, Json),
                     "application/json; charset=utf-8");
            }
            else if (path.StartsWith("/frame/"))
            {
                string id = path["/frame/".Length..];
                int dot = id.LastIndexOf('.');
                if (dot >= 0)
                    id = id[..dot];
                var bytes = Inspector.Frame(id);
                if (bytes is null || bytes.Length == 0)
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }
                // The bytes are whatever OBS sent, and the probe asks for PNG
                // while the detection loop asks for JPEG. Sniff rather than
                // track it: two magic numbers are cheaper than a format field
                // threaded through every frame.
                bool png = bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50;
                Send(ctx, bytes, png ? "image/png" : "image/jpeg");
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private static void Send(HttpListenerContext ctx, byte[] body, string contentType)
    {
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = body.Length;
        // The page polls and the frames never change, but a stale window list
        // would show a pick that has already been superseded.
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.OutputStream.Write(body, 0, body.Length);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener.Close(); } catch { }
    }
}
