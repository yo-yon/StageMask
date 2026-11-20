using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using StageMask.Core.Detection;

namespace StageMask.Core.Settings;

public enum MaskStyle
{
    /// <summary>塗りつぶし(最も安全)</summary>
    Solid,
    /// <summary>モザイク</summary>
    Mosaic,
    /// <summary>ぼかし</summary>
    Blur,
}

public enum RuleAction
{
    /// <summary>ウィンドウ全体を隠し、移動・リサイズに追従する</summary>
    HideWindow,
    /// <summary>ウィンドウ内の文字をOCRで読み取り、機密情報だけを隠す</summary>
    AutoDetect,
}

public enum CaptureMethod
{
    /// <summary>
    /// PrintWindow(PW_RENDERFULLCONTENT)。キャプチャ中の黄色枠が出ない。
    /// ※ PW_RENDERFULLCONTENT は公式ドキュメントに記載のないフラグ。
    /// </summary>
    PrintWindow,
    /// <summary>
    /// Windows.Graphics.Capture(公式API)。非パッケージアプリでは対象ウィンドウに黄色枠が表示される。
    /// </summary>
    GraphicsCapture,
}

/// <summary>手動で配置する固定マスク。座標は仮想スクリーン上の物理ピクセル。</summary>
public sealed class ManualMask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>見た目(塗りつぶし/モザイク/ぼかし)</summary>
    public MaskStyle Style { get; set; } = MaskStyle.Solid;
    /// <summary>マスク上の文字・画像(共通設定を上書きする場合)</summary>
    public MaskContentOverride Content { get; set; } = new();
}

/// <summary>
/// マスクの上に表示する文字・画像を、共通設定から上書きするための設定。
/// UseCustom が false なら共通設定(表示タブ)を使う。
/// </summary>
public sealed class MaskContentOverride
{
    public bool UseCustom { get; set; }
    /// <summary>表示する文字(空なら文字なし)</summary>
    public string Text { get; set; } = "";
    /// <summary>表示する画像ファイルのパス(空なら画像なし)</summary>
    public string ImagePath { get; set; } = "";
}

/// <summary>ウィンドウ単位のルール。プロセス名(とタイトルの部分一致)で対象を決める。</summary>
public sealed class WindowRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    /// <summary>プロセス名(拡張子なし、大文字小文字は区別しない)。例: OUTLOOK, msedge</summary>
    public string ProcessName { get; set; } = "";
    /// <summary>ウィンドウタイトルの部分一致(空なら条件なし)</summary>
    public string TitleContains { get; set; } = "";
    public RuleAction Action { get; set; } = RuleAction.HideWindow;
    public MaskStyle Style { get; set; } = MaskStyle.Solid;
    /// <summary>このルールのマスク上の文字・画像(共通設定を上書きする場合)</summary>
    public MaskContentOverride Content { get; set; } = new();
}

public sealed class DetectionSettings
{
    public bool Enabled { get; set; } = true;
    public bool DetectEmail { get; set; } = true;
    public bool DetectIdentifier { get; set; } = true;
    public bool DetectSecret { get; set; } = true;
    public bool DetectGenericSecret { get; set; } = true;
    public bool DetectNgWords { get; set; } = true;

    /// <summary>
    /// NGワード(メモリ上は平文)。設定ファイルには平文で書かず、
    /// 暗号化した <see cref="NgWordsProtected"/> として保存する。
    /// </summary>
    [JsonIgnore]
    public List<string> NgWords { get; set; } = new();

    /// <summary>暗号化済みのNGワード(設定ファイル保存用)。直接編集しない。</summary>
    public string? NgWordsProtected { get; set; }

    public bool FuzzyNgWords { get; set; } = true;

    /// <summary>
    /// フェイルクローズ: 自動検出の対象ウィンドウを検査できていない間
    /// (起動直後・取り込み失敗・OCR未導入・処理の停止)は、ウィンドウ全体を隠す。
    /// </summary>
    public bool FailClosed { get; set; } = true;

    /// <summary>
    /// 画面全体モード: ルールに関係なく、表示中のすべてのウィンドウを自動検出の対象にする
    /// (手前のウィンドウに完全に隠れているものは除く)。対象が増えるぶんCPU負荷と検出の遅れが大きくなる。
    /// </summary>
    public bool ScanAllWindows { get; set; }

