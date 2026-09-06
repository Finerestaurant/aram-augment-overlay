using System.Text.RegularExpressions;

namespace AramOverlay.Core;

/// <summary>
/// The language the League client is set to, which is the language the augment
/// names arrive in and the screen has to be read with.
///
/// The Windows display language is not a reliable stand-in for it. On the
/// machine this was measured against, Windows is en-US and the client is ko_KR:
/// guessing from the system would fetch English augment names and point the
/// English OCR pack at Korean cards, and recognise nothing at all.
///
/// Two files on disk carry the answer, and they agreed there:
///
///   %ProgramData%\Riot Games\Metadata\league_of_legends.live\
///       league_of_legends.live.product_settings.yaml    locale_data.default_locale
///   &lt;install&gt;\Config\LeagueClientSettings.yaml         install.globals.locale
///
/// The second is the file the client rewrites when a player changes language,
/// so it is asked first. The first is only reachable at a path that never
/// moves, which is also where the install directory comes from --
/// product_install_full_path. The registry key other tools read for that
/// (HKLM\SOFTWARE\WOW6432Node\Riot Games, Inc\League of Legends -> Location)
/// was absent on the measured machine, so it is not used.
///
/// Nothing here needs the client to be running, and nothing here parses YAML:
/// one line is wanted out of each file, and every answer is checked against the
/// locales Riot actually publishes before it is believed.
/// </summary>
public static class ClientLocale
{
    private static readonly Regex DefaultLocale =
        new(@"default_locale\s*:\s*""?(?<v>[A-Za-z]{2}_[A-Za-z]{2})""?", RegexOptions.Compiled);

    private static readonly Regex InstallPath =
        new(@"product_install_full_path\s*:\s*""?(?<v>[^""\r\n]+?)""?\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

    // Indented, so the bare "locale:" of some other block cannot win by accident.
    private static readonly Regex GlobalsLocale =
        new(@"^\s+locale\s*:\s*""?(?<v>[A-Za-z]{2}_[A-Za-z]{2})""?",
            RegexOptions.Compiled | RegexOptions.Multiline);

    private static string ProductSettings => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Riot Games", "Metadata", "league_of_legends.live",
        "league_of_legends.live.product_settings.yaml");

    /// <summary>
    /// The client's locale as one of <see cref="Settings.Locales"/>, or null when
    /// neither file is there to be read. Never throws: a locked file, a path that
    /// does not exist and a file in a shape nobody expected all mean the same
    /// thing here, which is that the caller should fall back.
    /// </summary>
    public static string? Detect()
    {
        try
        {
            string product = ProductSettings;
            if (!File.Exists(product))
                return null;

            string text = File.ReadAllText(product);

            var install = InstallPath.Match(text);
            if (install.Success)
            {
                // The path is written with forward slashes; Windows takes them.
                string settings = Path.Combine(install.Groups["v"].Value.Trim(),
                                               "Config", "LeagueClientSettings.yaml");
                if (File.Exists(settings))
                {
                    string? chosen = Supported(GlobalsLocale.Match(File.ReadAllText(settings)));
                    if (chosen is not null)
                        return chosen;
                }
            }

            return Supported(DefaultLocale.Match(text));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A match is only an answer if Riot publishes that folder.</summary>
    private static string? Supported(Match match)
    {
        if (!match.Success)
            return null;
        string code = match.Groups["v"].Value.ToLowerInvariant();
        return Array.Exists(Settings.Locales, l => l.Code == code) ? code : null;
    }
}
