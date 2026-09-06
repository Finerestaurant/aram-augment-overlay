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

    /// <summary>The system language when it is one this app speaks, else Korean.</summary>
    public static string SystemDefault()
    {
        string tag = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Array.Exists(Languages, l => l.Code == tag) ? tag : "ko";
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

    private static void Add(string key, string ko, string en, string ja, string zh) =>
        Table[key] = new[] { ko, en, ja, zh };

    static Strings()
    {
        _index = 0;

        // ---- window chrome ----------------------------------------------
        Add("App.Title", "아수라장 증강 오버레이", "ARAM Mayhem Augment Overlay",
            "ARAM 大乱闘 オーグメント オーバーレイ", "极地大乱斗 强化符文覆盖层");
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
        Add("Status.NoPicks", "아직 없습니다", "Nothing yet", "まだありません", "暂无");

        // ---- rarities ----------------------------------------------------
        Add("Rarity.silver", "실버", "Silver", "シルバー", "白银");
        Add("Rarity.gold", "골드", "Gold", "ゴールド", "黄金");
        Add("Rarity.prismatic", "프리즘", "Prismatic", "プリズム", "棱彩");
        Add("Rarity.unknown", "미상", "Unknown", "不明", "未知");
        Add("Rarity.WithLevel", "{0} · {1}레벨", "{0} · level {1}",
            "{0} · レベル {1}", "{0} · {1} 级");

        // ---- settings: sections and fields --------------------------------
        Add("Settings.Language", "언어", "LANGUAGE", "言語", "语言");
        Add("Settings.UiLanguage", "프로그램 언어", "Interface", "表示言語", "界面语言");
        Add("Settings.AugmentNames", "증강 이름", "Augment names", "オーグメント名", "强化符文名称");
        Add("Settings.OcrLanguage", "OCR 언어", "OCR language", "OCR 言語", "OCR 语言");
        Add("Settings.Resolution", "해상도", "RESOLUTION", "解像度", "分辨率");
        Add("Settings.GameResolution", "게임 해상도", "Game resolution", "ゲーム解像度", "游戏分辨率");
        Add("Settings.Obs", "OBS", "OBS", "OBS", "OBS");
        Add("Settings.ObsPort", "websocket 포트", "websocket port", "websocket ポート", "websocket 端口");
        Add("Settings.ObsSource", "게임 캡처 소스", "Game capture source",
            "ゲームキャプチャソース", "游戏采集源");
        Add("Settings.ObsPassword", "비밀번호", "Password", "パスワード", "密码");
        Add("Settings.Overlay", "오버레이", "OVERLAY", "オーバーレイ", "覆盖层");
        Add("Settings.WidgetPort", "위젯 포트", "Widget port", "ウィジェットのポート", "挂件端口");
        Add("Settings.Rows", "표시 행 수", "Rows shown", "表示行数", "显示行数");
        Add("Settings.MaxWidth", "최대 폭 (px)", "Maximum width (px)", "最大幅 (px)", "最大宽度 (px)");
        Add("Settings.Save", "저장하고 다시 시작", "Save and restart", "保存して再起動", "保存并重启");
        Add("Settings.Defaults", "기본값", "Defaults", "既定値", "默认值");
        Add("Settings.EnableWebsocket", "OBS websocket 서버 켜기", "Turn on the OBS websocket server",
            "OBS の websocket サーバーを有効化", "打开 OBS websocket 服务器");

        Add("Settings.Advanced", "고급", "ADVANCED", "詳細", "高级");
        Add("Settings.DebugMode", "디버그 로그 보기", "Show the debug log",
            "デバッグログを表示", "显示调试日志");
        Add("Hint.DebugMode",
            "감지 과정을 그대로 찍습니다. 증강이 기록되지 않았을 때 어디서 갈렸는지 볼 때만 켜세요.",
            "Prints the detection reasoning. Turn it on only when an augment went unrecorded and you want to see where it went wrong.",
            "検出の過程をそのまま出力します。オーグメントが記録されなかった原因を調べるときだけ有効にしてください。",
            "会原样输出检测过程。只在某个强化符文没有被记录、想查清原因时才打开。");

        // ---- settings: hints ----------------------------------------------
        Add("Settings.Auto", "자동", "Auto", "自動", "自动");
        Add("Hint.PasswordBlank", "비워 두면 OBS 설정 파일에서 자동으로 읽습니다.",
            "Leave blank to read it from the OBS config file.",
            "空欄にすると OBS の設定ファイルから自動で読み取ります。",
            "留空则从 OBS 配置文件自动读取。");
        Add("Hint.ReadsWith", "'{0}' 로 읽습니다.", "Reads with {0}.",
            "{0} で読み取ります。", "使用 {0} 读取。");
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
        Add("Hint.Ratio169", "16:9 — 좌표가 비율대로 환산되어 그대로 동작합니다.",
            "16:9 — coordinates rescale proportionally and this works as-is.",
            "16:9 — 座標が比率で換算され、そのまま動作します。",
            "16:9 — 坐标按比例换算，可直接工作。");
        Add("Hint.NotRatio169",
            "16:9가 아닙니다. 클라이언트가 HUD를 다르게 배치하므로 카드 위치가 어긋날 수 있습니다.",
            "Not 16:9. The client anchors its HUD differently, so the cards may not line up.",
            "16:9 ではありません。クライアントが HUD を別配置にするため、カード位置がずれることがあります。",
            "不是 16:9。客户端会以不同方式排布 HUD，卡片位置可能对不上。");
        Add("Hint.Saved", "저장했습니다. 다시 시작합니다...", "Saved. Restarting...",
            "保存しました。再起動します...", "已保存，正在重启...");
        Add("Hint.DefaultsRestored", "기본값으로 되돌렸습니다. 다시 시작합니다...",
            "Restored to defaults. Restarting...",
            "既定値に戻しました。再起動します...", "已恢复默认值，正在重启...");

        // ---- log lines the window prints itself ---------------------------
        Add("Log.UrlCopied", "위젯 주소를 복사했습니다: {0}", "Widget address copied: {0}",
            "ウィジェットのアドレスをコピーしました: {0}", "已复制挂件地址: {0}");
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
            "ARAM 大乱闘 オーグメント オーバーレイ", "极地大乱斗 强化符文覆盖层");
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
        Add("Core.OcrPreparing", "OCR 준비 중...", "Preparing OCR...", "OCR を準備中...", "正在准备 OCR...");
        Add("Core.WidgetUrl",
            "위젯 주소: {0}   <- OBS 브라우저 소스에 이 주소를 넣으세요 (크기는 내용에 맞춰 자동 조정됩니다)",
            "Widget address: {0}   <- put this in an OBS browser source (it resizes itself)",
            "ウィジェットのアドレス: {0}   <- OBS のブラウザソースにこのアドレスを入れてください（サイズは自動調整されます）",
            "挂件地址: {0}   <- 请填入 OBS 浏览器源（尺寸会自动调整）");
        Add("Core.BrowserRefreshed", "브라우저 소스 새로고침: {0}", "Browser sources refreshed: {0}",
            "ブラウザソースを更新しました: {0}", "已刷新浏览器源: {0}");
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
            "待機中... ARAM 大乱闘のゲームを開始してください。", "等待中... 请开始一局极地大乱斗混乱模式。");
        Add("Loop.GameOver", "게임이 종료되었습니다.", "The game ended.",
            "ゲームが終了しました。", "对局已结束。");
        Add("Loop.GameDetected", "게임 감지: gameMode={0}, level={1}",
            "Game detected: gameMode={0}, level={1}",
            "ゲーム検出: gameMode={0}, level={1}", "检测到对局: gameMode={0}, level={1}");
        Add("Loop.NotMayhem", "  아수라장(KIWI)이 아닙니다. 증강 감지를 건너뜁니다.",
            "  Not ARAM Mayhem (KIWI). Augment detection is skipped.",
            "  ARAM 大乱闘（KIWI）ではありません。オーグメント検出をスキップします。",
            "  不是极地大乱斗混乱模式（KIWI）。跳过强化符文检测。");
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
        Add("Loop.ViaFlare", "카드 밝기 {0}배, 종료 {1}초 전",
            "card {0}x brighter, {1}s before the close",
            "カード明度 {0} 倍、終了 {1} 秒前", "卡片亮度 {0} 倍，关闭前 {1} 秒");
        Add("Loop.ViaTooltip", "툴팁 '{0}'", "tooltip '{0}'", "ツールチップ '{0}'", "提示框 '{0}'");
    }
}