    /// <summary>
    /// 確認前の変化を先に隠す: 対象ウィンドウを短い間隔で取り込んで前回の検査内容と比べ、
    /// 変わった部分(まだOCRで検査していない表示)を、検査が終わるまで塗りつぶしで隠す。
    /// </summary>
    public bool MaskUnverifiedChanges { get; set; } = true;
    /// <summary>変化を確認する間隔(ミリ秒)</summary>
    public int ChangeCheckMs { get; set; } = 100;

    /// <summary>
    /// マウスホイールでスクロールしたら、カーソルの下の対象ウィンドウ全体をすぐに隠す
    /// (低レベルマウスフックでホイールの回転だけを検知する。ほかの入力は見ない)。
    /// </summary>
    public bool MaskOnWheel { get; set; } = true;
    /// <summary>最後のホイール操作から、ウィンドウ全体を隠し続ける時間(ミリ秒)</summary>
    public int WheelMaskMs { get; set; } = 600;

    /// <summary>
    /// 大きな変化(スクロール・ページ切り替えなど)を検知したら、ウィンドウ全体を隠す。
    /// ホイール以外(タッチパッド・スクロールバー・キー操作)のスクロールにも効く。
    /// 動きが止まってから WheelMaskMs の間と、未確認の部分が広い間は全体を隠し続ける。
    /// </summary>
    public bool MaskOnLargeChange { get; set; } = true;

    /// <summary>
    /// 対象アプリのメニュー・入力候補・ツールチップなど(本体とは別の小さなウィンドウ)も、本体と同じルールで扱う。
    /// 「機密情報を隠す」なら検査し、「常に隠す」なら隠す。メールアドレスの入力候補などの見落としを防ぐ。
    /// </summary>
    public bool IncludePopups { get; set; } = true;

    /// <summary>
    /// 時計・会議の経過時間のように「数字だけが毎秒変わる小さな部分」は、変化しても先に隠さない
    /// (OCR はこれまでどおり行い、数字以外の文字が出てきたら次からは通常どおり隠す)。
    /// </summary>
    public bool IgnoreClockChanges { get; set; } = true;

    /// <summary>
    /// 会議の映像・動画のように動き続けるウィンドウは、ウィンドウ全体ではなく「変わった部分」だけを隠す。
    /// 3秒以上動き続け、その間スクロール(内容の上下のずれ)が見えないウィンドウが対象。
    /// OFF にすると、動いている間はずっとウィンドウ全体を隠す(従来の動き)。
    /// </summary>
    public bool VideoTileMask { get; set; } = true;
    /// <summary>ウィンドウのこの割合(%)以上が変わったら「大きな変化」とする</summary>
    public int LargeChangePercent { get; set; } = 10;

    /// <summary>OCRを実行する間隔(ミリ秒)</summary>
    public int IntervalMs { get; set; } = 600;
    /// <summary>一度検出した領域をマスクし続ける時間(ミリ秒)</summary>
    public int HoldMs { get; set; } = 3000;
    /// <summary>検出領域の周囲に足す余白(物理ピクセル)</summary>
    public int PaddingPx { get; set; } = 6;
    /// <summary>日本語エンジンに加えて英語エンジンでもOCRする(英数字の精度向上)</summary>
    public bool UseEnglishEngine { get; set; } = true;
    public CaptureMethod Capture { get; set; } = CaptureMethod.PrintWindow;
    public MaskStyle Style { get; set; } = MaskStyle.Solid;
    /// <summary>検出枠を赤線で表示する(位置合わせ確認用。本番ではOFF)</summary>
    public bool ShowDebugBoxes { get; set; }

    public DetectorOptions ToDetectorOptions() => new()
    {
        DetectEmail = DetectEmail,
        DetectIdentifier = DetectIdentifier,
        DetectSecret = DetectSecret,
        DetectGenericSecret = DetectGenericSecret,
        DetectNgWords = DetectNgWords,
        NgWords = new List<string>(NgWords),
        FuzzyNgWords = FuzzyNgWords,
    };
}

