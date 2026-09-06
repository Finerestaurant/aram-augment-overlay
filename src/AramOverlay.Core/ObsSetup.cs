using System.Text.Json;
using System.Text.Json.Nodes;

namespace AramOverlay.Core;

/// <summary>
/// Turning on OBS's websocket server without clicking through OBS.
///
/// The CLI flags --websocket_port / --websocket_password only override values;
/// they never switch the server on. `server_enabled` in obs-websocket's own
/// config.json is the only thing that does, and OBS must be closed while it is
/// edited or it overwrites the file on exit.
///
/// One step cannot be automated: the first-run Auto-Configuration Wizard is
/// modal and blocks OBS from writing its config at all, so OBS has to be started
/// and that dialog dismissed once by hand before any of this exists.
/// </summary>
public static class ObsSetup
{
    public enum Result { Enabled, AlreadyOn, NoConfig, ObsRunning, Failed }

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "obs-studio", "plugin_config", "obs-websocket", "config.json");

    public static bool ObsIsRunning() =>
        System.Diagnostics.Process.GetProcessesByName("obs64").Length > 0 ||
        System.Diagnostics.Process.GetProcessesByName("obs32").Length > 0;

    public static (Result Result, string Message) Enable(int port = 4455)
    {
        if (!File.Exists(ConfigPath))
            return (Result.NoConfig, Strings.Get("ObsSetup.NoConfig"));

        if (ObsIsRunning())
            return (Result.ObsRunning, Strings.Get("ObsSetup.ObsRunning"));

        try
        {
            var config = JsonNode.Parse(File.ReadAllText(ConfigPath))?.AsObject();
            if (config is null)
                return (Result.Failed, Strings.Get("ObsSetup.ReadFailed"));

            bool wasOn = config["server_enabled"]?.GetValue<bool>() == true;
            string password = config["server_password"]?.GetValue<string>() ?? "";
            if (wasOn && config["server_port"]?.GetValue<int>() == port)
                return (Result.AlreadyOn, Strings.Get("ObsSetup.AlreadyOn", port));

            config["server_enabled"] = true;
            config["server_port"] = port;
            File.WriteAllText(ConfigPath,
                config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            return (Result.Enabled, Strings.Get("ObsSetup.Enabled", port, password));
        }
        catch (Exception exc)
        {
            return (Result.Failed, Strings.Get("ObsSetup.WriteFailed", exc.Message));
        }
    }
}
