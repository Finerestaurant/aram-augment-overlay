using System.Text.Json;
using System.Text.Json.Nodes;

namespace AramOverlay.Core;

/// <summary>Everything this tool asks OBS to do.</summary>
public sealed class ObsCapture : IAsyncDisposable
{
    private readonly ObsClient _client = new();

    public string Source { get; }

    private ObsCapture(string source) => Source = source;

    public static async Task<ObsCapture> ConnectAsync(string? host = null, int? port = null,
                                                      string? password = null, string? source = null)
    {
        var capture = new ObsCapture(source ?? Config.ObsSource);
        await capture._client.ConnectAsync(host ?? Config.ObsHost, port ?? Config.ObsPort,
                                           password ?? Config.ObsPassword);
        return capture;
    }

    /// <summary>The password OBS wrote into its own plugin config.</summary>
    public static JsonNode? ReadWebsocketConfig()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "obs-studio", "plugin_config", "obs-websocket", "config.json");
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> VersionAsync()
    {
        var v = await _client.RequestAsync("GetVersion");
        return $"OBS {v?["obsVersion"]?.GetValue<string>()} / " +
               $"websocket {v?["obsWebSocketVersion"]?.GetValue<string>()}";
    }

    private async Task<JsonArray> InputsAsync()
    {
        var list = await _client.RequestAsync("GetInputList");
        return list?["inputs"]?.AsArray() ?? new JsonArray();
    }

    public async Task<bool> HasSourceAsync() =>
        (await InputsAsync()).Any(i => i?["inputName"]?.GetValue<string>() == Source);

    /// <summary>
    /// The game window as OBS itself lists it: title, class and executable.
    /// Riot's title and class do not change with the client language, and
    /// "priority 2" below matches on the executable anyway.
    ///
    /// An earlier build wrote only "::League of Legends.exe" and OBS never
    /// hooked it -- its own window list showed that entry disabled, the game
    /// capture never logged an attempt, and GetSourceScreenshot handed back a
    /// black frame at any asked-for size instead of an error. Two games' worth
    /// of picks were missed by a tool that reported itself as watching. The
    /// exact triple OBS offers hooks within two seconds.
    /// </summary>
    public const string GameWindow =
        "League of Legends (TM) Client:RiotWindowClass:League of Legends.exe";
    private const string GameExe = "League of Legends.exe";
    private const string LegacyGameWindow = "::" + GameExe;

    /// <summary>
    /// The source's own size in pixels, read off its scene item rather than a
    /// picture -- a few bytes instead of a screenshot. Null when the source is
    /// not an item of the current program scene, in which case the loop falls
    /// back to the 16:9 assumption.
    /// </summary>
    public async Task<(int W, int H)?> SourceSizeAsync()
    {
        try
        {
            var scene = await _client.RequestAsync("GetCurrentProgramScene");
            string name = scene?["sceneName"]?.GetValue<string>()
                          ?? scene?["currentProgramSceneName"]?.GetValue<string>() ?? "";
            var id = await _client.RequestAsync("GetSceneItemId",
                new JsonObject { ["sceneName"] = name, ["sourceName"] = Source });
            int itemId = (int)(id?["sceneItemId"]?.GetValue<double>() ?? -1);
            if (itemId < 0)
                return null;
            var t = await _client.RequestAsync("GetSceneItemTransform",
                new JsonObject { ["sceneName"] = name, ["sceneItemId"] = itemId });
            var tr = t?["sceneItemTransform"];
            int w = (int)Math.Round(tr?["sourceWidth"]?.GetValue<double>() ?? 0);
            int h = (int)Math.Round(tr?["sourceHeight"]?.GetValue<double>() ?? 0);
            return w > 0 && h > 0 ? (w, h) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Create the Game Capture source if it is missing. True if created.</summary>
    public async Task<bool> EnsureGameCaptureAsync()
    {
        if (await HasSourceAsync())
            return false;
        var scenes = await _client.RequestAsync("GetSceneList");
        string scene = scenes?["currentProgramSceneName"]?.GetValue<string>() ?? "";
        await _client.RequestAsync("CreateInput", new JsonObject
        {
            ["sceneName"] = scene,
            ["inputName"] = Source,
            ["inputKind"] = "game_capture",
            ["sceneItemEnabled"] = true,
            ["inputSettings"] = new JsonObject
            {
                ["capture_mode"] = "window",
                ["window"] = GameWindow,
                // Match by executable, so borderless or windowed both hook.
                ["priority"] = 2,
                ["capture_cursor"] = true,
                ["anti_cheat_hook"] = true,
            },
        });
        return true;
    }

    /// <summary>
    /// Point an existing game capture at a window OBS can actually find.
    ///
    /// Preferred is whatever OBS lists for the game right now, since that is
    /// by definition findable; with no game running only the known-bad legacy
    /// value is replaced, by the canonical triple. A source the user has put
    /// into another capture mode is left alone. Returns the window it was
    /// switched to, or null when nothing was changed.
    /// </summary>
    public async Task<string?> RepairGameCaptureAsync()
    {
        var got = await _client.RequestAsync("GetInputSettings",
            new JsonObject { ["inputName"] = Source });
        if (got?["inputKind"]?.GetValue<string>() != "game_capture")
            return null;
        var settings = got["inputSettings"];
        string window = settings?["window"]?.GetValue<string>() ?? "";
        string mode = settings?["capture_mode"]?.GetValue<string>() ?? "window";
        if (mode != "window" && window != LegacyGameWindow)
            return null;

        string? want = await ListedGameWindowAsync()
                       ?? (window == LegacyGameWindow || window.Length == 0 ? GameWindow : null);
        if (want is null || want == window)
            return null;
        await _client.RequestAsync("SetInputSettings", new JsonObject
        {
            ["inputName"] = Source,
            ["inputSettings"] = new JsonObject
            {
                ["capture_mode"] = "window",
                ["window"] = want,
                ["priority"] = 2,
            },
        });
        return want;
    }

    /// <summary>The game's entry in the window list OBS offers the source, if the game is up.</summary>
    private async Task<string?> ListedGameWindowAsync()
    {
        var list = await _client.RequestAsync("GetInputPropertiesListPropertyItems",
            new JsonObject { ["inputName"] = Source, ["propertyName"] = "window" });
        foreach (var item in list?["propertyItems"]?.AsArray() ?? new JsonArray())
        {
            string value = item?["itemValue"]?.GetValue<string>() ?? "";
            if (item?["itemEnabled"]?.GetValue<bool>() == true &&
                !value.StartsWith("::", StringComparison.Ordinal) &&
                value.EndsWith(":" + GameExe, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }

    /// <summary>
    /// Reload every browser source pointing at <paramref name="url"/>.
    ///
    /// CEF does not retry a load that failed, so a browser source that was
    /// showing the widget while this tool was stopped stays blank for good once
    /// it reloads against a dead port. Startup is exactly when that has happened.
    /// </summary>
    public Task<List<string>> RefreshBrowserSourcesAsync(string url) =>
        ForEachBrowserSourceAsync(url, async name =>
        {
            await _client.RequestAsync("PressInputPropertiesButton", new JsonObject
            {
                ["inputName"] = name,
                ["propertyName"] = "refreshnocache",
            });
            return true;
        });

    /// <summary>
    /// Set every browser source showing <paramref name="url"/> to this size, so
    /// the outline in OBS matches what the page actually draws.
    /// </summary>
    public Task<List<string>> ResizeBrowserSourcesAsync(string url, int width, int height) =>
        ForEachBrowserSourceAsync(url, async name =>
        {
            var current = await SettingsOfAsync(name);
            if (current?["width"]?.GetValue<int>() == width &&
                current?["height"]?.GetValue<int>() == height)
                return false;
            await _client.RequestAsync("SetInputSettings", new JsonObject
            {
                ["inputName"] = name,
                ["overlay"] = true,
                ["inputSettings"] = new JsonObject { ["width"] = width, ["height"] = height },
            });
            return true;
        });

    private async Task<JsonNode?> SettingsOfAsync(string name)
    {
        var settings = await _client.RequestAsync("GetInputSettings",
            new JsonObject { ["inputName"] = name });
        return settings?["inputSettings"];
    }

    private async Task<List<string>> ForEachBrowserSourceAsync(string url,
                                                               Func<string, Task<bool>> action)
    {
        string want = url.TrimEnd('/');
        var done = new List<string>();
        JsonArray inputs;
        try
        {
            inputs = await InputsAsync();
        }
        catch
        {
            return done;
        }

        foreach (var input in inputs)
        {
            if (input?["inputKind"]?.GetValue<string>() != "browser_source")
                continue;
            string name = input["inputName"]!.GetValue<string>();
            try
            {
                var settings = await SettingsOfAsync(name);
                if ((settings?["url"]?.GetValue<string>() ?? "").TrimEnd('/') != want)
                    continue;
                if (await action(name))
                    done.Add(name);
            }
            catch
            {
                // A source that will not answer is skipped, not fatal.
            }
        }
        return done;
    }

    /// <summary>
    /// One frame from the game capture source, or null while the game is not
    /// hooked: the source then has zero size and obs-websocket answers with an
    /// error rather than handing back a black frame, so "not attached yet" stays
    /// distinguishable from "attached but black".
    /// </summary>
    public Task<Frame?> GrabAsync(int? width = null, int? height = null,
                                  int? quality = null, string format = "jpg") =>
        ShotAsync(new JsonObject
        {
            ["sourceName"] = Source,
            ["imageFormat"] = format,
            ["imageWidth"] = width ?? Config.DetW,
            ["imageHeight"] = height ?? Config.DetH,
            ["imageCompressionQuality"] = quality ?? Config.DetQuality,
        });

    /// <summary>
    /// The source at its own size -- no width or height asked for -- which is
    /// the one way to learn what the game is actually rendering at.
    /// </summary>
    public Task<Frame?> GrabNativeAsync(int? quality = null, string format = "jpg") =>
        ShotAsync(new JsonObject
        {
            ["sourceName"] = Source,
            ["imageFormat"] = format,
            ["imageCompressionQuality"] = quality ?? Config.OcrQuality,
        });

    private async Task<Frame?> ShotAsync(JsonObject request)
    {
        JsonNode? shot;
        try
        {
            shot = await _client.RequestAsync("GetSourceScreenshot", request);
        }
        catch
        {
            return null;
        }

        string data = shot?["imageData"]?.GetValue<string>() ?? "";
        if (data.Length == 0)
            return null;
        int comma = data.IndexOf(',');
        if (data.StartsWith("data:") && comma >= 0)
            data = data[(comma + 1)..];
        try
        {
            return await Imaging.DecodeAsync(Convert.FromBase64String(data));
        }
        catch
        {
            return null;
        }
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
