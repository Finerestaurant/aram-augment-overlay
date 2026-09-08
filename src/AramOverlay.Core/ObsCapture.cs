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
                ["window"] = "::League of Legends.exe",
                // Match by executable, so borderless or windowed both hook.
                ["priority"] = 2,
                ["capture_cursor"] = true,
                ["anti_cheat_hook"] = true,
            },
        });
        return true;
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
    public async Task<Frame?> GrabAsync(int? width = null, int? height = null,
                                        int? quality = null, string format = "jpg")
    {
        JsonNode? shot;
        try
        {
            shot = await _client.RequestAsync("GetSourceScreenshot", new JsonObject
            {
                ["sourceName"] = Source,
                ["imageFormat"] = format,
                ["imageWidth"] = width ?? Config.DetW,
                ["imageHeight"] = height ?? Config.DetH,
                ["imageCompressionQuality"] = quality ?? Config.DetQuality,
            });
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
