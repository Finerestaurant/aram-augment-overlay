using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AramOverlay.Core;

namespace AramOverlay.App;

public sealed class PickRow
{
    public string Name { get; init; } = "";
    public string Rarity { get; init; } = "";
    /// <summary>How the pick was decided and how sure -- shown only with the
    /// log, since it means nothing to a streamer and everything to whoever
    /// is working out a wrong one.</summary>
    public string Detail { get; init; } = "";
    public string Level { get; init; } = "";
    public Visibility LevelVisibility => Level.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Brush RarityBrush { get; init; } = Brushes.Gray;
    public Brush RaritySoftBrush { get; init; } = Brushes.Transparent;
    /// <summary>The augment's white line-art glyph, used as a mask; null draws
    /// the plain square behind it.</summary>
    public ImageSource? Icon { get; init; }
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PickRow> _picks = new();
    private readonly List<string> _log = new();
    private readonly DispatcherTimer _timer;
    private readonly OverlayRunner _runner;
    private Settings _settings;
    // What the loop was last started with. The form is compared against this
    // rather than against the saved file: a setting saved but not yet applied
    // still needs a restart, and one changed back to what is running does not.
    private Settings _running;
    private string? _lastPicksKey;
    private bool _loadingSettings;
    private bool _awaitingRestart;
    private string? _missingOcrTag;          // what the engine would report
    private string? _missingOcrCapability;   // what Windows installs
    private DispatcherTimer? _ocrWatch;
    // What the status line says after "turn it on" was pressed, until the loop
    // comes back or Retry is pressed. The outcome of that click is the one thing
    // the state polled every 400 ms cannot know.
    private (string Text, string Brush)? _statusNote;
    private Action? _confirmed;
    // Whether the OBS process exists is only asked while disconnected, and not
    // on every tick even then: enumerating processes is not free.
    private DateTime _obsCheckedAt;
    private bool _obsRunning;

    public MainWindow(OverlayRunner runner)
    {
        InitializeComponent();
        _runner = runner;
        _settings = Settings.Load();
        _running = Settings.Load();
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
        StatusActions.Visibility = status ? Visibility.Visible : Visibility.Collapsed;
        SettingsActions.Visibility = status ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The outcome appears beside the button and goes away on its own. The log
    /// line was the only word before, and the log is hidden by default, so
    /// pressing the button looked like it had done nothing.
    /// </summary>
    private void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        var outcome = UrlClipboard.Copy(_runner.Url, AppendLog);
        var (key, tone) = outcome switch
        {
            UrlClipboard.Outcome.Copied => ("Hint.Copied", "Ok"),
            UrlClipboard.Outcome.NotReady => ("Hint.CopyNotReady", "Warn"),
            _ => ("Hint.CopyFailed", "Bad"),
        };
        CopyHint.Foreground = (Brush)FindResource(tone);
        CopyHint.Text = Strings.Get(key);
        CopyHint.Visibility = Visibility.Visible;

        _copyHintTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _copyHintTimer.Stop();
        _copyHintTimer.Tick -= HideCopyHint;
        _copyHintTimer.Tick += HideCopyHint;
        _copyHintTimer.Start();
    }

    private DispatcherTimer? _copyHintTimer;

    private void HideCopyHint(object? sender, EventArgs e)
    {
        _copyHintTimer?.Stop();
        CopyHint.Visibility = Visibility.Collapsed;
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
        _statusNote = null;
        AppendLog(Strings.Get("Log.Retrying"));
        _runner.Restart();
    }

    /// <summary>
    /// The button on the status line. While disconnected it is either Retry
    /// (OBS is up, so the loop just has to try again) or Turn it on (OBS is
    /// down, so its websocket server can be switched on in its config).
    /// </summary>
    private void OnStatusAction(object sender, RoutedEventArgs e)
    {
        if (_obsRunning)
        {
            OnRetry(sender, e);
            return;
        }
        var (result, message) = EnableWebsocket();
        _statusNote = (message,
            result is ObsSetup.Result.Enabled or ObsSetup.Result.AlreadyOn ? "Ok" : "Warn");
        Refresh();
    }

