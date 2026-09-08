using System.Windows;
using AramOverlay.Core;

namespace AramOverlay.App;

/// <summary>
/// Putting the widget address on the clipboard, with both ways it fails said
/// out loud.
///
/// The button had two silent exits and a user could not tell them apart or
/// tell either from a button that did nothing: it returned without a word when
/// there was no address yet, and it swallowed the clipboard exception whole.
/// Pressing it and having nothing happen is the one outcome that must not be
/// possible, because the address is the thing OBS needs and a stuck button
/// looks like a broken app.
///
/// The retries are not politeness. The clipboard is one system-wide lock and
/// any process can hold it for a few milliseconds -- an overlay, a clipboard
/// manager, the game -- so a single attempt fails often enough to be the
/// ordinary case rather than the exceptional one.
/// </summary>
internal static class UrlClipboard
{
    private const int Retries = 10;
    private const int RetryDelayMs = 50;

    public static void Copy(string? url, Action<string> report)
    {
        if (url is null)
        {
            report(Strings.Get("Log.UrlNotReady"));
            return;
        }
        // WPF's Clipboard has no retrying overload -- that one is WinForms' --
        // so the loop is here.
        Exception? last = null;
        for (int attempt = 0; attempt < Retries; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(url, true);
                report(Strings.Get("Log.UrlCopied", url));
                return;
            }
            catch (Exception exc)
            {
                last = exc;
                Thread.Sleep(RetryDelayMs);
            }
        }
        // The address goes in the message too, so a failure still leaves it
        // somewhere the user can read it off and type in by hand.
        report(Strings.Get("Log.UrlCopyFailed", url, last?.Message ?? ""));
    }
}
