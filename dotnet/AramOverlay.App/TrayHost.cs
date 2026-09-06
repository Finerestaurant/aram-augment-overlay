using System.Windows;
using System.Windows.Controls;
using AramOverlay.Core;

namespace AramOverlay.App;

/// <summary>
/// The tray icon, and the two ways out of the window.
///
/// Closing the window hides it here instead of quitting, because closing used to
/// be the only visible way to stop the tool. Windows files a new tray icon under
/// the overflow chevron, so the first time it happens the balloon says where the
/// window went.
/// </summary>
public static class TrayHost
{
    private static TrayIcon? _icon;
    private static ContextMenu? _menu;
    private static MainWindow? _window;
    private static OverlayRunner? _runner;
    private static bool _toldAboutTray;
    private static bool _quitting;

    public static void Install(MainWindow window, OverlayRunner runner)
    {
        _window = window;
        _runner = runner;

        _menu = new ContextMenu();
        _menu.Items.Add(MenuItem("열기", Show));
        _menu.Items.Add(MenuItem("위젯 주소 복사", CopyUrl));
        _menu.Items.Add(MenuItem("목록 초기화", () => runner.ResetPicks()));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(MenuItem("종료", Quit));

        _icon = new TrayIcon("아수라장 증강 오버레이");
        _icon.DoubleClicked += Show;
        _icon.RightClicked += () => _icon?.ShowMenu(_menu);
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    public static void Show()
    {
        if (_window is null)
            return;
        _window.Dispatcher.Invoke(() =>
        {
            _window.Show();
            _window.WindowState = System.Windows.WindowState.Normal;
            _window.Activate();
        });
    }

    public static void HideToTray(Window window)
    {
        window.Hide();
        if (_toldAboutTray || _icon is null)
            return;
        _toldAboutTray = true;
        _icon.ShowBalloon("아수라장 증강 오버레이",
                          "트레이에서 계속 실행 중입니다. 아이콘을 두 번 누르면 다시 열립니다.");
    }

    private static void CopyUrl()
    {
        string? url = _runner?.Url;
        if (url is null)
            return;
        try
        {
            Clipboard.SetText(url);
            Log.Write($"위젯 주소를 복사했습니다: {url}");
        }
        catch
        {
            // The clipboard can be held by another process.
        }
    }

    /// <summary>
    /// Shut down the way the Quit button does: stop the loop, drop the tray
    /// icon, close the window. Killing the process instead leaves the icon
    /// behind as a ghost until something makes Windows notice it is dead.
    /// </summary>
    public static void Quit()
    {
        if (_quitting)
            return;
        _quitting = true;

        _icon?.Dispose();
        _icon = null;
        _runner?.Stop();
        Application.Current?.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }
}
