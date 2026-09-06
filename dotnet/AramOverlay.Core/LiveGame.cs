using System.Text.Json;
using System.Text.Json.Nodes;

namespace AramOverlay.Core;

/// <summary>
/// The League client's Live Client Data API.
///
/// It gives game mode and level cheaply, so the screen only needs watching while
/// a Mayhem game is actually running. It carries nothing about augments -- the
/// full spec was checked and there is no field for them, which is why the rest
/// of this tool reads the screen.
/// </summary>
public static class LiveGame
{
    // The client serves this over HTTPS with its own certificate, so validation
    // is off for this one endpoint on localhost.
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    { Timeout = TimeSpan.FromSeconds(2) };

    public static async Task<JsonNode?> PollAsync()
    {
        try
        {
            var response = await Http.GetAsync(Config.LiveUrl);
            if (!response.IsSuccessStatusCode)
                return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync());
        }
        catch
        {
            return null;                 // no game running, or the client is busy
        }
    }

    public static string ModeOf(JsonNode? game) =>
        game?["gameData"]?["gameMode"]?.GetValue<string>() ?? "";

    public static int? LevelOf(JsonNode? game)
    {
        var level = game?["activePlayer"]?["level"];
        return level is null ? null : (int?)level.GetValue<double>();
    }

    public static double TimeOf(JsonNode? game)
    {
        var t = game?["gameData"]?["gameTime"];
        try
        {
            return t is null ? 0 : t.GetValue<double>();
        }
        catch (Exception exc) when (exc is InvalidOperationException or FormatException)
        {
            return 0;
        }
    }
}
