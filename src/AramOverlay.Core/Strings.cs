using System.Globalization;

namespace AramOverlay.Core;

/// <summary>
/// Every string a person reads, in one table.
///
/// The four languages sit on one line per key so a translation can be checked
/// against the others without hunting through four files. Composite format
/// arguments keep their numbering across languages, which is the only thing a
/// translator has to be careful about.
///
/// Korean is the source language: it is what the tool was written in and what
/// every measurement in docs/FINDINGS.md is phrased against. A missing entry in
/// another language falls back to Korean rather than showing a bare key.
/// </summary>
public static class Strings
{
    public static readonly (string Code, string Name)[] Languages =
    {
        ("ko", "한국어"), ("en", "English"), ("ja", "日本語"), ("zh", "简体中文"),
    };

    private static readonly Dictionary<string, string[]> Table = new();
    private static int _index;      // into the Languages array

    public static string Language
    {
        get => Languages[_index].Code;
        set
        {
            int found = Array.FindIndex(Languages, l => l.Code == value);
            _index = found < 0 ? 0 : found;
        }
    }

    /// <summary>
    /// The window's language, taken from the Windows display language.
    ///
    /// Falling back to English rather than Korean: a player whose language this
    /// app has no words for is far more likely to read English than Korean, and
    /// the Korean default was only ever the author's own machine showing through.
    /// </summary>
    public static string SystemDefault()
    {
        string tag = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Array.Exists(Languages, l => l.Code == tag) ? tag : "en";
    }

    public static string Get(string key)
    {
        if (!Table.TryGetValue(key, out var row))
            return key;                                  // a key with no entry is a bug, show it
        string text = row[_index];
        return text.Length > 0 ? text : row[0];          // fall back to Korean
    }

    public static string Get(string key, params object?[] args) =>
        string.Format(Get(key), args);

    /// <summary>
    /// One string in a named language rather than the interface one.
    ///
    /// The widget is the case this exists for. It sits on the broadcast beside
    /// augment names fetched in the client's language, so its own words belong
    /// to that language too -- a Japanese client with a Korean interface was
    /// putting 歯の妖精 on stream under "프리즘" and "11레벨".
    ///
    /// A client language this app does not speak falls back to English, not to
    /// Korean: the fallback lands next to the augment names on someone's stream,
    /// where the more widely readable of the two wins.
    /// </summary>
    public static string In(string code, string key, params object?[] args)
    {
        if (!Table.TryGetValue(key, out var row))
            return key;
        int index = Array.FindIndex(Languages, l => l.Code == code);
        if (index < 0)
            index = Array.FindIndex(Languages, l => l.Code == "en");
        string text = row[index];
        if (text.Length == 0)
            text = row[0];
        return args.Length == 0 ? text : string.Format(text, args);
    }

    private static void Add(string key, string ko, string en, string ja, string zh) =>
        Table[key] = new[] { ko, en, ja, zh };

