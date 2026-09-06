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
        Log.Write("증강 데이터 불러오는 중...");
        var db = await AugmentDb.LoadAsync();
        Log.Write($"  증강 {db.Augments.Count}종");
        var items = await ItemNames.LoadAsync();

        string password = Config.ObsPassword;
        if (password.Length == 0)
        {
            var cfg = ObsCapture.ReadWebsocketConfig();
            password = cfg?["server_password"]?.GetValue<string>() ?? "";
            if (password.Length > 0)
                Log.Write("OBS 설정 파일에서 websocket 비밀번호를 읽었습니다.");
        }

        try
        {
            _obs = await ObsCapture.ConnectAsync(password: password);
            Log.Write(await _obs.VersionAsync());
        }
        catch (Exception exc)
        {
            Log.Write($"OBS 연결 실패: {exc.Message}");
            Log.Write("  OBS가 실행 중인지, 도구 > WebSocket 서버 설정에서 서버가 켜져 있는지 확인하세요.");
            return 2;
        }

        if (await _obs.EnsureGameCaptureAsync())
            Log.Write($"게임 캡처 소스 '{_obs.Source}' 를 새로 만들었습니다.");
        else if (!await _obs.HasSourceAsync())
        {
            Log.Write($"소스 '{_obs.Source}' 를 찾을 수 없습니다.");
            return 2;
        }

        Log.Write("OCR 준비 중...");
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
        Log.Write($"위젯 주소: {Url}   <- OBS 브라우저 소스에 이 주소를 넣으세요 " +
                  "(크기는 내용에 맞춰 자동 조정됩니다)");
        var refreshed = await _obs.RefreshBrowserSourcesAsync(Url);
        if (refreshed.Count > 0)
            Log.Write($"브라우저 소스 새로고침: {string.Join(", ", refreshed)}");

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
            Log.Write("종료합니다.");
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
