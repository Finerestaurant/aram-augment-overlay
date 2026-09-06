using AramOverlay.Core;

namespace AramOverlay.App;

/// <summary>
/// Owns the detection loop's lifetime for the window.
///
/// Settings apply by restarting rather than being live-patched: the language
/// decides which data file is loaded and the resolution decides every box
/// coordinate, and both are read once during startup.
/// </summary>
public sealed class OverlayRunner
{
    private readonly object _gate = new();
    private OverlayHost? _host;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public RunState State { get; private set; } = new();
    public string? Url { get; private set; }
    public bool IsRunning => _task is { IsCompleted: false };

    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning)
                return;
            Settings.Load().Apply();
            var host = new OverlayHost();
            var cts = new CancellationTokenSource();
            _host = host;
            _cts = cts;
            State = host.State;
            Url = null;

            _task = Task.Run(async () =>
            {
                try
                {
                    int code = await host.RunAsync(cts.Token);
                    if (code != 0)
                        Log.Write("오버레이가 시작되지 못했습니다.");
                }
                catch (OperationCanceledException)
                {
                    // Asked to stop.
                }
                catch (Exception exc)
                {
                    Log.Write($"오류로 중단됐습니다: {exc.Message}");
                }
                finally
                {
                    await host.DisposeAsync();
                }
            });

            // The URL only exists once the server is up; poll briefly rather
            // than threading a callback through the host for one string.
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 100 && Url is null; i++)
                {
                    await Task.Delay(200);
                    Url = host.Url;
                }
            });
        }
    }

    public void Stop()
    {
        Task? task;
        lock (_gate)
        {
            _cts?.Cancel();
            task = _task;
        }
        try
        {
            task?.Wait(TimeSpan.FromSeconds(6));
        }
        catch
        {
            // A loop that will not come back must not hold up shutdown.
        }
        lock (_gate)
        {
            _task = null;
            _host = null;
            Url = null;
        }
    }

    public void Restart()
    {
        Task.Run(() =>
        {
            Stop();
            Start();
        });
    }

    public void ResetPicks()
    {
        var state = State;
        lock (state)
            state.Picks.Clear();
    }
}
