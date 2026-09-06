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
    private static readonly Dictionary<string, string> RarityNames = new()
    {
        ["silver"] = "실버", ["gold"] = "골드", ["prismatic"] = "프리즘", ["unknown"] = "미상",
    };

    private readonly ObservableCollection<PickRow> _picks = new();
    private readonly List<string> _log = new();
    private readonly DispatcherTimer _timer;
    private readonly OverlayRunner _runner;
    private Settings _settings;
    private string? _lastPicksKey;
    private bool _loadingSettings;

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
        MaximiseButton.ToolTip = maximised ? "최대화" : "이전 크기로";
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
            AppendLog($"위젯 주소를 복사했습니다: {url}");
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
        AppendLog("목록을 비웠습니다.");
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        AppendLog("다시 시도합니다...");
        _runner.Restart();
    }

    private void OnQuit(object sender, RoutedEventArgs e) => TrayHost.Quit();

    // -------------------------------------------------------------- settings
    private void LoadSettingsIntoUi()
    {
        _loadingSettings = true;

        LocaleBox.ItemsSource = Settings.Locales.Select(l => $"{l.Name}  ({l.Code})").ToArray();
        int localeIndex = Array.FindIndex(Settings.Locales, l => l.Code == _settings.Locale);
        LocaleBox.SelectedIndex = localeIndex < 0 ? 0 : localeIndex;

        var installed = TooltipOcr.InstalledLanguages();
        OcrBox.ItemsSource = new[] { "자동" }.Concat(installed).ToArray();
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

        var (w, h, scale) = ScreenInfo.Detect();
        ScreenHint.Text = w > 0
            ? $"이 PC 화면: {w}×{h}, 배율 {scale}%"
            : "화면 정보를 읽지 못했습니다";

        _loadingSettings = false;
        UpdateOcrHint();
        UpdateResolutionHint();
    }

    private void OnLocaleChanged(object sender, SelectionChangedEventArgs e) => UpdateOcrHint();

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
        string chosen = OcrBox.SelectedItem as string ?? "자동";
        if (chosen != "자동")
        {
            OcrHint.Foreground = (Brush)FindResource("Faint");
            OcrHint.Text = $"'{chosen}' 로 읽습니다.";
            return;
        }

        string code = Settings.Locales[Math.Max(0, LocaleBox.SelectedIndex)].Code;
        var tags = Settings.OcrForLocale.GetValueOrDefault(code, Array.Empty<string>());
        var installed = TooltipOcr.InstalledLanguages();
        string? have = tags.FirstOrDefault(installed.Contains);
        if (have is not null)
        {
            OcrHint.Foreground = (Brush)FindResource("Faint");
            OcrHint.Text = $"'{have}' 로 읽습니다.";
        }
        else
        {
            string want = tags.FirstOrDefault() ?? "?";
            OcrHint.Foreground = (Brush)FindResource("Bad");
            OcrHint.Text = $"'{want}' OCR 언어 팩이 없습니다. 관리자 PowerShell에서\n" +
                           $"Add-WindowsCapability -Online -Name 'Language.OCR~~~{want}~0.0.1.0'";
        }
    }

    private void UpdateResolutionHint()
    {
        if (_loadingSettings || ResolutionHint is null)
            return;
        if (!int.TryParse(WidthBox.Text, out int w) || !int.TryParse(HeightBox.Text, out int h))
        {
            ResolutionHint.Foreground = (Brush)FindResource("Bad");
            ResolutionHint.Text = "숫자를 입력하세요.";
            return;
        }
        if (Settings.IsSupportedShape(w, h))
        {
            ResolutionHint.Foreground = (Brush)FindResource("Faint");
            ResolutionHint.Text = "16:9 — 좌표가 비율대로 환산되어 그대로 동작합니다.";
        }
        else
        {
            ResolutionHint.Foreground = (Brush)FindResource("Warn");
            ResolutionHint.Text = "16:9가 아닙니다. 클라이언트가 HUD를 다르게 배치하므로 " +
                                  "카드 위치가 어긋날 수 있습니다.";
        }
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
            SavedHint.Text = "숫자 칸에 숫자가 아닌 값이 있습니다.";
            return;
        }

        string ocr = OcrBox.SelectedItem as string ?? "자동";
        _settings = new Settings
        {
            Locale = Settings.Locales[Math.Max(0, LocaleBox.SelectedIndex)].Code,
            OcrLanguage = ocr == "자동" ? "" : ocr,
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
        SavedHint.Text = "저장했습니다. 다시 시작합니다...";
        AppendLog("설정을 저장했습니다. 다시 시작합니다...");
        _lastPicksKey = null;
        _runner.Restart();
    }

    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        _settings = new Settings();
        _settings.Save();
        LoadSettingsIntoUi();
        SavedHint.Foreground = (Brush)FindResource("Ok");
        SavedHint.Text = "기본값으로 되돌렸습니다. 다시 시작합니다...";
        AppendLog("설정을 기본값으로 되돌렸습니다.");
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

        if (running && _runner.Url is not null)
        {
            ObsDot.Fill = (Brush)FindResource("Ok");
            ObsText.Text = "OBS 연결됨";
            WidgetUrlText.Text = _runner.Url;
            RetryButton.Visibility = Visibility.Collapsed;
        }
        else if (running)
        {
            ObsDot.Fill = (Brush)FindResource("Dim");
            ObsText.Text = "시작하는 중";
        }
        else
        {
            ObsDot.Fill = (Brush)FindResource("Bad");
            ObsText.Text = "OBS 연결 안 됨";
            RetryButton.Visibility = Visibility.Visible;
        }

        if (state.Connected)
        {
            GameDot.Fill = (Brush)FindResource("Ok");
            GameText.Text = state.GameMode == Config.MayhemGameMode
                ? (state.Level is int lv ? $"게임 감지 (Lv {lv})" : "게임 감지")
                : $"{state.GameMode} — 감지 안 함";
        }
        else
        {
            GameDot.Fill = (Brush)FindResource("Dim");
            GameText.Text = "게임 대기 중";
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
            string rarity = RarityNames.GetValueOrDefault(pick.Rarity, RarityNames["unknown"]);
            _picks.Add(new PickRow
            {
                Name = pick.Name,
                Sub = pick.Level is int level ? $"{rarity} · {level}레벨" : rarity,
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
        CountText.Text = $"획득한 증강 {picks.Count}개";
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
