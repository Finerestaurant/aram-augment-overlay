using System.Text.Json.Nodes;

namespace AramOverlay.Core;

/// <summary>
/// Startup: load the data, connect to OBS, serve the widget, run the loop.
///
/// Kept apart from any UI so the same sequence runs from a console and from the
/// status window, and so a failure to connect surfaces as a returned code with
/// its reason already in the log rather than an exception crossing a thread.
/// </summary>
public sealed class OverlayHost : IAsyncDisposable
{
    private ObsCapture? _obs;
    private ObsCapture? _probeObs;
    private WidgetServer? _server;

    public RunState State { get; } = new();
    public string? Url { get; private set; }

    public async Task<int> RunAsync(CancellationToken token)
    {
        Log.Write(Strings.Get("Core.LoadingAugments"));
        var db = await AugmentDb.LoadAsync();
        Log.Write(Strings.Get("Core.AugmentCount", db.Augments.Count));
        if (db.MayhemPool.Count > 0)
            Log.Write(Strings.Get("Core.MayhemPool", db.MayhemPool.Count));
        var items = await ItemNames.LoadAsync();

        string password = Config.ObsPassword;
        if (password.Length == 0)
        {
            var cfg = ObsCapture.ReadWebsocketConfig();
            password = cfg?["server_password"]?.GetValue<string>() ?? "";
            if (password.Length > 0)
                Log.Write(Strings.Get("Core.PasswordFromConfig"));
        }

        try
        {
            _obs = await ObsCapture.ConnectAsync(password: password);
            Log.Write(await _obs.VersionAsync());
        }
        catch (Exception exc)
        {
            Log.Write(Strings.Get("Core.ObsConnectFailed", exc.Message));
            Log.Write(Strings.Get("Core.ObsCheckHint"));
            return 2;
        }

        if (await _obs.EnsureGameCaptureAsync())
            Log.Write(Strings.Get("Core.GameCaptureCreated", _obs.Source));
        else if (!await _obs.HasSourceAsync())
        {
            Log.Write(Strings.Get("Core.SourceMissing", _obs.Source));
            return 2;
        }
        else
        {
            // A source from an earlier build points at a window OBS cannot
            // find; fix it here rather than wait for the first black frame.
            try
            {
                if (await _obs.RepairGameCaptureAsync() is { } window)
                    Log.Write(Strings.Get("Core.GameCaptureRepaired", _obs.Source, window));
            }
            catch
            {
                // A source that will not answer about its window is still a
                // source; the loop reports black frames if it comes to that.
            }
        }

        Log.Write(Strings.Get("Core.OcrPreparing"));
        var ocr = TooltipOcr.TryCreate();
        if (ocr is null)
        {
            Log.Write(TooltipOcr.MissingLanguageMessage());
            return 2;
        }

        var gate = await Assets.GateAsync();
        var hide = await Assets.HideButtonAsync();

        _server = new WidgetServer(State, Assets.Text("widget.html"));
        Url = _server.Start();
        Log.Write(Strings.Get("Core.WidgetUrl", Url));
        var refreshed = await _obs.RefreshBrowserSourcesAsync(Url);
        if (refreshed.Count > 0)
            Log.Write(Strings.Get("Core.BrowserRefreshed", string.Join(", ", refreshed)));

        // The tooltip grab costs ~250 ms and the selection is only a few frames
        // long, so it runs off the detection loop on its own connection.
        _probeObs = await ObsCapture.ConnectAsync(password: password);

        var loop = new OverlayLoop(db, items, ocr, gate, hide, _obs, _probeObs, _server);
        try
        {
            await loop.RunAsync(token);
        }
        catch (OperationCanceledException)
        {
            Log.Write(Strings.Get("Core.Quitting"));
        }
        finally
        {
            _server.Save();
        }
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        _server?.Dispose();
        if (_obs is not null)
            await _obs.DisposeAsync();
        if (_probeObs is not null)
            await _probeObs.DisposeAsync();
    }
}
