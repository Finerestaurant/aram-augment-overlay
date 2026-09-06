using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AramOverlay.Core;

namespace AramOverlay.App;

public sealed class PickRow
{
    public string Name { get; init; } = "";
    public string Sub { get; init; } = "";
    public string Confidence { get; init; } = "";
    public Brush RarityBrush { get; init; } = Brushes.Gray;
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PickRow> _picks = new();
    private readonly List<string> _log = new();
    private readonly DispatcherTimer _timer;
    private readonly OverlayRunner _runner;
    private Settings _settings;
    private string? _lastPicksKey;
    private bool _loadingSettings;
    private bool _awaitingRestart;

    public MainWindow(OverlayRunner runner)
    {
        InitializeComponent();
        _runner = runner;
        _settings = Settings.Load();
        PicksList.ItemsSource = _picks;

        LoadSettingsIntoUi();
        Log.Add(line => Dispatcher.BeginInvoke(() => AppendLog(line)));

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    // ------------------------------------------------------------- title bar
    private void OnMinimise(object sender, RoutedEventArgs e) =>
        WindowState = System.Windows.WindowState.Minimized;

    private void OnMaximise(object sender, RoutedEventArgs e)
    {
        bool maximised = WindowState == System.Windows.WindowState.Maximized;
        WindowState = maximised
            ? System.Windows.WindowState.Normal
            : System.Windows.WindowState.Maximized;
        // E922 is the maximise glyph, E923 the restore one.
        MaximiseButton.Content = maximised ? "" : "";
        MaximiseButton.ToolTip = Strings.Get(maximised ? "Chrome.Maximise" : "Chrome.Restore");
    }

    /// <summary>
    /// Closing leaves it running in the tray -- closing the window used to be
    /// the only visible way to stop the tool, which is the confusion to avoid.
    /// </summary>
    private void OnHideToTray(object sender, RoutedEventArgs e) => TrayHost.HideToTray(this);

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (StatusView is null || SettingsView is null)
            return;                       // fires once during InitializeComponent
        bool status = NavStatus.IsChecked == true;
        StatusView.Visibility = status ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = status ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        string? url = _runner.Url;
        if (url is null)
            return;
        try
        {
            Clipboard.SetText(url);
            AppendLog(Strings.Get("Log.UrlCopied", url));
        }
        catch
        {
            // The clipboard can be held by another process; not worth a dialog.
        }
    }

    // --------------------------------------------------------------- actions
    private void OnReset(object sender, RoutedEventArgs e)
    {
        _runner.ResetPicks();
        _lastPicksKey = null;
        AppendLog(Strings.Get("Log.ListCleared"));
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        AppendLog(Strings.Get("Log.Retrying"));
        _runner.Restart();
    }

    private void OnQuit(object sender, RoutedEventArgs e) => TrayHost.Quit();

    // -------------------------------------------------------------- settings
    private void LoadSettingsIntoUi()
    {
        _loadingSettings = true;

        UiLanguageBox.ItemsSource = Strings.Languages.Select(l => l.Name).ToArray();
        UiLanguageBox.SelectedIndex = Math.Max(0,
            Array.FindIndex(Strings.Languages, l => l.Code == Strings.Language));

        LocaleBox.ItemsSource = Settings.Locales.Select(l => $"{l.Name}  ({l.Code})").ToArray();
        int localeIndex = Array.FindIndex(Settings.Locales, l => l.Code == _settings.Locale);
        LocaleBox.SelectedIndex = localeIndex < 0 ? 0 : localeIndex;

        var installed = TooltipOcr.InstalledLanguages();
        OcrBox.ItemsSource = new[] { Strings.Get("Settings.Auto") }.Concat(installed).ToArray();
        OcrBox.SelectedIndex = _settings.OcrLanguage.Length == 0
            ? 0 : Math.Max(0, installed.ToList().IndexOf(_settings.OcrLanguage) + 1);

        PresetBox.ItemsSource = new[]
            { "1920 × 1080", "2560 × 1440", "3840 × 2160", "1600 × 900", "1280 × 720" };

        WidthBox.Text = _settings.ScreenWidth.ToString();
        HeightBox.Text = _settings.ScreenHeight.ToString();
        ObsPortBox.Text = _settings.ObsPort.ToString();
        ObsSourceBox.Text = _settings.ObsSource;
        ObsPasswordBox.Text = _settings.ObsPassword;
        WidgetPortBox.Text = _settings.WidgetPort.ToString();
        RowsBox.Text = _settings.WidgetRows.ToString();
        MaxWidthBox.Text = _settings.WidgetMaxWidth.ToString();
        DebugModeSwitch.IsChecked = _settings.DebugMode;
        ApplyDebugMode(_settings.DebugMode);

        var (w, h, scale) = ScreenInfo.Detect();
        ScreenHint.Text = w > 0
            ? Strings.Get("Hint.ThisScreen", w, h, scale)
            : Strings.Get("Hint.ScreenUnknown");

        _loadingSettings = false;
        UpdateOcrHint();
        UpdateResolutionHint();
    }