public sealed class AppearanceSettings
{
    /// <summary>塗りつぶし色(#RRGGBB または #AARRGGBB)</summary>
    public string SolidColor { get; set; } = "#FF2B2B2B";
    /// <summary>マスクに「MASKED」などの文字を表示する</summary>
    public bool ShowLabel { get; set; } = true;
    public string LabelText { get; set; } = "MASKED";
    /// <summary>
    /// マスクに敷き詰めて表示する画像(PNG/JPEG など)。空なら画像なし。
    /// 塗りつぶしのマスクにだけ表示する(モザイク/ぼかしには表示しない)。
    /// </summary>
    public string LabelImagePath { get; set; } = "";
    /// <summary>モザイクの1ブロックの大きさ(物理ピクセル)</summary>
    public int MosaicBlockSize { get; set; } = 14;
    /// <summary>ぼかしの強さ(縮小率。大きいほど強い)</summary>
    public int BlurStrength { get; set; } = 10;
    /// <summary>手動マスクのモザイク/ぼかしを作り直す間隔(ミリ秒)。短いほど追従が速く、CPU負荷は上がる</summary>
    public int MosaicRefreshMs { get; set; } = 250;
    public string PanicMessage { get; set; } = "しばらくお待ちください";
    /// <summary>操作パネルを画面共有・録画に映さない(WDA_EXCLUDEFROMCAPTURE)</summary>
    public bool HidePanelFromCapture { get; set; } = true;
}

/// <summary>
/// 共有用ミラー: 取り込み元のモニターの映像にマスクを重ねて別のウィンドウに映し、そのウィンドウ(を表示したモニター)を共有する。
/// 自分の画面ではマスクを出さず(枠だけ)、相手にはマスクした映像を見せる。OFF なら従来どおり画面に直接マスクを描く。
/// </summary>
public sealed class ShareMirrorSettings
{
    public bool Enabled { get; set; }
    /// <summary>取り込み元のモニター(デバイス名。例 \\.\DISPLAY1)。空ならメインのモニター</summary>
    public string SourceMonitor { get; set; } = "";
    /// <summary>ミラーを全画面で表示するモニター(デバイス名)。空なら通常のウィンドウで表示</summary>
    public string MirrorMonitor { get; set; } = "";
    /// <summary>ミラーを更新する最大のコマ数(1秒あたり)</summary>
    public int MaxFps { get; set; } = 15;
    /// <summary>自分の画面(取り込み元のモニター)で、隠している場所に枠を表示する</summary>
    public bool ShowLocalOutlines { get; set; } = true;
}

public sealed class HotkeySettings
{
    public string ToggleMasks { get; set; } = "Ctrl+Alt+M";
    public string Panic { get; set; } = "Ctrl+Alt+P";
    public string EditMode { get; set; } = "Ctrl+Alt+E";
    public string ToggleDetection { get; set; } = "Ctrl+Alt+D";
    /// <summary>操作パネルを最前面に表示する(マスクの下に隠れたときの呼び出し用)</summary>
    public string ShowPanel { get; set; } = "Ctrl+Alt+S";
}

public sealed class AppSettings
{
    /// <summary>設定ファイルの形式。2 = v0.5.4 以降</summary>
    public int Version { get; set; } = 2;
    public List<ManualMask> ManualMasks { get; set; } = new();
    public List<WindowRule> WindowRules { get; set; } = new();
    public DetectionSettings Detection { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public ShareMirrorSettings ShareMirror { get; set; } = new();
    /// <summary>
    /// マスクするモニター(デバイス名)。空ならすべてのモニター。
    /// 画面共有するモニターだけに絞ると、ほかの画面の取り込み・OCR を省ける。共有用ミラーの使用中は、取り込み元のモニターだけになる
    /// </summary>
    public string TargetMonitor { get; set; } = "";
    public HotkeySettings Hotkeys { get; set; } = new();
    public DiagnosticsSettings Diagnostics { get; set; } = new();
}

/// <summary>
/// 診断ログ(開発・動作確認用)。CPU・メモリ使用量と処理の状況を1秒ごとに CSV へ記録する。
/// 画面の内容(読み取った文字・NGワード・ウィンドウのタイトル)は記録しない。
/// </summary>
public sealed class DiagnosticsSettings
{
    public bool Enabled { get; set; }
    /// <summary>1ファイルの上限(MB)。超えたら次のファイルに切り替える</summary>
    public int MaxFileMb { get; set; } = 10;
    /// <summary>残すファイルの数。超えた分は古いものから消す</summary>
    public int MaxFiles { get; set; } = 10;
}
