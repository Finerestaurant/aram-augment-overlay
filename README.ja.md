# ARAM 大乱闘 オーグメント オーバーレイ

[![CI](https://github.com/Finerestaurant/aram-augment-overlay/actions/workflows/ci.yml/badge.svg)](https://github.com/Finerestaurant/aram-augment-overlay/actions/workflows/ci.yml)
[![release](https://img.shields.io/github/v/release/Finerestaurant/aram-augment-overlay?include_prereleases)](https://github.com/Finerestaurant/aram-augment-overlay/releases)

[한국어](README.md) · [English](README.en.md) · **日本語** · [简体中文](README.zh-CN.md)

リーグ・オブ・レジェンドの **ARAM: 大乱闘** で獲得したオーグメントを、OBS の画面に積み上げて
表示するツールです。画面を読み取って認識するため、アカウント連携もログインも必要ありません。

![ゲーム画面上のオーバーレイ](docs/images/overlay.png)

オーグメントを選ぶと数秒でリストに加わります。背景は透明なので、そのまま配信画面に乗ります。

![積み上がっていくオーグメント](docs/images/overlay.gif)

- オーグメント獲得の瞬間を自動検出 → 名前・レアリティ・アイコンをオーバーレイに追加
- シルバー / ゴールド / プリズムをレアリティごとに色分け
- OBS のブラウザソースとして貼り付けるので、そのまま配信画面に乗ります

> 勝率やティアなどのパフォーマンス指標は、Riot のポリシー上表示しません。

> ウィンドウは韓国語・英語・日本語・中国語に対応し、Windows の言語に従います（設定 → 表示言語で
> 変更できます）。ただし検出のしきい値は韓国語クライアントで測定したものです。[制限](#制限) を
> 参照してください。

---

## ダウンロード

[リリースページ](../../releases) から `ARAM-Augment-Overlay.exe` を 1 つ落とすだけです。
インストールも、他に用意するものもありません — .NET ランタイムすら不要です。

初回実行時に **「Windows によって PC が保護されました」** と表示されます。コード署名をしていない
プログラムだからです。**詳細情報 → 実行** を押してください。

| | |
|---|---|
| OS | Windows 10 / 11、**1920×1080 解像度、拡大率 100%** |
| OBS Studio | 28 以上 |
| ゲーム設定 | **ボーダーレスフルスクリーン**推奨 |
| OCR | クライアント言語に対応する Windows OCR 言語パック — アプリから導入できます |

---

## 最初に使うとき

**1. OBS の websocket サーバーを有効にする**

OBS を **一度起動** して、最初に出る自動構成ウィザードを閉じます。（ウィザードが開いている間、OBS は
設定ファイルを保存しません。）その後 **OBS を完全に終了** し、本プログラムを起動して
**設定**タブの **OBS websocket サーバーを有効化** を押します。OBS 内で
**ツール → WebSocket サーバー設定 → WebSocket サーバーを有効にする** としても同じです。

**2. 実行**

OBS を起動し直し、**状態**タブの **再試行** を押します。接続できるとインジケーターが
緑になり、ウィジェットのアドレスが表示されます。

ウィンドウの ✕ は終了ではなく **トレイに収納** します。トレイアイコンをダブルクリックするか、
プログラムをもう一度起動すると戻ります。本当に終了するには **終了** ボタン、または
トレイアイコンを右クリックして 終了 を選びます。

**3. OBS にウィジェットを追加**

**ソース → + → ブラウザ**
- URL: `http://127.0.0.1:8777/`
- サイズは何を入れても構いません — 実行中にカード幅と 4 行の高さへ自動調整されます
- **「シーンがアクティブになったときにブラウザを更新する」** の有効化を推奨

背景は透明なので、ゲーム画面にそのまま重なります。ゲームキャプチャソースはツールが自動で作ります。

---

### プログラムの画面

状態タブは接続状況とこれまでに獲得したオーグメントを表示し、設定タブで言語・解像度・OBS・オーバーレイを調整します。


| | |
|---|---|
| ![](docs/images/app-status.png) | ![](docs/images/app-settings.png) |

---

## 設定タブ

変更内容は exe と同じ場所の `config.json` に保存され、**保存して再起動**で
反映されます。言語と座標は起動時に一度だけ読むため、再起動が必要です。

| | |
|---|---|
| オーグメント名 | CommunityDragon から取得するクライアント言語。言語ごとにキャッシュします |
| OCR 言語 | 画面を読む Windows OCR の言語。**자동**（自動）なら上の言語に合わせ、パックが無ければインストールコマンドを案内します |
| ゲーム解像度 | 座標をこの解像度に換算します。16:9 ならそのまま動作し、それ以外は警告が出ます |
| OBS | websocket ポート · ゲームキャプチャソース名 · パスワード（空欄なら OBS の設定から自動取得） |
| オーバーレイ | ウィジェットのポート · 表示行数 · 最大幅 |

コマンドライン引数も使えます。`--stop` は実行中のオーバーレイを正常終了させ（トレイアイコンも
片付けます）、`--widget-port` などは設定より優先されます。

---

## うまくいかないとき

**「OBS 未接続」**
OBS が起動しているか、websocket サーバーが有効か確認してください（設定タブのボタン）。OBS を
起動してから **再試行** を押せば済みます。ウィンドウを閉じ直す必要はありません。

**トレイにアイコンが溜まる**
タスクマネージャーで強制終了すると、アイコンを返却する機会がないまま死んだアイコンが残ります。
トレイの上をマウスが通れば Windows が片付けます。強制終了ではなく **終了** ボタンか `--stop` を
使ってください。

**OBS が「セーフモードで起動しますか?」と聞く**
必ず通常モードを選んでください。セーフモードは websocket を無効にするため接続できません。OBS が
異常終了した次の起動で表示されます。

**オーグメントを認識しない**
- 解像度が 1920×1080、拡大率 100% か確認
- ゲームが **ボーダーレスフルスクリーン** か確認
- OBS のゲームキャプチャソースが実際にゲームを捉えているかプレビューで確認

---

## 制限

- 座標は 1920×1080 で測定し、設定した解像度に **比率で換算** されます。16:9 なら他の解像度でも同じ
  配置になりますが、実際に検証したのは 1920×1080 だけです。
- 名前は OCR で読みます。読みが揺れてもオーグメント名の一覧と照合して補正しますが、まれに誤ることが
  あります。確定できなかった読みは記録しません。
- レアリティ判定と選択検出のしきい値は、実プレイとゲームプレイ映像から測定した値です。検証に使った
  シルバーの標本がまだ少なく、シルバーでは誤差が出ることがあります。
- ARAM 大乱闘（`gameMode: KIWI`）でのみ動作します。
- **しきい値はすべて韓国語クライアントで測定しています。** 設定で他の言語も選べ、テストでは英語も
  問題なく読めましたが、韓国語以外は継続的な検証をしていません。

---

## 仕組み

オーグメントのデータは公式 API に **ありません。** Live Client Data API（`127.0.0.1:2999`）の
仕様を全て確認しましたがオーグメント関連のフィールドは一つもなく、マッチ API も大乱闘の対戦には
アクセスできません。そのため画面を読む方法しか残っていません。

1. Live Client Data API で大乱闘かどうか、現在のレベルを確認
2. OBS websocket でゲーム画面を受け取り、**リロールボタン 3 つをテンプレートマッチング** →
   オーグメント選択画面を検出
3. 選択の瞬間は画面が閉じるところから読む — 閉じる直前のカードの明るさとツールチップ
4. カード枠の色でレアリティを判定し、カードのタイトルを OCR
5. 読み取った名前をオーグメント一覧と照合して確定

明るさのしきい値ではなくテンプレートマッチングを使う理由、各しきい値の測定根拠、検証データは
[`docs/FINDINGS.md`](docs/FINDINGS.md)（韓国語）にまとめてあります。

---

## 開発

```
dotnet build src/AramOverlay.slnx
dotnet run --project src/AramOverlay.SelfTest             # 突き合わせ検証
dotnet run --project src/AramOverlay.SelfTest -- --obs    # 起動中の OBS への接続確認
dotnet publish src/AramOverlay.App -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

NuGet パッケージもネイティブ DLL も使わず、BCL と WinRT だけで動きます。OBS websocket は
`ClientWebSocket`、ウィジェットサーバーは `HttpListener`、OCR は `Windows.Media.Ocr`、画像デコードは
`Windows.Graphics.Imaging`、OpenCV が担っていた 4 つの処理は `Cv.cs` に自前で実装しています。

このツールは元々 Python で作られ、C# へ移植しました。どこまで完全に一致させられ、どこからは原理的に
不可能だったかは [`docs/PORTING.md`](docs/PORTING.md)（韓国語）にあります。`tests/` の突き合わせ
データは当時の Python 実装が出した値で、作り直すには `scripts/gen_*.py` とコミット `ecd9cb5`
以前の Python コードが必要です。

タグを push すると GitHub Actions がビルドしてリリースを公開します:

```
git tag v0.1.0 && git push origin v0.1.0
```

---

## 使っているもの

- オーグメントデータ: [CommunityDragon](https://www.communitydragon.org/)
- OCR: Windows 内蔵の OCR エンジン（Windows.Media.Ocr）
- OBS 連携: [obs-websocket](https://github.com/obsproject/obs-websocket)

League of Legends は Riot Games, Inc. の商標です。本プロジェクトは Riot Games とは無関係です。

## ライセンス

MIT
