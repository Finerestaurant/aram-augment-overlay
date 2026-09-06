namespace AramOverlay.Core;

/// <summary>Cached fetches from CommunityDragon.</summary>
public static class CDragon
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// The cached copy if it is younger than <paramref name="maxAgeDays"/>,
    /// otherwise a fresh download. A failed refresh falls back to a stale cache
    /// rather than stopping startup -- names going a week out of date beats the
    /// tool refusing to run because CommunityDragon is down.
    /// </summary>
    public static async Task<string> FetchAsync(string url, string cachePath,
                                                bool refresh, int maxAgeDays)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var cache = new FileInfo(cachePath);
        bool fresh = cache.Exists &&
                     DateTime.UtcNow - cache.LastWriteTimeUtc < TimeSpan.FromDays(maxAgeDays);
        if (fresh && !refresh)
            return await File.ReadAllTextAsync(cachePath);

        try
        {
            string body = await Http.GetStringAsync(url);
            await File.WriteAllTextAsync(cachePath, body);
            return body;
        }
        catch (Exception) when (cache.Exists)
        {
            return await File.ReadAllTextAsync(cachePath);
        }
    }
}