    private void OnQuit(object sender, RoutedEventArgs e) =>
        Confirm("Confirm.QuitTitle", "Confirm.QuitBody", "Action.Quit", TrayHost.Quit);

    // ---------------------------------------------------------- confirmation
    private void Confirm(string titleKey, string bodyKey, string okKey, Action onOk)
    {
        ConfirmTitle.Text = Strings.Get(titleKey);
        ConfirmBody.Text = Strings.Get(bodyKey);
        ConfirmOk.Content = Strings.Get(okKey);
        _confirmed = onOk;
        ConfirmVeil.Visibility = Visibility.Visible;
        ConfirmOk.Focus();
    }

    private void OnConfirmOk(object sender, RoutedEventArgs e)
    {
        var action = _confirmed;
        _confirmed = null;
        ConfirmVeil.Visibility = Visibility.Collapsed;
        action?.Invoke();
    }

    private void OnConfirmCancel(object sender, RoutedEventArgs e)
    {
        _confirmed = null;
        ConfirmVeil.Visibility = Visibility.Collapsed;
    }

    // A click on the card must not fall through to the veil and cancel.
    private void OnConfirmCardClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ConfirmVeil.Visibility == Visibility.Visible)
        {
            OnConfirmCancel(this, e);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

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

        UpdateScreenHint();

        _loadingSettings = false;
        UpdateOcrHint();
        UpdateResolutionHint();
        UpdateSaveButton();
    }

    private void UpdateScreenHint()
    {
        var (w, h, scale) = ScreenInfo.Detect();
        ScreenHint.Text = w > 0
            ? Strings.Get("Hint.ThisScreen", w, h, scale)
            : Strings.Get("Hint.ScreenUnknown");
    }

    private void OnLocaleChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateOcrHint();
        UpdateSaveButton();
    }

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
        _lastPicksKey = null;          // the rows show their score only with the log
    }

    private void ApplyDebugMode(bool on)
    {
        // Straight onto Config, not only into the file. The loop reads this
        // every time it decides whether to write a decision frame, so the switch
        // takes effect on the next pick rather than on the next restart -- which
        // is what the button now promises by not offering one.
        Config.DebugMode = on;
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
    /// the pieces set from code -- hints, the status line, the picks list -- are
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
        UpdateSaveButton();

        UpdateScreenHint();
        SavedHint.Text = "";
        _statusNote = null;
        UpdateOcrHint();
        UpdateResolutionHint();
        _lastPicksKey = null;          // force the picks list to be rebuilt
        Refresh();
    }

    private void OnResolutionChanged(object sender, TextChangedEventArgs e)
    {
        UpdateResolutionHint();
        SyncPreset();
        if (!_loadingSettings)
            UpdateSaveButton();
    }

    /// <summary>The preset box shows the preset the two fields spell, or nothing
    /// when they spell none -- it must never claim one they do not match.</summary>
    private void SyncPreset()
    {
        if (PresetBox?.ItemsSource is not string[] presets)
            return;
        string current = $"{WidthBox.Text.Trim()} × {HeightBox.Text.Trim()}";
        int index = Array.IndexOf(presets, current);
        if (PresetBox.SelectedIndex == index)
            return;
        bool was = _loadingSettings;
        _loadingSettings = true;             // OnPresetChosen must not write the fields back
        PresetBox.SelectedIndex = index;
        _loadingSettings = was;
    }

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

    /// <summary>
    /// One language drives both halves: which names to fetch and which
    /// recogniser reads the screen. They used to be separate settings, and a
    /// pair that did not match read nothing at all while the window kept
    /// detecting the augment screen perfectly -- the hardest failure to guess at.
    /// </summary>
    private void UpdateOcrHint()
    {
        if (_loadingSettings || OcrHint is null)
            return;

        string code = Settings.Locales[Math.Max(0, LocaleBox.SelectedIndex)].Code;
        var tags = Settings.OcrForLocale.GetValueOrDefault(code, Array.Empty<string>());
        var installed = TooltipOcr.InstalledLanguages();
        string? have = tags.FirstOrDefault(installed.Contains);

        _missingOcrTag = null;
        _missingOcrCapability = null;
        OcrFixRow.Visibility = Visibility.Collapsed;

        if (have is not null)
        {
            OcrHint.Foreground = (Brush)FindResource("Dim");
            OcrHint.Text = Strings.Get("Hint.ReadsWith", have);
            return;
        }

        string want = tags.FirstOrDefault() ?? "?";
        _missingOcrTag = want;
        _missingOcrCapability = Settings.CapabilityTag(code);
        OcrHint.Foreground = (Brush)FindResource("Bad");
        OcrHint.Text = Strings.Get("Hint.OcrPackMissing", _missingOcrCapability);
        OcrFixRow.Visibility = Visibility.Visible;
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
            ResolutionHint.Foreground = (Brush)FindResource("Dim");
            ResolutionHint.Text = Strings.Get("Hint.Ratio169");
        }
        else
        {
            // No longer a warning: the geometry follows the height and centres
            // itself, measured on every fullscreen size the client offers.
            ResolutionHint.Foreground = (Brush)FindResource("Dim");
            ResolutionHint.Text = Strings.Get("Hint.NotRatio169");
        }
    }

    /// <summary>
    /// Writes obs-websocket's own config, which is the only thing that actually
    /// switches the server on -- the OBS command-line flags only override values.
    /// Shared by the settings row and the status line's button.
    /// </summary>
    private (ObsSetup.Result Result, string Message) EnableWebsocket()
    {
        int port = int.TryParse(ObsPortBox.Text, out int p) ? p : 4455;
        var (result, message) = ObsSetup.Enable(port);
        WebsocketHint.Foreground = (Brush)FindResource(
            result is ObsSetup.Result.Enabled or ObsSetup.Result.AlreadyOn ? "Ok" : "Warn");
        WebsocketHint.Text = message;
        Log.Write(message.Replace("\n", " "));
        return (result, message);
    }

    private void OnEnableWebsocket(object sender, RoutedEventArgs e) => EnableWebsocket();

    /// <summary>
    /// Adds the language pack through an elevated prompt, then waits for the
    /// recogniser to report it. Windows only ships OCR for the display
    /// languages the machine came with, so anyone reading a different language
    /// lands here, and the manual route is an admin PowerShell -- which is
    /// where most people would stop.
    ///
    /// The install runs hidden and takes minutes, so without a running count
    /// there is nothing on screen to say it is working; that silence is what
    /// made the first version look broken.
    /// </summary>
    private void OnInstallOcr(object sender, RoutedEventArgs e)
    {
        if (_missingOcrTag is null || _missingOcrCapability is null)
            return;
        string engineTag = _missingOcrTag;
        string capability = _missingOcrCapability;

        // Say the prompt is coming before it steals focus, and paint it first.
        OcrPackDesc.Foreground = (Brush)FindResource("Warn");
        OcrPackDesc.Text = Strings.Get("Hint.OcrElevating");
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        var (result, process) = OcrSetup.Install(capability);
        if (result != OcrSetup.Result.Started)
        {
            OcrPackDesc.Foreground = (Brush)FindResource("Bad");
            OcrPackDesc.Text = Strings.Get(result == OcrSetup.Result.Declined
                ? "Hint.OcrInstallDeclined"
                : "Hint.OcrInstallFailed", OcrSetup.CapabilityName(capability));
            return;
        }

        // Only the enabled state changes: assigning Content would replace the
        // language binding with a fixed string, and it would stop following a
        // language switch from then on.
        InstallOcrButton.IsEnabled = false;
        OcrProgress.Visibility = Visibility.Visible;
        ShowInstalling(capability, "0:00");
        AppendLog(Strings.Get("Hint.OcrInstallingFor", capability, "0:00"));

        var started = DateTime.UtcNow;
        _ocrWatch?.Stop();
        _ocrWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ocrWatch.Tick += (_, _) =>
        {
            try
            {
                WatchInstall(started, engineTag, capability, process);
            }
            catch (Exception exc)
            {
                _ocrWatch!.Stop();
                InstallOcrButton.IsEnabled = true;
                OcrProgress.Visibility = Visibility.Collapsed;
                OcrPackDesc.Foreground = (Brush)FindResource("Bad");
                OcrPackDesc.Text = Strings.Get("Hint.OcrInstallFailed",
                                               OcrSetup.CapabilityName(capability));
                AppendLog($"{exc.GetType().Name}: {exc.Message}");
            }
        };
        _ocrWatch.Start();
    }

    /// <summary>
    /// What the row says while the installer runs: which pack and how long so
    /// far, over a bar that only moves. Windows reports no percentage for a
    /// Feature-on-Demand install, and a bar creeping along on a guess would be
    /// lying -- but that is a fact about the implementation, not something the
    /// person waiting needs to be told.
    /// </summary>
    private void ShowInstalling(string capability, string elapsed)
    {
        OcrPackDesc.Foreground = (Brush)FindResource("Warn");
        OcrPackDesc.Text = Strings.Get("Hint.OcrInstallingFor", capability, elapsed);
    }

    private void WatchInstall(DateTime started, string engineTag, string capability,
                              System.Diagnostics.Process? process)
    {
        var elapsed = DateTime.UtcNow - started;
        // The capability is ja-JP while the engine lists ja, so this asks
        // the engine its own way rather than comparing the strings.
        bool present = TooltipOcr.HasLanguage(engineTag);
        bool finished = process?.HasExited == true;

        if (!present && !finished && elapsed < TimeSpan.FromMinutes(20))
        {
            ShowInstalling(capability, $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}");
            return;
        }

        _ocrWatch!.Stop();
        InstallOcrButton.IsEnabled = true;
        OcrProgress.Visibility = Visibility.Collapsed;

        // Three outcomes, and the exit code alone cannot tell them apart:
        // the recogniser sees it, the install finished but the recogniser
        // has not picked it up yet, or it failed. Claiming success on exit
        // code 0 is what made a silent no-op look like it had worked.
        // ExitCode throws while the process is still running, and the
        // recogniser can report the language before the installer has
        // finished -- reading it unguarded took the whole app down.
        bool installed = finished && process!.ExitCode == 0;
        string outcome = present
            ? Strings.Get("Hint.OcrInstalled", engineTag)
            : installed
                ? Strings.Get("Hint.OcrInstalledNeedsRestart", capability)
                : Strings.Get("Hint.OcrInstallFailed", OcrSetup.CapabilityName(capability));
        if (present)
        {
            // The pack row has done its job and goes; the outcome moves up to
            // the game-language row, which is what stays on screen.
            OcrFixRow.Visibility = Visibility.Collapsed;
            OcrHint.Foreground = (Brush)FindResource("Ok");
            OcrHint.Text = outcome;
        }
        else
        {
            OcrPackDesc.Foreground = (Brush)FindResource(installed ? "Ok" : "Bad");
            OcrPackDesc.Text = outcome;
        }
        AppendLog(outcome);
    }

    private void OnOpenLanguageSettings(object sender, RoutedEventArgs e) =>
        OcrSetup.OpenLanguageSettings();

    /// <summary>The form as a Settings, or null when a number will not parse.</summary>
    private Settings? ReadForm()
    {
        if (!int.TryParse(WidthBox.Text, out int width) ||
            !int.TryParse(HeightBox.Text, out int height) ||
            !int.TryParse(ObsPortBox.Text, out int obsPort) ||
            !int.TryParse(WidgetPortBox.Text, out int widgetPort) ||
            !int.TryParse(RowsBox.Text, out int rows) ||
            !int.TryParse(MaxWidthBox.Text, out int maxWidth))
            return null;

        return new Settings
        {
            UiLanguage = Strings.Languages[Math.Max(0, UiLanguageBox.SelectedIndex)].Code,
            DebugMode = DebugModeSwitch.IsChecked == true,
            Locale = Settings.Locales[Math.Max(0, LocaleBox.SelectedIndex)].Code,
            ScreenWidth = width,
            ScreenHeight = height,
            ObsPort = obsPort,
            ObsPassword = ObsPasswordBox.Text.Trim(),
            ObsSource = ObsSourceBox.Text.Trim(),
            WidgetPort = widgetPort,
            WidgetRows = rows,
            WidgetMaxWidth = maxWidth,
        };
    }

    /// <summary>
    /// Make the button say what pressing it will do.
    ///
    /// It read "save and restart" no matter what was pending, so changing the
    /// window's language -- which is applied and saved the instant it is picked
    /// and is not read by the loop at all -- looked like it required tearing
    /// detection down mid-game. The label now follows the form.
    /// </summary>
    private void UpdateSaveButton()
    {
        var form = ReadForm();
        bool restart = form is null || form.NeedsRestartFrom(_running);
        SaveButton.Content = Strings.Get(restart ? "Settings.Save" : "Settings.SaveOnly");
    }

    private void OnSettingChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingSettings)
            UpdateSaveButton();
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        var form = ReadForm();
        if (form is null)
        {
            SavedHint.Foreground = (Brush)FindResource("Bad");
            SavedHint.Text = Strings.Get("Hint.NotANumber");
            return;
        }

        bool restart = form.NeedsRestartFrom(_running);
        _settings = form;
        _settings.Save();

        SavedHint.Foreground = (Brush)FindResource("Ok");
        AppendLog(Strings.Get("Log.SettingsSaved"));

        if (!restart)
        {
            // Nothing the loop reads has moved, and the pieces that did apply
            // themselves when they were changed. Restarting here would drop the
            // OBS connection and the augments taken so far to no purpose.
            SavedHint.Text = Strings.Get("Hint.SavedNoRestart");
            return;
        }

        SavedHint.Text = Strings.Get("Hint.Saved");
        _running = form;
        _lastPicksKey = null;
        _awaitingRestart = true;
        _runner.Restart();
        UpdateSaveButton();
    }

    private void OnResetSettings(object sender, RoutedEventArgs e) =>
        Confirm("Confirm.DefaultsTitle", "Confirm.DefaultsBody", "Settings.Defaults", RestoreDefaults);

    private void RestoreDefaults()
    {
        // The interface language is a preference about this window, not about
        // detection, so a settings reset leaves it alone.
        _settings = new Settings
        {
            UiLanguage = _settings.UiLanguage,
            DebugMode = _settings.DebugMode,
        };
        _settings.Save();
        _running = _settings;
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

    /// <summary>
    /// The status line: one glyph, one title, one line under it, and at most
    /// two buttons. Colour is the third channel, never the only one.
    /// </summary>
    private void SetStatus(string tone, string title, string detail,
                           string? primaryKey = null, string? secondaryKey = null, bool help = false)
    {
        StatusHelp.Visibility = help ? Visibility.Visible : Visibility.Collapsed;
        if (help)
        {
            // OBS in the same language as this window, so the words in the
            // picture are the words the person will see in OBS.
            var uri = new Uri($"pack://application:,,,/Assets/obs-websocket-menu.{Strings.Language}.png");
            if (HelpImage.Source is not BitmapImage { UriSource: { } current } || current != uri)
                HelpImage.Source = new BitmapImage(uri);
        }
        StatusGlyphBg.Background = (Brush)FindResource(tone + "Soft");
        StatusGlyph.Foreground = (Brush)FindResource(tone);
        // E73E check, E7BA warning, E8EA (a dot) for waiting, E783 for an error.
        StatusGlyph.Text = tone switch { "Ok" => "", "Bad" => "", "Warn" => "", _ => "" };
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        StatusDetail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        StatusAction.Visibility = primaryKey is null ? Visibility.Collapsed : Visibility.Visible;
        if (primaryKey is not null)
            StatusAction.Content = Strings.Get(primaryKey);
        StatusSecondary.Visibility = secondaryKey is null ? Visibility.Collapsed : Visibility.Visible;
        if (secondaryKey is not null)
            StatusSecondary.Content = Strings.Get(secondaryKey);
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
            _statusNote = null;
            WidgetUrlText.Text = _runner.Url;
            if (state.Connected && state.GameMode == Config.MayhemGameMode && state.CaptureBlank)
                SetStatus("Warn", Strings.Get("Status.CaptureBlank"),
                    Strings.Get("Status.CaptureBlankDetail", _running.ObsSource));
            else if (state.Connected && state.GameMode == Config.MayhemGameMode)
                SetStatus("Ok", Strings.Get("Status.Watching"),
                    state.Level is int lv
                        ? state.SourceWidth > 0
                            ? Strings.Get("Status.WatchingDetailSize", lv, _running.ObsSource,
                                          state.SourceWidth, state.SourceHeight)
                            : Strings.Get("Status.WatchingDetail", lv, _running.ObsSource)
                        : Strings.Get("Status.WatchingDetailNoLevel", _running.ObsSource));
            else if (state.Connected)
                SetStatus("Warn", Strings.Get("Status.ObsConnected"),
                    Strings.Get("Status.NotMayhemDetail", state.GameMode));
            else
                SetStatus("Ok", Strings.Get("Status.ObsConnected"), Strings.Get("Status.WaitingDetail"));
        }
        else if (running || _runner.IsBusy)
        {
            SetStatus("Dim", Strings.Get("Status.Starting"), Strings.Get("Status.StartingDetail"));
        }
        else
        {
            if (DateTime.UtcNow - _obsCheckedAt > TimeSpan.FromSeconds(3))
            {
                _obsCheckedAt = DateTime.UtcNow;
                _obsRunning = ObsSetup.ObsIsRunning();
            }
            if (_statusNote is { } note)
            {
                SetStatus("Bad", Strings.Get("Status.ObsDisconnected"), note.Text, "Action.Retry");
                StatusDetail.Foreground = (Brush)FindResource(note.Brush);
            }
            else if (_obsRunning)
            {
                SetStatus("Bad", Strings.Get("Status.ObsDisconnected"),
                    Strings.Get("Status.ObsDownRunning"), "Action.Retry", help: true);
            }
            else
            {
                SetStatus("Bad", Strings.Get("Status.ObsDisconnected"),
                    Strings.Get("Status.ObsDownOff"), "Action.TurnOn", "Action.Retry");
            }
        }
        if (_statusNote is null)
            StatusDetail.Foreground = (Brush)FindResource("Dim");

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
            string tone = pick.Rarity switch
            {
                "silver" => "Silver",
                "gold" => "Gold",
                "prismatic" => "Prism",
                _ => "Dim",
            };
            _picks.Add(new PickRow
            {
                Name = pick.Name,
                Rarity = Strings.Get($"Rarity.{pick.Rarity}"),
                Level = pick.Level is int level ? Strings.Get("Pick.Level", level) : "",
                Detail = Config.DebugMode && pick.Confidence > 0
                    ? $"  ·  {pick.Via} {pick.Confidence:0.00}" : "",
                RarityBrush = (Brush)FindResource(tone),
                RaritySoftBrush = (Brush)FindResource(tone + "Soft"),
                Icon = LoadIcon(pick.IconUrl),
            });
        }
        CountText.Text = picks.Count.ToString();
        EmptyPanel.Visibility = picks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The CDN glyph, fetched in the background; a bad URL or an
    /// offline machine just leaves the square blank.</summary>
    private static ImageSource? LoadIcon(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            return image;
        }
        catch
        {
            return null;
        }
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