    private void OnLocaleChanged(object sender, SelectionChangedEventArgs e) => UpdateOcrHint();

    /// <summary>
    /// The log is for working out why an augment went unrecorded, which is not
    /// something to put in front of someone who is just streaming. It keeps
    /// collecting either way, so turning it on shows what already happened
    /// rather than starting from blank.
    /// </summary>
    private void OnDebugModeToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
            return;
        bool on = DebugModeSwitch.IsChecked == true;
        _settings.DebugMode = on;
        _settings.Save();
        ApplyDebugMode(on);
    }

    private void ApplyDebugMode(bool on)
    {
        LogPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        LogRow.Height = on ? new GridLength(150) : new GridLength(0);
        if (on)
        {
            LogText.Text = string.Join("\n", _log.TakeLast(60));
            LogScroller.ScrollToEnd();
        }
    }

    /// <summary>
    /// Switches language live. Everything in XAML re-reads through the binding;
    /// the pieces set from code -- hints, status pills, the picks list -- are
    /// rebuilt here, since they hold plain strings rather than bindings.
    /// </summary>
    private void OnUiLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || UiLanguageBox.SelectedIndex < 0)
            return;
        string code = Strings.Languages[UiLanguageBox.SelectedIndex].Code;
        if (code == Strings.Language)
            return;

        Strings.Language = code;
        _settings.UiLanguage = code;
        _settings.Save();
        LocSource.Current.Refresh();

        // The OCR picker holds a translated "auto" entry, so it is rebuilt too.
        int ocrIndex = OcrBox.SelectedIndex;
        _loadingSettings = true;
        OcrBox.ItemsSource = new[] { Strings.Get("Settings.Auto") }
            .Concat(TooltipOcr.InstalledLanguages()).ToArray();
        OcrBox.SelectedIndex = Math.Max(0, ocrIndex);
        _loadingSettings = false;

        var (w, h, scale) = ScreenInfo.Detect();
        ScreenHint.Text = w > 0
            ? Strings.Get("Hint.ThisScreen", w, h, scale)
            : Strings.Get("Hint.ScreenUnknown");
        SavedHint.Text = "";
        UpdateOcrHint();
        UpdateResolutionHint();
        _lastPicksKey = null;          // force the picks list to be rebuilt
        Refresh();
    }

    private void OnResolutionChanged(object sender, TextChangedEventArgs e) => UpdateResolutionHint();

    private void OnPresetChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || PresetBox.SelectedItem is not string preset)
            return;
        var parts = preset.Replace(" ", "").Split('×');
        if (parts.Length == 2)
        {
            WidthBox.Text = parts[0];
            HeightBox.Text = parts[1];
        }
    }

    private void UpdateOcrHint()
    {
        if (_loadingSettings || OcrHint is null)
            return;
        string auto = Strings.Get("Settings.Auto");
        string chosen = OcrBox.SelectedItem as string ?? auto;
        if (chosen != auto)
        {
            OcrHint.Foreground = (Brush)FindResource("Faint");
            OcrHint.Text = Strings.Get("Hint.ReadsWith", chosen);
            return;
        }

        string code = Settings.Locales[Math.Max(0, LocaleBox.SelectedIndex)].Code;
        var tags = Settings.OcrForLocale.GetValueOrDefault(code, Array.Empty<string>());
        var installed = TooltipOcr.InstalledLanguages();
        string? have = tags.FirstOrDefault(installed.Contains);
        if (have is not null)
        {
            OcrHint.Foreground = (Brush)FindResource("Faint");
            OcrHint.Text = Strings.Get("Hint.ReadsWith", have);
        }
        else
        {
            string want = tags.FirstOrDefault() ?? "?";
            OcrHint.Foreground = (Brush)FindResource("Bad");
            OcrHint.Text = Strings.Get("Hint.OcrPackMissing", want);
        }
    }

    private void UpdateResolutionHint()
    {
        if (_loadingSettings || ResolutionHint is null)
            return;
        if (!int.TryParse(WidthBox.Text, out int w) || !int.TryParse(HeightBox.Text, out int h))
        {
            ResolutionHint.Foreground = (Brush)FindResource("Bad");
            ResolutionHint.Text = Strings.Get("Hint.NumbersOnly");
            return;
        }
        if (Settings.IsSupportedShape(w, h))
        {
            ResolutionHint.Foreground = (Brush)FindResource("Faint");
            ResolutionHint.Text = Strings.Get("Hint.Ratio169");
        }
        else
        {
            ResolutionHint.Foreground = (Brush)FindResource("Warn");
            ResolutionHint.Text = Strings.Get("Hint.NotRatio169");
        }
    }

    /// <summary>
    /// Writes obs-websocket's own config, which is the only thing that actually
    /// switches the server on -- the OBS command-line flags only override values.
    /// </summary>
    private void OnEnableWebsocket(object sender, RoutedEventArgs e)
    {
        int port = int.TryParse(ObsPortBox.Text, out int p) ? p : 4455;
        var (result, message) = ObsSetup.Enable(port);
        WebsocketHint.Foreground = (Brush)FindResource(
            result is ObsSetup.Result.Enabled or ObsSetup.Result.AlreadyOn ? "Ok" : "Warn");
        WebsocketHint.Text = message;
        Log.Write(message.Replace("\n", " "));
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text, out int width) ||
            !int.TryParse(HeightBox.Text, out int height) ||
            !int.TryParse(ObsPortBox.Text, out int obsPort) ||
            !int.TryParse(WidgetPortBox.Text, out int widgetPort) ||
            !int.TryParse(RowsBox.Text, out int rows) ||
            !int.TryParse(MaxWidthBox.Text, out int maxWidth))
        {
            SavedHint.Foreground = (Brush)FindResource("Bad");
            SavedHint.Text = Strings.Get("Hint.NotANumber");
            return;
        }

        string ocr = OcrBox.SelectedItem as string ?? Strings.Get("Settings.Auto");
        _settings = new Settings
        {
            UiLanguage = Strings.Languages[Math.Max(0, UiLanguageBox.SelectedIndex)].Code,
            DebugMode = DebugModeSwitch.IsChecked == true,
            Locale = Settings.Locales[Math.Max(0, LocaleBox.SelectedIndex)].Code,
            OcrLanguage = ocr == Strings.Get("Settings.Auto") ? "" : ocr,
            ScreenWidth = width,
            ScreenHeight = height,
            ObsPort = obsPort,
            ObsPassword = ObsPasswordBox.Text.Trim(),
            ObsSource = ObsSourceBox.Text.Trim(),
            WidgetPort = widgetPort,
            WidgetRows = rows,
            WidgetMaxWidth = maxWidth,
        };
        _settings.Save();

        SavedHint.Foreground = (Brush)FindResource("Ok");
        SavedHint.Text = Strings.Get("Hint.Saved");
        AppendLog(Strings.Get("Log.SettingsSaved"));
        _lastPicksKey = null;
        _awaitingRestart = true;
        _runner.Restart();
    }

    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        // The interface language is a preference about this window, not about
        // detection, so a settings reset leaves it alone.
        _settings = new Settings
        {
            UiLanguage = _settings.UiLanguage,
            DebugMode = _settings.DebugMode,
        };
        _settings.Save();
        LoadSettingsIntoUi();
        SavedHint.Foreground = (Brush)FindResource("Ok");
        SavedHint.Text = Strings.Get("Hint.DefaultsRestored");
        AppendLog(Strings.Get("Log.DefaultsRestored"));
        _awaitingRestart = true;
        _runner.Restart();
    }

    // ----------------------------------------------------------------- state
    private void AppendLog(string line)
    {
        _log.Add(line);
        if (_log.Count > 200)
            _log.RemoveRange(0, 50);
        LogText.Text = string.Join("\n", _log.TakeLast(60));
        LogScroller.ScrollToEnd();
    }

    private void Refresh()
    {
        var state = _runner.State;
        bool running = _runner.IsRunning;

        // A restart that fails leaves the settings tab saying "restarting"
        // forever, which is how a dead loop went unnoticed. The hint follows it
        // to the end either way.
        if (_awaitingRestart && !_runner.IsBusy)
        {
            _awaitingRestart = false;
            bool ok = running && _runner.Url is not null;
            SavedHint.Foreground = (Brush)FindResource(ok ? "Ok" : "Bad");
            SavedHint.Text = Strings.Get(ok ? "Hint.Restarted" : "Hint.RestartFailed");
        }

        if (running && _runner.Url is not null)
        {
            ObsDot.Fill = (Brush)FindResource("Ok");
            ObsText.Text = Strings.Get("Status.ObsConnected");
            WidgetUrlText.Text = _runner.Url;
            RetryButton.Visibility = Visibility.Collapsed;
        }
        else if (running)
        {
            ObsDot.Fill = (Brush)FindResource("Dim");
            ObsText.Text = Strings.Get("Status.Starting");
        }
        else
        {
            ObsDot.Fill = (Brush)FindResource("Bad");
            ObsText.Text = Strings.Get("Status.ObsDisconnected");
            RetryButton.Visibility = Visibility.Visible;
        }

        if (state.Connected)
        {
            GameDot.Fill = (Brush)FindResource("Ok");
            GameText.Text = state.GameMode == Config.MayhemGameMode
                ? (state.Level is int lv
                    ? Strings.Get("Status.GameDetectedLevel", lv)
                    : Strings.Get("Status.GameDetected"))
                : Strings.Get("Status.NotMayhem", state.GameMode);
        }
        else
        {
            GameDot.Fill = (Brush)FindResource("Dim");
            GameText.Text = Strings.Get("Status.WaitingForGame");
        }

        List<Pick> picks;
        lock (state)
            picks = state.Picks.ToList();
        string key = string.Join("|", picks.Select(p => $"{p.Name}/{p.Rarity}/{p.Level}"));
        if (key == _lastPicksKey)
            return;
        _lastPicksKey = key;

        _picks.Clear();
        foreach (var pick in picks)
        {
            string rarity = Strings.Get($"Rarity.{pick.Rarity}");
            _picks.Add(new PickRow
            {
                Name = pick.Name,
                Sub = pick.Level is int level ? Strings.Get("Rarity.WithLevel", rarity, level) : rarity,
                Confidence = pick.Confidence > 0 ? $"{pick.Confidence:0.00}" : "",
                RarityBrush = (Brush)FindResource(pick.Rarity switch
                {
                    "silver" => "Silver",
                    "gold" => "Gold",
                    "prismatic" => "Prism",
                    _ => "Dim",
                }),
            });
        }
        CountText.Text = Strings.Get("Status.PickCount", picks.Count);
        EmptyText.Visibility = picks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>Physical pixels and display scaling, read without making this
/// process DPI-aware -- doing that after WPF starts mis-scales the window.</summary>
public static class ScreenInfo
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr dc, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    public static (int Width, int Height, int Scale) Detect()
    {
        try
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            int physicalW = GetDeviceCaps(dc, 118);       // DESKTOPHORZRES
            int physicalH = GetDeviceCaps(dc, 117);       // DESKTOPVERTRES
            ReleaseDC(IntPtr.Zero, dc);
            int logicalW = GetSystemMetrics(0);
            return (physicalW, physicalH,
                    logicalW > 0 ? (int)Math.Round(physicalW * 100.0 / logicalW) : 100);
        }
        catch
        {
            return (0, 0, 100);
        }
    }
}
