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
        _menu.Items.Add(MenuItem("Action.Open", Show));
        _menu.Items.Add(MenuItem("Action.CopyUrl", CopyUrl));
        _menu.Items.Add(MenuItem("Action.Reset", () => runner.ResetPicks()));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(MenuItem("Action.Quit", Quit));

        _icon = new TrayIcon(Strings.Get("App.Title"));
        _icon.DoubleClicked += Show;
        _icon.RightClicked += () => _icon?.ShowMenu(_menu);
    }

    private static MenuItem MenuItem(string key, Action action)
    {
        var item = new MenuItem();
        item.SetBinding(HeaderedItemsControl.HeaderProperty,
                        new System.Windows.Data.Binding($"[{key}]")
                        {
                            Source = LocSource.Current,
                            Mode = System.Windows.Data.BindingMode.OneWay,
                        });
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
        _icon.ShowBalloon(Strings.Get("Tray.HiddenTitle"), Strings.Get("Tray.HiddenBody"));
    }

    private static void CopyUrl() => UrlClipboard.Copy(_runner?.Url, Log.Write);

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
