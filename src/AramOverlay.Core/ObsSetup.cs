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
            return (Result.NoConfig,
                    "OBS 설정 파일이 없습니다.\n" +
                    "OBS를 한 번 실행하고 '자동 구성 마법사'를 닫은 뒤 다시 시도하세요.\n" +
                    "(마법사가 떠 있는 동안에는 OBS가 설정 파일을 저장하지 않습니다.)");

        if (ObsIsRunning())
            return (Result.ObsRunning,
                    "OBS가 실행 중입니다. 종료할 때 설정 파일을 덮어쓰므로 먼저 OBS를 닫아주세요.");

        try
        {
            var config = JsonNode.Parse(File.ReadAllText(ConfigPath))?.AsObject();
            if (config is null)
                return (Result.Failed, "OBS 설정 파일을 읽지 못했습니다.");

            bool wasOn = config["server_enabled"]?.GetValue<bool>() == true;
            string password = config["server_password"]?.GetValue<string>() ?? "";
            if (wasOn && config["server_port"]?.GetValue<int>() == port)
                return (Result.AlreadyOn, $"이미 켜져 있습니다 (포트 {port}).");

            config["server_enabled"] = true;
            config["server_port"] = port;
            File.WriteAllText(ConfigPath,
                config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            return (Result.Enabled,
                    $"websocket 서버를 켰습니다 (포트 {port}, 비밀번호 {password}).\n" +
                    "OBS를 실행한 뒤 다시 시도를 누르세요.\n" +
                    "[주의] OBS가 비정상 종료되면 다음 실행 때 '안전 모드'를 묻습니다. " +
                    "안전 모드는 websocket을 끄므로 반드시 '일반 모드로 실행'을 고르세요.");
        }
        catch (Exception exc)
        {
            return (Result.Failed, $"설정 파일을 쓰지 못했습니다: {exc.Message}");
        }
    }
}
