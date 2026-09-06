using System.Diagnostics;

namespace AramOverlay.Core;

/// <summary>
/// Installing the Windows OCR language pack the tool needs.
///
/// OCR is a Feature on Demand tied to a language, and Windows only preinstalls
/// the ones for the display languages the machine shipped with -- a Korean
/// Windows has ko and nothing else. So anyone reading the screen in a language
/// their Windows did not ship with has to add a pack first, and telling them to
/// open an admin PowerShell is telling most people to give up.
///
/// The install needs elevation, so this hands the command to a UAC prompt
/// rather than trying to be clever about it. Basic goes first because OCR
/// depends on it; adding one that is already present is a no-op.
/// </summary>
public static class OcrSetup
{
    public enum Result { Started, Declined, Failed }

    /// <summary>
    /// The capability name for a Windows language tag. Note this is not the tag
    /// the OCR engine reports back: Windows installs Language.OCR~~~zh-CN and
    /// the engine then lists it as zh-Hans-CN. Asking for the engine's tag
    /// installs nothing, and used to do so without complaining.
    /// </summary>
    public static string CapabilityName(string languageTag) =>
        $"Language.OCR~~~{languageTag}~0.0.1.0";

    /// <summary>
    /// Ask Windows to add the pack, via an elevated PowerShell. Returns as soon
    /// as the prompt is answered -- the install runs for minutes, so the caller
    /// watches for the language to appear and uses the exit code only to tell a
    /// finished install from a failed one.
    /// </summary>
    public static (Result Result, Process? Process) Install(string languageTag)
    {
        // Add-WindowsCapability reports a bad name as a non-terminating error
        // and PowerShell still exits 0, so a silent no-op read as success. The
        // exit code has to carry the failure, hence the explicit try/exit.
        string command =
            "try { Add-WindowsCapability -Online -Name " +
            $"'Language.Basic~~~{languageTag}~0.0.1.0' -ErrorAction Stop | Out-Null }} catch {{ }}; " +
            "try { Add-WindowsCapability -Online -Name " +
            $"'{CapabilityName(languageTag)}' -ErrorAction Stop | Out-Null }} catch {{ exit 1 }}; " +
            "exit 0";

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"",
            UseShellExecute = true,       // required for the elevation verb
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            var process = Process.Start(start);
            return process is null ? (Result.Failed, null) : (Result.Started, process);
        }
        catch (System.ComponentModel.Win32Exception exc) when (exc.NativeErrorCode == 1223)
        {
            return (Result.Declined, null);      // ERROR_CANCELLED: UAC dismissed
        }
        catch
        {
            return (Result.Failed, null);
        }
    }

    /// <summary>The Settings page where this can be done by hand instead.</summary>
    public static void OpenLanguageSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:regionlanguage")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // Settings can be blocked by policy; the log already carries the
            // command line as a fallback.
        }
    }
}