    static Strings()
    {
        _index = 0;

        // ---- window chrome ----------------------------------------------
        Add("App.Title", "아수라장 증강 오버레이", "ARAM Mayhem Augment Overlay",
            "ランダムミッド: メイヘム オーグメント オーバーレイ", "海克斯大乱斗 强化符文覆盖层");
        Add("Chrome.Minimise", "최소화", "Minimise", "最小化", "最小化");
        Add("Chrome.Maximise", "최대화", "Maximise", "最大化", "最大化");
        Add("Chrome.Restore", "이전 크기로", "Restore", "元のサイズに戻す", "还原");
        Add("Chrome.HideToTray", "트레이로 내리기 (종료하려면 종료 버튼)",
            "Hide to tray (use Quit to actually stop it)",
            "トレイに収納（終了するには終了ボタン）", "收进托盘（真正退出请用退出按钮）");

        // ---- navigation and shared actions -------------------------------
        Add("Nav.Status", "상태", "Status", "状態", "状态");
        Add("Nav.Settings", "설정", "Settings", "設定", "设置");
        Add("Action.CopyUrl", "주소 복사", "Copy address", "アドレスをコピー", "复制地址");
        Add("Action.Reset", "목록 초기화", "Clear list", "リストを消去", "清空列表");
        Add("Action.Retry", "다시 시도", "Retry", "再試行", "重试");
        Add("Action.Quit", "종료", "Quit", "終了", "退出");
        Add("Action.Open", "열기", "Open", "開く", "打开");
        Add("Action.Cancel", "취소", "Cancel", "キャンセル", "取消");
        Add("Hint.Copied", "복사되었습니다", "Copied", "コピーしました", "已复制");
        Add("Hint.CopyNotReady", "아직 주소가 없습니다 — OBS에 연결되면 생깁니다",
            "No address yet — it appears once OBS is connected",
            "まだアドレスがありません — OBS に接続すると表示されます",
            "尚无地址 — 连接 OBS 后会出现");
        Add("Hint.CopyFailed", "복사하지 못했습니다. 다시 눌러 주세요",
            "Could not copy. Press again",
            "コピーできませんでした。もう一度押してください", "复制失败，请再按一次");
        Add("Action.TurnOn", "켜기", "Turn it on", "有効にする", "开启");

        // ---- status ------------------------------------------------------
        Add("Status.WidgetPending", "위젯 주소 준비 중", "Widget address pending",
            "ウィジェットのアドレスを準備中", "挂件地址准备中");
        Add("Status.Starting", "시작하는 중", "Starting", "起動中", "启动中");
        Add("Status.ObsConnected", "OBS 연결됨", "OBS connected", "OBS 接続済み", "已连接 OBS");
        Add("Status.ObsDisconnected", "OBS 연결 안 됨", "OBS not connected",
            "OBS 未接続", "未连接 OBS");
        Add("Status.WaitingForGame", "게임 대기 중", "Waiting for a game",
            "ゲーム待機中", "等待对局");
        Add("Status.GameDetected", "게임 감지", "Game detected", "ゲーム検出", "已检测到对局");
        Add("Status.GameDetectedLevel", "게임 감지 (Lv {0})", "Game detected (Lv {0})",
            "ゲーム検出（Lv {0}）", "已检测到对局（Lv {0}）");
        Add("Status.NotMayhem", "{0} — 감지 안 함", "{0} — not watched",
            "{0} — 検出しません", "{0} — 不进行检测");
        Add("Status.PickCount", "획득한 증강 {0}개", "{0} augments taken",
            "獲得したオーグメント {0} 個", "已获得 {0} 个强化符文");
        Add("Status.Taken", "획득한 증강", "Augments taken", "獲得したオーグメント", "已获得的强化符文");
        Add("Status.NoPicks", "아직 없습니다", "Nothing yet", "まだありません", "暂无");
        // No level list: one game handed out picks at 3, 9 and 12, so the
        // schedule is the game's to state, not this window's.
        Add("Status.NoPicksDetail",
            "증강을 고르면 여기에 쌓입니다.",
            "Augments land here as you take them.",
            "オーグメントを選ぶとここに並びます。",
            "选取的强化符文会出现在这里。");

        // ---- the status line: one title, one line under it -----------------
        Add("Status.Watching", "아수라장 게임 감지 중", "Watching an ARAM Mayhem game",
            "ランダムミッド: メイヘムのゲームを監視中", "正在监视海克斯大乱斗对局");
        Add("Status.WatchingDetail", "레벨 {0} · OBS 소스 '{1}' 에 연결됨",
            "Level {0} · connected to OBS as “{1}”",
            "レベル {0} · OBS の「{1}」に接続済み", "{0} 级 · 已连接 OBS 的“{1}”");
        Add("Status.WatchingDetailNoLevel", "OBS 소스 '{0}' 에 연결됨",
            "Connected to OBS as “{0}”", "OBS の「{0}」に接続済み", "已连接 OBS 的“{0}”");
        Add("Status.WaitingDetail", "아수라장 게임이 시작되면 증강을 읽기 시작합니다.",
            "Augments are read once an ARAM Mayhem game starts.",
            "ランダムミッド: メイヘムのゲームが始まるとオーグメントを読み取ります。",
            "海克斯大乱斗对局开始后即开始读取强化符文。");
        Add("Status.NotMayhemDetail", "{0} 모드는 감지하지 않습니다. 아수라장에서만 동작합니다.",
            "{0} is not watched. Only ARAM Mayhem is.",
            "{0} は監視しません。ランダムミッド: メイヘムのみ対応です。",
            "不监视 {0} 模式，仅支持海克斯大乱斗。");
        Add("Status.StartingDetail", "OBS에 연결하는 중입니다.", "Connecting to OBS.",
            "OBS に接続しています。", "正在连接 OBS。");
        Add("Status.ObsDownOff",
            "OBS가 꺼져 있거나 websocket 서버가 꺼져 있습니다. 서버를 켜려면 OBS가 종료된 상태여야 합니다.",
            "OBS is not running, or its websocket server is off. Turning it on needs OBS closed.",
            "OBS が起動していないか、websocket サーバーが無効です。有効化には OBS を閉じておく必要があります。",
            "OBS 未运行，或其 websocket 服务器已关闭。开启时需要先关闭 OBS。");
        Add("Status.ObsDownRunning",
            "OBS는 실행 중이지만 응답이 없습니다. 도구 › WebSocket 서버 설정과 비밀번호를 확인하세요.",
            "OBS is running but did not answer. Check Tools › WebSocket Server Settings and the password.",
            "OBS は起動中ですが応答がありません。ツール › WebSocket サーバー設定とパスワードを確認してください。",
            "OBS 正在运行但没有响应。请检查 工具 › WebSocket 服务器设置 和密码。");

        Add("Help.ObsWebsocket",
            "OBS 상단 메뉴 도구 › WebSocket 서버 설정에서 '서버 활성화'를 켜고, 비밀번호가 설정 탭과 같은지 확인하세요.",
            "In OBS, open Tools › WebSocket Server Settings, turn on “Enable WebSocket server”, and check the password matches the Settings tab.",
            "OBS のメニュー ツール › WebSocket サーバー設定 で「WebSocket サーバーを有効にする」をオンにし、パスワードが設定タブと同じか確認してください。",
            "在 OBS 顶部菜单 工具 › WebSocket 服务器设置 中开启“启用 WebSocket 服务器”，并确认密码与设置标签页一致。");

        // ---- confirmations, for the two things that cannot be undone -------
        Add("Confirm.QuitTitle", "오버레이를 종료할까요?", "Quit the overlay?",
            "オーバーレイを終了しますか？", "要退出覆盖层吗？");
        Add("Confirm.QuitBody",
            "감지가 멈추고 트레이 아이콘도 사라집니다. 창만 닫으려면 제목 표시줄의 ✕를 누르세요.",
            "Detection stops and the tray icon goes with it. To just close the window, use ✕ in the title bar.",
            "検出が止まり、トレイアイコンも消えます。ウィンドウだけ閉じるにはタイトルバーの ✕ を使ってください。",
            "检测会停止，托盘图标也会消失。若只想关闭窗口，请使用标题栏的 ✕。");
        Add("Confirm.DefaultsTitle", "설정을 기본값으로 되돌릴까요?", "Restore default settings?",
            "設定を既定値に戻しますか？", "要恢复默认设置吗？");
        Add("Confirm.DefaultsBody",
            "OBS 비밀번호와 포트, 해상도, 위젯 설정이 모두 초기화되고 감지가 다시 시작됩니다. 프로그램 언어는 유지됩니다.",
            "The OBS password and ports, the resolution and the widget settings all reset, and detection restarts. The interface language stays.",
            "OBS のパスワードとポート、解像度、ウィジェット設定がすべて初期化され、検出が再起動します。表示言語は保持されます。",
            "OBS 密码与端口、分辨率、挂件设置都会重置，检测将重新启动。界面语言保持不变。");

        // ---- rarities ----------------------------------------------------
        Add("Rarity.silver", "실버", "Silver", "シルバー", "白银");
        Add("Rarity.gold", "골드", "Gold", "ゴールド", "黄金");
        Add("Rarity.prismatic", "프리즘", "Prismatic", "プリズム", "棱彩");
        Add("Rarity.unknown", "미상", "Unknown", "不明", "未知");
        Add("Rarity.WithLevel", "{0} · {1}레벨", "{0} · level {1}",
            "{0} · レベル {1}", "{0} · {1} 级");

        // ---- the OBS widget page ------------------------------------------
        // The overlay is the thing on the broadcast, so it follows the same
        // language as the window rather than staying in the source language.
        Add("Widget.Reset", "초기화", "Clear", "クリア", "清空");
        Add("Widget.ResetTitle", "증강 목록 비우기", "Clear the augment list",
            "オーグメントのリストを消去", "清空强化符文列表");

        // ---- settings: sections and fields --------------------------------
        Add("Settings.Language", "언어", "Language", "言語", "语言");
        Add("Settings.UiLanguage", "프로그램 언어", "Interface", "表示言語", "界面语言");
        Add("Settings.GameLanguage", "게임 언어", "Game language", "ゲームの言語", "游戏语言");
        Add("Settings.OcrPack", "OCR 언어 팩", "OCR language pack", "OCR 言語パック", "OCR 语言包");
        Add("Settings.Game", "게임", "Game", "ゲーム", "游戏");
        Add("Settings.Resolution", "해상도", "Resolution", "解像度", "分辨率");
        Add("Settings.GameResolution", "게임 해상도", "Game resolution", "ゲーム解像度", "游戏分辨率");
        Add("Settings.Obs", "OBS", "OBS", "OBS", "OBS");
        Add("Settings.Websocket", "websocket 서버", "Websocket server", "websocket サーバー", "websocket 服务器");
        Add("Hint.WebsocketOnce", "한 번만 하면 됩니다. OBS를 종료한 상태에서 켜세요.",
            "Only needed once. OBS has to be closed while it is switched on.",
            "一度だけで済みます。OBS を閉じた状態で有効にしてください。",
            "只需一次。开启时请先关闭 OBS。");
        Add("Settings.ObsPort", "websocket 포트", "websocket port", "websocket ポート", "websocket 端口");
        Add("Settings.ObsSource", "게임 캡처 소스", "Game capture source",
            "ゲームキャプチャソース", "游戏采集源");
        Add("Settings.ObsPassword", "비밀번호", "Password", "パスワード", "密码");
        Add("Settings.Overlay", "오버레이", "Overlay", "オーバーレイ", "覆盖层");
        Add("Settings.WidgetPort", "위젯 포트", "Widget port", "ウィジェットのポート", "挂件端口");
        Add("Settings.Rows", "표시 행 수", "Rows shown", "表示行数", "显示行数");
        Add("Settings.MaxWidth", "최대 폭 (px)", "Maximum width (px)", "最大幅 (px)", "最大宽度 (px)");
        Add("Settings.Save", "저장하고 다시 시작", "Save and restart", "保存して再起動", "保存并重启");
        Add("Settings.SaveOnly", "저장", "Save", "保存", "保存");
        Add("Settings.Defaults", "기본값으로", "Restore defaults", "既定値に戻す", "恢复默认值");
        Add("Settings.EnableWebsocket", "OBS websocket 서버 켜기", "Turn on the OBS websocket server",
            "OBS の websocket サーバーを有効化", "打开 OBS websocket 服务器");

        Add("Settings.Advanced", "고급", "Advanced", "詳細", "高级");
        Add("Settings.DebugMode", "디버그 로그 보기", "Show the debug log",
            "デバッグログを表示", "显示调试日志");
        Add("Hint.DebugMode",
            "감지 과정을 그대로 찍습니다. 증강이 기록되지 않았을 때 어디서 갈렸는지 볼 때만 켜세요.",
            "Prints the detection reasoning. Turn it on only when an augment went unrecorded and you want to see where it went wrong.",
            "検出の過程をそのまま出力します。オーグメントが記録されなかった原因を調べるときだけ有効にしてください。",
            "会原样输出检测过程。只在某个强化符文没有被记录、想查清原因时才打开。");

        // The tiles show what each one looks like, so these are names and
        // nothing else. Copy explaining a choice the picture already makes is
        // the kind of line that reads as an excuse on screen.
        Add("Settings.WidgetTheme", "위젯 테마", "Widget theme", "ウィジェットのテーマ", "挂件主题");
        Add("Theme.HudTray", "HUD 트레이", "HUD tray", "HUD トレイ", "HUD 托盘");
        Add("Theme.Strip", "한 줄", "One strip", "1 行", "单行条");
        Add("Theme.Palette", "게임 팔레트", "Game palette", "ゲームパレット", "游戏配色");
        // The previews name the rarities rather than inventing augment names, so
        // each tile doubles as the colour legend and nothing in it has to be
        // made up. These two are the level line under them.
        Add("Theme.SampleLevelA", "3레벨", "Level 3", "レベル 3", "3 级");
        Add("Theme.SampleLevelB", "12레벨", "Level 12", "レベル 12", "12 级");

        Add("Settings.Inspector", "판정 기록 남기기", "Record how each pick was decided",
            "判定の記録を残す", "记录每次判定过程");
        Add("Hint.Inspector",
            "증강 선택창이 열린 동안 화면과 계측값을 모두 남기고, {0} 에서 프레임 단위로 되돌려 봅니다. 최근 8개 창을 보관하며 창 하나가 15~25MB 입니다.",
            "Keeps the screen and every measurement while an augment window is open, to step through frame by frame at {0}. The last 8 windows are kept, at 15-25 MB each.",
            "オーグメント選択画面が開いている間の画面と計測値をすべて保存し、{0} でコマ送りできます。直近 8 件を保持し、1 件あたり 15〜25MB です。",
            "在强化符文选择界面开启期间保存画面与全部测量值，可在 {0} 逐帧回看。保留最近 8 次，每次 15~25MB。");
        Add("Settings.OpenInspector", "열기", "Open", "開く", "打开");

        // ---- settings: hints ----------------------------------------------
        Add("Hint.PasswordBlank", "비워 두면 OBS 설정 파일에서 자동으로 읽습니다.",
            "Leave blank to read it from the OBS config file.",
            "空欄にすると OBS の設定ファイルから自動で読み取ります。",
            "留空则从 OBS 配置文件自动读取。");
        Add("Hint.ReadsWith",
            "증강 이름을 이 언어로 받아오고, 화면도 '{0}' 로 읽습니다.",
            "Augment names come in this language, and the screen is read with {0}.",
            "オーグメント名をこの言語で取得し、画面も {0} で読み取ります。",
            "以该语言获取强化符文名称，并用 {0} 读取画面。");
        Add("Action.InstallOcr", "OCR 언어 팩 설치", "Install the OCR language pack",
            "OCR 言語パックをインストール", "安装 OCR 语言包");
        Add("Action.OpenLanguageSettings", "언어 설정 열기", "Open language settings",
            "言語設定を開く", "打开语言设置");
        Add("Hint.OcrElevating",
            "Windows가 관리자 권한을 묻습니다 — [예]를 눌러 주세요.",
            "Windows will ask for administrator permission — choose Yes.",
            "Windows が管理者権限を求めます — [はい] を選んでください。",
            "Windows 会请求管理员权限 — 请选择“是”。");
        Add("Hint.OcrInstallingFor",
            "'{0}' 설치 중입니다 ({1} 경과). 몇 분 걸리며, 창을 닫지 마세요.",
            "Installing {0} ({1} elapsed). This takes a few minutes; leave the window open.",
            "{0} をインストール中です（{1} 経過）。数分かかります。ウィンドウは閉じないでください。",
            "正在安装 {0}（已用 {1}）。需要几分钟，请不要关闭窗口。");
        Add("Hint.OcrPackRow", "Windows에서 받아옵니다. 관리자 권한을 한 번 묻습니다.",
            "Downloaded from Windows. Asks for administrator permission once.",
            "Windows から取得します。管理者権限を一度求めます。",
            "从 Windows 下载。会请求一次管理员权限。");
        Add("Hint.OcrInstalling", "설치 중입니다. 몇 분 걸릴 수 있습니다...",
            "Installing. This can take a few minutes...",
            "インストール中です。数分かかることがあります...", "正在安装，可能需要几分钟...");
        Add("Hint.OcrInstalled", "'{0}' 설치 완료. 저장하고 다시 시작을 눌러 주세요.",
            "{0} installed. Press Save and restart.",
            "{0} をインストールしました。保存して再起動を押してください。",
            "已安装 {0}。请点击保存并重启。");
        Add("Hint.OcrInstalledNeedsRestart",
            "'{0}' 설치가 끝났지만 아직 인식되지 않습니다. 프로그램을 다시 시작해 확인하세요.",
            "{0} finished installing but is not being reported yet. Restart the app to check.",
            "{0} のインストールは終わりましたが、まだ認識されていません。アプリを再起動して確認してください。",
            "{0} 已安装完成，但尚未被识别。请重启程序后确认。");
        Add("Hint.OcrInstallDeclined", "설치를 취소했습니다. 관리자 권한이 필요합니다.",
            "Installation was cancelled. It needs administrator rights.",
            "インストールをキャンセルしました。管理者権限が必要です。",
            "已取消安装。此操作需要管理员权限。");
        Add("Hint.OcrInstallFailed",
            "설치하지 못했습니다. 설정 앱에서 언어를 추가하거나, 관리자 PowerShell에서\nAdd-WindowsCapability -Online -Name '{0}'",
            "Could not install it. Add the language in Settings, or in an admin PowerShell:\nAdd-WindowsCapability -Online -Name '{0}'",
            "インストールできませんでした。設定アプリで言語を追加するか、管理者 PowerShell で:\nAdd-WindowsCapability -Online -Name '{0}'",
            "安装失败。请在设置中添加语言，或在管理员 PowerShell 中执行:\nAdd-WindowsCapability -Online -Name '{0}'");
        Add("Hint.OcrPackMissing",
            "'{0}' OCR 언어 팩이 없습니다. 관리자 PowerShell에서\nAdd-WindowsCapability -Online -Name 'Language.OCR~~~{0}~0.0.1.0'",
            "The {0} OCR language pack is missing. In an admin PowerShell:\nAdd-WindowsCapability -Online -Name 'Language.OCR~~~{0}~0.0.1.0'",
            "{0} の OCR 言語パックがありません。管理者 PowerShell で:\nAdd-WindowsCapability -Online -Name 'Language.OCR~~~{0}~0.0.1.0'",
            "缺少 {0} 的 OCR 语言包。请在管理员 PowerShell 中执行:\nAdd-WindowsCapability -Online -Name 'Language.OCR~~~{0}~0.0.1.0'");
        Add("Hint.ThisScreen", "이 PC 화면: {0}×{1}, 배율 {2}%",
            "This screen: {0}×{1} at {2}% scaling",
            "この PC の画面: {0}×{1}、拡大率 {2}%", "本机屏幕: {0}×{1}，缩放 {2}%");
        Add("Hint.ScreenUnknown", "화면 정보를 읽지 못했습니다", "Could not read the screen size",
            "画面情報を取得できませんでした", "无法读取屏幕信息");
        Add("Hint.NumbersOnly", "숫자를 입력하세요.", "Enter a number.",
            "数値を入力してください。", "请输入数字。");
        Add("Hint.NotANumber", "숫자 칸에 숫자가 아닌 값이 있습니다.",
            "One of the number fields is not a number.",
            "数値欄に数値でない値があります。", "数字栏中有非数字的值。");
        // The geometry follows the frame on its own; the setting only decides
        // how large a frame the text is read from. Said as what happens, not
        // as how it is done.
        Add("Hint.Ratio169", "16:9 — 카드 위치는 자동으로 맞춰지고, 글자는 이 크기로 읽습니다.",
            "16:9 — card positions follow automatically; text is read at this size.",
            "16:9 — カード位置は自動で合わせ、文字はこのサイズで読み取ります。",
            "16:9 — 卡片位置自动匹配，文字按此尺寸读取。");
        Add("Hint.NotRatio169",
            "16:9가 아니어도 카드 위치는 화면 높이에 맞춰 따라갑니다.",
            "Not 16:9; card positions still follow the screen height.",
            "16:9 でなくてもカード位置は画面の高さに合わせて追従します。",
            "即使不是 16:9，卡片位置也会按屏幕高度跟随。");
        Add("Pick.Level", "{0}레벨", "Level {0}", "レベル {0}", "{0} 级");
        Add("Hint.Saved", "저장했습니다. 다시 시작합니다...", "Saved. Restarting...",
            "保存しました。再起動します...", "已保存，正在重启...");
        Add("Hint.Restarted", "다시 시작했습니다.", "Restarted.", "再起動しました。", "已重启。");
        Add("Hint.SavedNoRestart", "저장했습니다. 다시 시작할 필요는 없습니다.",
            "Saved. No restart needed.", "保存しました。再起動は不要です。", "已保存，无需重启。");
        Add("Hint.RestartFailed", "다시 시작하지 못했습니다. 상태 탭의 로그를 확인하세요.",
            "Could not restart. Check the log on the Status tab.",
            "再起動できませんでした。状態タブのログを確認してください。",
            "重启失败。请查看状态标签页的日志。");
        Add("Hint.DefaultsRestored", "기본값으로 되돌렸습니다. 다시 시작합니다...",
            "Restored to defaults. Restarting...",
            "既定値に戻しました。再起動します...", "已恢复默认值，正在重启...");

        // ---- log lines the window prints itself ---------------------------
        Add("Log.UrlCopied", "위젯 주소를 복사했습니다: {0}", "Widget address copied: {0}",
            "ウィジェットのアドレスをコピーしました: {0}", "已复制挂件地址: {0}");
        Add("Log.UrlNotReady",
            "아직 위젯 주소가 없습니다 — 오버레이가 시작된 뒤에 복사할 수 있습니다.",
            "There is no widget address yet -- it can be copied once the overlay has started.",
            "まだウィジェットのアドレスがありません — オーバーレイの開始後にコピーできます。",
            "尚无挂件地址 — 覆盖层启动后即可复制。");
        Add("Log.UrlCopyFailed",
            "주소를 복사하지 못했습니다 ({0}). 다른 프로그램이 클립보드를 잡고 있을 수 있습니다: {1}",
            "Could not copy the address ({0}). Another program may be holding the clipboard: {1}",
            "アドレスをコピーできませんでした（{0}）。他のプログラムがクリップボードを保持している可能性があります: {1}",
            "无法复制地址（{0}）。可能有其他程序正占用剪贴板: {1}");
        Add("Log.ListCleared", "목록을 비웠습니다.", "List cleared.",
            "リストを消去しました。", "已清空列表。");
        Add("Log.Retrying", "다시 시도합니다...", "Retrying...", "再試行します...", "正在重试...");
        Add("Log.SettingsSaved", "설정을 저장했습니다. 다시 시작합니다...",
            "Settings saved. Restarting...",
            "設定を保存しました。再起動します...", "设置已保存，正在重启...");
        Add("Log.DefaultsRestored", "설정을 기본값으로 되돌렸습니다.", "Settings restored to defaults.",
            "設定を既定値に戻しました。", "设置已恢复为默认值。");

        // ---- tray ---------------------------------------------------------
        Add("Tray.HiddenTitle", "아수라장 증강 오버레이", "ARAM Mayhem Augment Overlay",
            "ランダムミッド: メイヘム オーグメント オーバーレイ", "海克斯大乱斗 强化符文覆盖层");
        Add("Tray.HiddenBody", "트레이에서 계속 실행 중입니다. 아이콘을 두 번 누르면 다시 열립니다.",
            "Still running in the tray. Double-click the icon to bring it back.",
            "トレイで実行を続けています。アイコンをダブルクリックすると戻ります。",
            "仍在托盘中运行。双击图标即可重新打开。");

        // ---- startup, OBS and OCR -----------------------------------------
        Add("Core.LoadingAugments", "증강 데이터 불러오는 중...", "Loading augment data...",
            "オーグメントデータを読み込み中...", "正在加载强化符文数据...");
        Add("Core.AugmentCount", "  증강 {0}종", "  {0} augments",
            "  オーグメント {0} 種", "  强化符文 {0} 种");
        Add("Core.PasswordFromConfig", "OBS 설정 파일에서 websocket 비밀번호를 읽었습니다.",
            "Read the websocket password from the OBS config file.",
            "OBS の設定ファイルから websocket のパスワードを読み取りました。",
            "已从 OBS 配置文件读取 websocket 密码。");
        Add("Core.ObsConnectFailed", "OBS 연결 실패: {0}", "Could not connect to OBS: {0}",
            "OBS への接続に失敗しました: {0}", "连接 OBS 失败: {0}");
        Add("Core.ObsCheckHint",
            "  OBS가 실행 중인지, 도구 > WebSocket 서버 설정에서 서버가 켜져 있는지 확인하세요.",
            "  Check that OBS is running and its websocket server is on (Tools > WebSocket Server Settings).",
            "  OBS が起動しているか、ツール > WebSocket サーバー設定でサーバーが有効か確認してください。",
            "  请确认 OBS 正在运行，且在 工具 > WebSocket 服务器设置 中已启用服务器。");
        Add("Core.GameCaptureCreated", "게임 캡처 소스 '{0}' 를 새로 만들었습니다.",
            "Created the game capture source {0}.",
            "ゲームキャプチャソース {0} を作成しました。", "已创建游戏采集源 {0}。");
        Add("Core.SourceMissing", "소스 '{0}' 를 찾을 수 없습니다.", "Source {0} not found.",
            "ソース {0} が見つかりません。", "找不到来源 {0}。");
        Add("Core.GameCaptureRepaired", "게임 캡처 소스 '{0}' 의 창을 '{1}' 로 바꿨습니다.",
            "Pointed the game capture source {0} at {1}.",
            "ゲームキャプチャソース {0} のウィンドウを {1} にしました。",
            "已将游戏采集源 {0} 的窗口设为 {1}。");
        Add("Loop.CaptureBlank", "OBS 소스 '{0}' 가 검은 화면만 보냅니다. 게임 창을 다시 잡습니다.",
            "Source {0} is sending only black frames; re-pointing it at the game window.",
            "ソース {0} が黒い画面しか送ってきません。ゲームウィンドウを再指定します。",
            "来源 {0} 只发送黑屏，正在重新指向游戏窗口。");
        Add("Loop.CaptureBack", "게임 화면이 들어옵니다.", "Game picture is coming in.",
            "ゲーム画面が届いています。", "已收到游戏画面。");
        Add("Loop.SourceSize", "게임 화면 {0}×{1} — 감지 프레임 {2}×{3}",
            "Game picture is {0}×{1}; detecting on {2}×{3}",
            "ゲーム画面 {0}×{1} — 検出フレーム {2}×{3}", "游戏画面 {0}×{1} — 检测帧 {2}×{3}");
        Add("Status.WatchingDetailSize", "레벨 {0} · OBS 소스 '{1}' · 게임 화면 {2}×{3}",
            "Level {0} · connected to OBS as “{1}” · game at {2}×{3}",
            "レベル {0} · OBS の「{1}」に接続済み · ゲーム画面 {2}×{3}",
            "{0} 级 · 已连接 OBS 的“{1}” · 游戏画面 {2}×{3}");
        Add("Status.CaptureBlank", "OBS에 게임 화면이 없습니다", "OBS has no game picture",
            "OBS にゲーム画面がありません", "OBS 中没有游戏画面");
        Add("Status.CaptureBlankDetail",
            "게임 캡처 소스 '{0}' 의 창을 'League of Legends (TM) Client' 로 고르세요.",
            "Pick “League of Legends (TM) Client” as the window of the game capture source “{0}”.",
            "ゲームキャプチャソース「{0}」のウィンドウに「League of Legends (TM) Client」を選んでください。",
            "请在游戏采集源“{0}”的窗口中选择“League of Legends (TM) Client”。");
        Add("Core.OcrPreparing", "OCR 준비 중...", "Preparing OCR...", "OCR を準備中...", "正在准备 OCR...");
        Add("Core.WidgetUrl",
            "위젯 주소: {0}   <- OBS 브라우저 소스에 이 주소를 넣으세요 (크기는 내용에 맞춰 자동 조정됩니다)",
            "Widget address: {0}   <- put this in an OBS browser source (it resizes itself)",
            "ウィジェットのアドレス: {0}   <- OBS のブラウザソースにこのアドレスを入れてください（サイズは自動調整されます）",
            "挂件地址: {0}   <- 请填入 OBS 浏览器源（尺寸会自动调整）");
        Add("Core.BrowserRefreshed", "브라우저 소스 새로고침: {0}", "Browser sources refreshed: {0}",
            "ブラウザソースを更新しました: {0}", "已刷新浏览器源: {0}");
        Add("Core.MayhemPool", "  그중 아수라장 풀 {0}종 (가산점, 후보를 걸러내지는 않습니다)",
            "  {0} of them are in the Mayhem pool (ranked up, never filtered out)",
            "  うち アスラ場 プール {0} 種（加点のみで、候補は除外しません）",
            "  其中 {0} 个属于大乱斗池（仅加分，不排除候选）");
        Add("Loop.PicksRestored", "진행 중이던 게임의 증강 {0}개를 복원했습니다.",
            "Restored {0} augments from the game already in progress.",
            "進行中だったゲームの増強 {0} 個を復元しました。",
            "已恢复进行中对局的 {0} 个增强。");
        Add("Core.StartFailed", "오버레이가 시작되지 못했습니다.", "The overlay could not start.",
            "オーバーレイを開始できませんでした。", "覆盖层未能启动。");
        Add("Core.CrashedWith", "오류로 중단됐습니다: {0}", "Stopped with an error: {0}",
            "エラーで停止しました: {0}", "因错误而停止: {0}");
        Add("Core.Quitting", "종료합니다.", "Shutting down.", "終了します。", "正在退出。");
        Add("Ocr.LanguageMissing",
            "Windows OCR에 '{0}' 언어가 없습니다.\n    현재 사용 가능한 언어: {1}\n    설정 탭에서 사용 가능한 언어를 고르거나,\n    관리자 PowerShell에서 다음을 실행한 뒤 다시 시도하세요:\n    Add-WindowsCapability -Online -Name 'Language.OCR~~~{0}~0.0.1.0'",
            "Windows OCR has no {0} language.\n    Available now: {1}\n    Pick one of those on the Settings tab, or run this in an admin PowerShell and retry:\n    Add-WindowsCapability -Online -Name 'Language.OCR~~~{0}~0.0.1.0'",
            "Windows OCR に {0} 言語がありません。\n    現在利用可能: {1}\n    設定タブで利用可能な言語を選ぶか、管理者 PowerShell で次を実行して再試行してください:\n    Add-WindowsCapability -Online -Name 'Language.OCR~~~{0}~0.0.1.0'",
            "Windows OCR 中没有 {0} 语言。\n    当前可用: {1}\n    请在设置标签页选择可用语言，或在管理员 PowerShell 中执行以下命令后重试:\n    Add-WindowsCapability -Online -Name 'Language.OCR~~~{0}~0.0.1.0'");
        Add("Ocr.CallFailed", "  OCR 호출 실패: {0}: {1}", "  OCR call failed: {0}: {1}",
            "  OCR 呼び出しに失敗: {0}: {1}", "  OCR 调用失败: {0}: {1}");
        Add("Ocr.None", "(없음)", "(none)", "(なし)", "(无)");

        // ---- OBS websocket setup ------------------------------------------
        Add("ObsSetup.NoConfig",
            "OBS 설정 파일이 없습니다.\nOBS를 한 번 실행하고 '자동 구성 마법사'를 닫은 뒤 다시 시도하세요.\n(마법사가 떠 있는 동안에는 OBS가 설정 파일을 저장하지 않습니다.)",
            "There is no OBS config file yet.\nStart OBS once, dismiss the Auto-Configuration Wizard, then try again.\n(While that wizard is open, OBS does not save its config at all.)",
            "OBS の設定ファイルがありません。\nOBS を一度起動して自動構成ウィザードを閉じてから、もう一度お試しください。\n（ウィザードが開いている間、OBS は設定ファイルを保存しません。）",
            "还没有 OBS 配置文件。\n请先启动一次 OBS 并关闭自动配置向导，然后重试。\n（向导开着时 OBS 根本不会保存配置。）");
        Add("ObsSetup.ObsRunning",
            "OBS가 실행 중입니다. 종료할 때 설정 파일을 덮어쓰므로 먼저 OBS를 닫아주세요.",
            "OBS is running. It overwrites this file on exit, so close OBS first.",
            "OBS が起動中です。終了時に設定ファイルを上書きするため、先に OBS を閉じてください。",
            "OBS 正在运行。它退出时会覆盖该文件，请先关闭 OBS。");
        Add("ObsSetup.AlreadyOn", "이미 켜져 있습니다 (포트 {0}).", "Already on (port {0}).",
            "すでに有効です（ポート {0}）。", "已经开启（端口 {0}）。");
        Add("ObsSetup.Enabled",
            "websocket 서버를 켰습니다 (포트 {0}, 비밀번호 {1}).\nOBS를 실행한 뒤 다시 시도를 누르세요.\n[주의] OBS가 비정상 종료되면 다음 실행 때 '안전 모드'를 묻습니다. 안전 모드는 websocket을 끄므로 반드시 '일반 모드로 실행'을 고르세요.",
            "The websocket server is on (port {0}, password {1}).\nStart OBS, then press Retry.\n[Note] If OBS closes abnormally it will offer safe mode next time. Safe mode disables the websocket server, so always choose normal mode.",
            "websocket サーバーを有効にしました（ポート {0}、パスワード {1}）。\nOBS を起動してから再試行を押してください。\n[注意] OBS が異常終了すると次回セーフモードを聞かれます。セーフモードは websocket を無効にするため、必ず通常モードを選んでください。",
            "已开启 websocket 服务器（端口 {0}，密码 {1}）。\n启动 OBS 后请点击重试。\n[注意] 若 OBS 异常退出，下次会询问是否进入安全模式。安全模式会关闭 websocket，请务必选择普通模式。");
        Add("ObsSetup.ReadFailed", "OBS 설정 파일을 읽지 못했습니다.", "Could not read the OBS config file.",
            "OBS の設定ファイルを読み取れませんでした。", "无法读取 OBS 配置文件。");
        Add("ObsSetup.WriteFailed", "설정 파일을 쓰지 못했습니다: {0}",
            "Could not write the config file: {0}",
            "設定ファイルを書き込めませんでした: {0}", "无法写入配置文件: {0}");
        Add("Obs.NoHello", "OBS가 Hello를 보내지 않았습니다.", "OBS sent no Hello.",
            "OBS が Hello を送信しませんでした。", "OBS 未发送 Hello。");
        Add("Obs.BadHello", "OBS Hello 형식이 올바르지 않습니다.", "The OBS Hello was malformed.",
            "OBS の Hello の形式が不正です。", "OBS 的 Hello 格式不正确。");
        Add("Obs.NoAuthReply", "OBS 인증에 응답이 없습니다.", "No reply to the OBS handshake.",
            "OBS 認証の応答がありません。", "OBS 认证没有响应。");
        Add("Obs.AuthFailed", "OBS 인증 실패 — websocket 비밀번호를 확인하세요.",
            "OBS authentication failed — check the websocket password.",
            "OBS 認証に失敗しました — websocket のパスワードを確認してください。",
            "OBS 认证失败 — 请检查 websocket 密码。");
        Add("Obs.RequestFailed", "OBS 요청 실패 (코드 {0}): {1}", "OBS request failed (code {0}): {1}",
            "OBS リクエスト失敗（コード {0}）: {1}", "OBS 请求失败（代码 {0}）: {1}");

        // ---- detection loop ------------------------------------------------
        Add("Loop.Waiting", "대기 중... 아수라장 게임을 시작하세요.",
            "Waiting... start an ARAM Mayhem game.",
            "待機中... ランダムミッド: メイヘムのゲームを開始してください。", "等待中... 请开始一局海克斯大乱斗。");
        Add("Loop.GameOver", "게임이 종료되었습니다.", "The game ended.",
            "ゲームが終了しました。", "对局已结束。");
        Add("Loop.GameDetected", "게임 감지: gameMode={0}, level={1}",
            "Game detected: gameMode={0}, level={1}",
            "ゲーム検出: gameMode={0}, level={1}", "检测到对局: gameMode={0}, level={1}");
        Add("Loop.NotMayhem", "  아수라장(KIWI)이 아닙니다. 증강 감지를 건너뜁니다.",
            "  Not ARAM Mayhem (KIWI). Augment detection is skipped.",
            "  ランダムミッド: メイヘム（KIWI）ではありません。オーグメント検出をスキップします。",
            "  不是海克斯大乱斗（KIWI）。跳过强化符文检测。");
        Add("Loop.NewGame", "새 게임이 시작되어 목록을 초기화합니다.",
            "A new game started, so the list is cleared.",
            "新しいゲームが始まったのでリストを初期化します。", "新对局开始，已清空列表。");
        Add("Loop.SourceResized", "위젯 크기에 맞춰 브라우저 소스 조정: {0}x{1}",
            "Browser source resized to the widget: {0}x{1}",
            "ウィジェットに合わせてブラウザソースを調整: {0}x{1}",
            "已按挂件调整浏览器源: {0}x{1}");
        Add("Loop.WindowOpen", "=== 증강 선택창 (레벨 {0}) === 게이트 [{1}]",
            "=== Augment window (level {0}) === gate [{1}]",
            "=== オーグメント選択画面（レベル {0}）=== ゲート [{1}]",
            "=== 强化符文选择界面（等级 {0}）=== 门限 [{1}]");
        Add("Loop.WindowClosed", "=== 선택창 종료 ===", "=== Window closed ===",
            "=== 選択画面 終了 ===", "=== 选择界面结束 ===");
        Add("Loop.Summary",
            "[주의] 프레임 {0}, 안정 {1}, 게이트 최저 {2}, 숨김 최대 {3}, 최대 밝기비 {4}, 기준 {5}",
            "[note] frames {0}, settled {1}, gate low {2}, hide high {3}, peak ratio {4}, baseline {5}",
            "[注意] フレーム {0}、安定 {1}、ゲート最低 {2}、非表示最大 {3}、最大明度比 {4}、基準 {5}",
            "[注意] 帧 {0}，稳定 {1}，门限最低 {2}，隐藏最高 {3}，最大亮度比 {4}，基准 {5}");
        Add("Loop.Anvil", "  모루(아이템) 화면으로 판단 — 이 창은 건너뜁니다: {0}",
            "  Read as an item anvil screen — skipping this window: {0}",
            "  アイテムのアンビル画面と判断 — この画面はスキップします: {0}",
            "  判定为道具铁砧界面 — 跳过此界面: {0}");
        Add("Loop.AnvilSkipped", "  모루 화면이었으므로 기록하지 않습니다.",
            "  It was an anvil screen, so nothing is recorded.",
            "  アンビル画面だったため記録しません。", "  这是铁砧界面，因此不予记录。");
        Add("Loop.NeverSettled",
            "  카드가 안정된 프레임이 없었습니다 — 증강창이 아니라고 보고 기록하지 않습니다.",
            "  No frame ever settled — treating it as not an augment window and recording nothing.",
            "  カードが安定したフレームがありませんでした — オーグメント画面ではないと判断し記録しません。",
            "  没有任何一帧稳定下来 — 视为不是强化符文界面，不予记录。");
        Add("Loop.Undecidable",
            "  어느 카드를 골랐는지 확정할 수 없습니다 (카드 밝기 차이가 작고 툴팁도 못 읽음). 기록하지 않습니다.",
            "  Could not tell which card was taken (brightness too even and the tooltip would not read). Nothing recorded.",
            "  どのカードを選んだか確定できません（明るさの差が小さく、ツールチップも読めませんでした）。記録しません。",
            "  无法确定选了哪张卡（亮度差异过小，提示框也读不出）。不予记录。");
        Add("Loop.TitleUnconfirmed", "  {0} 카드 제목을 확정하지 못했습니다: {1}",
            "  Could not confirm the title on card {0}: {1}",
            "  カード {0} のタイトルを確定できませんでした: {1}", "  无法确定卡片 {0} 的标题: {1}");
        Add("Loop.NoReading", "읽은 값 없음", "nothing read", "読み取り結果なし", "没有读到内容");
        Add("Loop.RerollSeen", "    리롤 감지: {0} 카드 {1} -> {2}",
            "    Reroll seen: card {0} {1} -> {2}",
            "    リロール検出: カード {0} {1} -> {2}", "    检测到重随: 卡片 {0} {1} -> {2}");
        Add("Loop.TooltipMisses", "    툴팁 대조 실패: {0}  (총 {1}종)",
            "    Tooltip did not match: {0}  ({1} distinct)",
            "    ツールチップ照合失敗: {0}  （計 {1} 種）", "    提示框比对失败: {0}  （共 {1} 种）");
        Add("Loop.TooltipNever", "    툴팁이 한 번도 읽히지 않았습니다 (커서가 카드 밖이었거나 미출현).",
            "    The tooltip never read at all (cursor off the cards, or it never appeared).",
            "    ツールチップを一度も読み取れませんでした（カーソルがカード外、または未表示）。",
            "    提示框一次也没读到（光标不在卡片上，或未出现）。");
        Add("Loop.StaleOffer",
            "    [주의] 세 장이 모두 확정된 것은 {0}초 전입니다 — 리롤 직후라면 낡았을 수 있습니다.",
            "    [note] All three titles were last confirmed {0}s ago — may be stale if a reroll just happened.",
            "    [注意] 3 枚すべてが確定したのは {0} 秒前です — リロール直後なら古い可能性があります。",
            "    [注意] 三张卡片全部确认是在 {0} 秒前 — 若刚重随过则可能已过时。");
        Add("Loop.Picked", "  선택: {0} ({1}, 일치도 {2}, OCR '{3}' x{4}, {5} 카드 / {6})",
            "  Taken: {0} ({1}, match {2}, OCR '{3}' x{4}, card {5} / {6})",
            "  選択: {0}（{1}、一致度 {2}、OCR '{3}' x{4}、カード {5} / {6}）",
            "  选择: {0}（{1}，匹配度 {2}，OCR '{3}' x{4}，卡片 {5} / {6}）");
        Add("Loop.Others", "    나머지 선택지: {0}", "    The other offers: {0}",
            "    残りの選択肢: {0}", "    其余选项: {0}");
        Add("Loop.SignalsDisagree",
            "    [주의] 밝기는 {0} 카드, 툴팁은 {1} 카드('{2}', {3}초 전)를 가리킵니다 — 툴팁을 따릅니다.",
            "    [note] brightness says card {0}, the tooltip says card {1} ('{2}', {3}s old) -- going with the tooltip.",
            "    [注意] 明度は カード {0}、ツールチップは カード {1}（'{2}'、{3} 秒前）を指しています — ツールチップに従います。",
            "    [注意] 亮度指向卡片 {0}，提示框指向卡片 {1}（'{2}'，{3} 秒前）— 采用提示框。");
        Add("Loop.HoverStale",
            "    툴팁은 {0} 카드를 가리키지만 {1}초 전 값이라 쓰지 않습니다.",
            "    The tooltip points at card {0}, but the reading is {1}s old and is not used.",
            "    ツールチップは カード {0} を指していますが、{1} 秒前の読み取りのため使いません。",
            "    提示框指向卡片 {0}，但该读数已是 {1} 秒前的，不予采用。");
        Add("Loop.CloseMeans", "    종료 시점 카드 밝기: {0} — 최고 {1}, 차순 대비 {2}배",
            "    Card brightness at the close: {0} -- brightest {1}, {2}x the next",
            "    終了時点のカード明度: {0} — 最高 {1}、次点比 {2} 倍",
            "    关闭时的卡片亮度: {0} — 最高 {1}，为次高的 {2} 倍");
        Add("Loop.DumpSaved", "    판정 화면 저장: {0}", "    decision frame saved: {0}",
            "    判定画面を保存: {0}", "    已保存判定画面: {0}");
        Add("Loop.TipDump", "    툴팁 패널 검출({0}) 화면 저장: {1}",
            "    tooltip panel found ({0}), frame saved: {1}",
            "    ツールチップ検出({0}) 画面を保存: {1}",
            "    提示框检出({0})，已保存画面: {1}");
        Add("Loop.DumpFailed", "    판정 화면 저장 실패: {0}", "    could not save the decision frame: {0}",
            "    判定画面の保存に失敗: {0}", "    保存判定画面失败: {0}");
        Add("Loop.InspectSaved", "    판정 기록 저장: {0} ({1}프레임)",
            "    recording saved: {0} ({1} frames)",
            "    判定の記録を保存: {0} ({1} フレーム)", "    已保存判定记录: {0}（{1} 帧）");
        Add("Loop.InspectFailed", "    판정 기록 저장 실패: {0}",
            "    could not save the recording: {0}",
            "    判定の記録の保存に失敗: {0}", "    保存判定记录失败: {0}");
        Add("Loop.ViaFlare", "카드 밝기 {0}배, 종료 {1}초 전",
            "card {0}x brighter, {1}s before the close",
            "カード明度 {0} 倍、終了 {1} 秒前", "卡片亮度 {0} 倍，关闭前 {1} 秒");
        Add("Loop.ViaTooltip", "툴팁 '{0}'", "tooltip '{0}'", "ツールチップ '{0}'", "提示框 '{0}'");
        Add("Loop.ViaSelectFlare", "선택 플레어 (최고 차순 대비 {0}배, 자기 기준선 대비 {1}배)",
            "selection flare (peak {0}x the next card, {1}x its own baseline)",
            "選択フレア (最高で次点比 {0} 倍、自身の基準比 {1} 倍)",
            "选择闪光 (峰值为次高的 {0} 倍，为自身基线的 {1} 倍)");
        Add("Loop.FlareOverTooltip",
            "    [주의] 선택 플레어는 {0} 카드, 툴팁은 {1} 카드('{2}', {3}초 전) — 플레어를 따릅니다.",
            "    [note] the selection flare says {0}, the tooltip says {1} ('{2}', {3}s old) -- following the flare.",
            "    [注意] 選択フレアは {0}、ツールチップは {1} ('{2}'、{3} 秒前) — フレアに従います。",
            "    [注意] 选择闪光指向 {0}，提示框指向 {1} ('{2}'，{3} 秒前) — 以闪光为准。");
    }
}
