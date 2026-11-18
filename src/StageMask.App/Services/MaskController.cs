using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using StageMask.App.Capture;
using StageMask.App.Native;
using StageMask.App.Overlay;
using StageMask.Core.Detection;
using StageMask.Core.Geometry;
using StageMask.Core.Settings;

namespace StageMask.App.Services;

/// <summary>
/// アプリの中心。約30fpsでウィンドウ位置・検出結果を集めてマスクを組み立て、全モニターのオーバーレイに描画する。
/// UIスレッドで動作する(検出ワーカーだけが別スレッド)。
/// </summary>
internal sealed class MaskController : IDisposable
{
    /// <summary>ウィンドウ全体マスクの余白(ドラッグ移動中のはみ出し対策)</summary>
    private const int WindowMaskPaddingPx = 4;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan EnumerateInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TopmostInterval = TimeSpan.FromSeconds(1);

    private readonly SettingsStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly List<OverlayWindow> _overlays = new();
    private readonly DispatcherTimer _timer;
    private readonly DetectionHoldBuffer<DetectionPayload> _hold = new();
    private readonly ScanStatusTracker _scanStatus = new();
    private readonly ConcurrentDictionary<IntPtr, ImageSource> _windowImages = new();
    private readonly ConcurrentDictionary<Guid, ImageSource> _manualImages = new();
    private readonly ContentImageCache _contentImages = new();
    private readonly DetectionWorker _worker;

    private List<WindowInfo> _windows = new();
    private DateTime _lastEnumUtc = DateTime.MinValue;
    private DateTime _lastTopmostUtc = DateTime.MinValue;
    private volatile WorkTarget[] _targets = Array.Empty<WorkTarget>();
    private volatile ManualMosaicTarget[] _manualTargets = Array.Empty<ManualMosaicTarget>();
    private volatile WindowInfo[] _windowsTopToBottom = Array.Empty<WindowInfo>();
    private HashSet<IntPtr> _visibleHandles = new();
    private readonly WheelMonitor _wheelMonitor;

    /// <summary>ウィンドウのこの割合以上が変わったら「大きな変化」として全体を隠す</summary>
    private double LargeChangeFraction => Math.Clamp(Settings.Detection.LargeChangePercent, 5, 100) / 100.0;
    private readonly ConcurrentDictionary<IntPtr, DateTime> _wheelUntil = new();
    private int _wheelTickQueued;
    private int _lastSignature;
    private bool _forceRender = true;
    private Brush _solidBrush = Brushes.Black;

    public AppSettings Settings { get; }
    public bool MasksEnabled { get; private set; } = true;

    // ---------- 診断ログ用 ----------
    private long _wheelEvents;
    private readonly ConcurrentQueue<string> _diagEvents = new();
    /// <summary>ホイール操作の累計回数</summary>
    public long WheelEventCount => Interlocked.Read(ref _wheelEvents);
    public long OcrFullCount => _worker.OcrFullCount;
    public long OcrBandCount => _worker.OcrBandCount;
    public long OcrSkippedCount => _worker.OcrSkippedCount;
    public long CaptureFailureCount => _worker.CaptureFailureCount;
    public double CheckCaptureMsTotal => _worker.CaptureMsTotal;
    public double CheckSignatureMsTotal => _worker.SignatureMsTotal;
    public double TakeMaxCaptureMs() => _worker.TakeMaxCaptureMs();

    /// <summary>診断ログに残す出来事を記録する(決まった文言だけを渡すこと。画面の内容は渡さない)。</summary>
    public void RecordEvent(string name)
    {
        _diagEvents.Enqueue(name);
        while (_diagEvents.Count > 100 && _diagEvents.TryDequeue(out _)) { }
    }

    /// <summary>前回呼んでから記録された出来事を取り出す。</summary>
    public List<string> TakeEvents()
    {
        var list = new List<string>();
        while (_diagEvents.TryDequeue(out var e)) list.Add(e);
        return list;
    }
    public bool Panic { get; private set; }
    public bool EditMode { get; private set; }

    public int OverlayCount => _overlays.Count;
    public int HiddenWindowCount { get; private set; }
    public int DetectTargetCount { get; private set; }
    public int DetectionMaskCount { get; private set; }
    /// <summary>まだ検査していない変化を隠しているマスクの数</summary>
    public int PendingMaskCount { get; private set; }
    /// <summary>スクロール中のため全体を隠しているウィンドウの数</summary>
    public int ScrollMaskCount { get; private set; }
    /// <summary>変化の確認1回分の時間</summary>
    public TimeSpan CheckRound => _worker.LastCheckRound;
    /// <summary>ホイール検知が動いているか</summary>
    public bool WheelMonitorRunning => _wheelMonitor.IsRunning;
    /// <summary>検査できていないため、ウィンドウ全体を隠している自動検出対象の数</summary>
    public int FailClosedCount { get; private set; }
    /// <summary>ウィンドウ全体を隠している理由(代表1件)</summary>
    public string? FailClosedReason { get; private set; }
    /// <summary>設定ファイルの読み込み・保存時の注意事項</summary>
    public string? StoreWarning => _store.Warning;
    /// <summary>マスク上に表示する画像を読み込めなかった理由</summary>
    public string? ContentImageError => _contentImages.LastError;
    public string? LastError { get; private set; }
    public string OcrStatus => _worker.OcrStatus;
    public string CaptureStatus => _worker.CaptureStatus;
    public string? WorkerError => _worker.LastError;
    public TimeSpan WorkerCycle => _worker.LastCycle;

    /// <summary>
    /// マスクより前面に保つウィンドウ(操作パネルが前面にある間だけ設定する)。
    /// 設定しないと、最前面のマスクの下にパネルが隠れて操作できなくなる。
    /// </summary>
    public IntPtr KeepAboveOverlays { get; set; }

    /// <summary>直近に列挙したウィンドウ(ウィンドウ選択リスト用)</summary>
    public IReadOnlyList<WindowInfo> CurrentWindows => _windows;

    /// <summary>ON/OFF・パニック・編集モードが変わった</summary>
    public event EventHandler? StateChanged;
    /// <summary>手動マスクが追加・変更・削除された</summary>
    public event EventHandler? ManualMasksChanged;

    public MaskController(SettingsStore store, AppSettings settings, Dispatcher dispatcher)
    {
        _store = store;
        Settings = settings;
        _dispatcher = dispatcher;
        ApplyAppearance();

        _worker = new DetectionWorker(
            () => _targets,
            _hold,
            _scanStatus,
            (hwnd, image) =>
            {
                if (image is null) _windowImages.TryRemove(hwnd, out _);
                else _windowImages[hwnd] = image;
            },
            () => _manualTargets,
            (id, image) =>
            {
                if (image is null) _manualImages.TryRemove(id, out _);
                else _manualImages[id] = image;
            },
            () => _windowsTopToBottom,
            BuildWorkerConfig());

        _wheelMonitor = new WheelMonitor(OnWheelFromHook);

        _timer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher) { Interval = TickInterval };
        _timer.Tick += (_, _) => Tick();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void Start()
    {
        UpdateWheelMonitor();
        RecreateOverlays();
        _timer.Start();
        _worker.Start();
    }

    // ------------------------------------------------------------------
    // 操作
    // ------------------------------------------------------------------

    public void SetMasksEnabled(bool enabled)
    {
        MasksEnabled = enabled;
        RecordEvent(enabled ? "マスクON" : "マスクOFF");
        Invalidate();
    }

    public void SetPanic(bool panic)
    {
        Panic = panic;
        RecordEvent(panic ? "パニックON" : "パニックOFF");
        Invalidate();
    }

    public void SetEditMode(bool edit)
    {
        EditMode = edit;
        RecordEvent(edit ? "編集モードON" : "編集モードOFF");
        Invalidate();
    }

    public void SetDetectionEnabled(bool enabled)
    {
        Settings.Detection.Enabled = enabled;
        RecordEvent(enabled ? "自動検出ON" : "自動検出OFF");
        ApplySettings();
    }

    /// <summary>設定画面での変更を反映して保存する。</summary>
    public void ApplySettings()
    {
        ApplyAppearance();
        _contentImages.Clear(); // 画像の指定が変わっていれば読み込み直す
        UpdateWheelMonitor();
        _worker.UpdateConfig(BuildWorkerConfig());
        RecordEvent("設定を適用");
        Save();
        Invalidate();
    }

    public void Save()
    {
        try
        {
            _store.Save(Settings);
        }
        catch (Exception ex)
        {
            LastError = "設定の保存に失敗: " + ex.Message;
            RecordEvent("設定の保存に失敗");
            Logger.Write("Save", ex);
        }
    }

    public void RemoveManualMask(Guid id)
    {
        Settings.ManualMasks.RemoveAll(m => m.Id == id);
        OnManualMasksEdited();
    }

    public void ClearManualMasks()
    {
        Settings.ManualMasks.Clear();
        OnManualMasksEdited();
    }

    /// <summary>
    /// 手動マスクが変わったときに呼ぶ。保存して再描画する。
    /// listChanged=false は一覧画面での「有効/名前」の編集(一覧の再描画は不要)。
    /// </summary>
    public void OnManualMasksEdited(bool listChanged = true)
    {
        Save();
        _forceRender = true;
        if (listChanged) ManualMasksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Invalidate()
    {
        _forceRender = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        Tick();
    }

    // ------------------------------------------------------------------
    // 毎フレームの処理
    // ------------------------------------------------------------------

    private void Tick()
    {
        try
        {
            TickCore();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Logger.Write("Tick", ex);
            RecordEvent("描画処理でエラー");
        }
    }

    private void TickCore()
    {
        var now = DateTime.UtcNow;
        if (now - _lastEnumUtc >= EnumerateInterval)
        {
            _windows = WindowEnumerator.GetWindows();
            _windowsTopToBottom = _windows.ToArray();
            _visibleHandles = ComputeVisibleWindows(_windows);
            _lastEnumUtc = now;
        }
        if (now - _lastTopmostUtc >= TopmostInterval)
        {
            foreach (var o in _overlays) o.BringToTop();
            RaiseKeptWindow();
            _lastTopmostUtc = now;
        }

        var visuals = new List<MaskVisual>();
        var targets = new Dictionary<IntPtr, WorkTarget>();
        var detectIds = new List<long>();
        var manualTargets = new List<ManualMosaicTarget>();
        int hiddenCount = 0, detectionMasks = 0, failClosed = 0, pendingMasks = 0, scrollMasks = 0;
        string? failReason = null;
        // 最後の検査成功からこの時間を過ぎたら「検査できていない」とみなす(処理の停止・遅延への備え)
        var maxScanAge = Settings.Detection.MaskUnverifiedChanges
            // 対象が多いと、奥のウィンドウは数回に1回しか確認されない。その周期より短くすると、確認待ちの間に
            // 全体マスクが点滅するため、周期に合わせて延ばす
            ? TimeSpan.FromMilliseconds(Math.Max(1500, 2 * _worker.CheckRotationPeriod.TotalMilliseconds + 4 * _worker.LastCheckRound.TotalMilliseconds + 500))
            : TimeSpan.FromMilliseconds(
                3 * Math.Max(Math.Clamp(Settings.Detection.IntervalMs, 150, 10_000), _worker.LastCycle.TotalMilliseconds) + 1500);

        if (MasksEnabled)
        {
            // 画面全体モード: ルールのないウィンドウにも適用する、見た目・表示は共通設定のルール
            WindowRule? scanAllRule = Settings.Detection.ScanAllWindows
                ? new WindowRule { Action = RuleAction.AutoDetect, Style = Settings.Detection.Style }
                : null;

            // ルールに一致したウィンドウ(本体)のプロセスごとのルール。メニュー・入力候補などに引き継ぐ
            var matched = new Dictionary<IntPtr, (WindowRule? Hide, WindowRule? Detect)>();
            var byProcess = new Dictionary<int, (WindowRule? Hide, WindowRule? Detect)>();
            foreach (var raw in _windows)
            {
                WindowRule? h = null, d = null;
                foreach (var rule in Settings.WindowRules)
                {
                    if (!WindowRuleMatcher.Matches(rule, raw.ProcessName, raw.Title)) continue;
                    if (rule.Action == RuleAction.HideWindow) h ??= rule;
                    else d ??= rule;
                }
                matched[raw.Handle] = (h, d);
                if ((h is not null || d is not null) && !raw.IsPopupLike)
                {
                    byProcess.TryGetValue(raw.ProcessId, out var p);
                    byProcess[raw.ProcessId] = (p.Hide ?? h, p.Detect ?? d);
                }
            }

            foreach (var raw in _windows)
            {
                var (hide, detect) = matched[raw.Handle];
                // 本体と同じプロセスのメニュー・入力候補・ツールチップは、本体のルールを引き継ぐ
                if (hide is null && detect is null && raw.IsPopupLike && Settings.Detection.IncludePopups &&
                    byProcess.TryGetValue(raw.ProcessId, out var inherited))
                {
                    hide = inherited.Hide;
                    detect = inherited.Detect;
                }
                if (hide is null && detect is null && scanAllRule is not null && _visibleHandles.Contains(raw.Handle))
                    detect = scanAllRule;
                if (hide is null && detect is null) continue;

                // 対象のウィンドウだけ、位置を毎フレーム取り直す
                var w = WindowEnumerator.RefreshGeometry(raw);
                if (w is null || w.IsMinimized) continue;
                if (w.FrameRect.Width < 8 || w.FrameRect.Height < 8) continue; // 実体のない補助ウィンドウ

                if (hide is not null)
                {
                    ImageSource? image = null;
                    if (hide.Style != MaskStyle.Solid && _windowImages.TryGetValue(w.Handle, out var img)) image = img;
                    var c = Content(hide.Content, hide.Style);
                    visuals.Add(new MaskVisual(w.FrameRect.Inflate(WindowMaskPaddingPx), MaskKind.Window, hide.Style, image,
                                               Text: c.Text, ContentImage: _contentImages.Get(c.ImagePath)));
                    hiddenCount++;
                    if (hide.Style != MaskStyle.Solid) AddTarget(targets, w, ocr: false, detectStyle: MaskStyle.Solid, fullImage: true, windowStyle: hide.Style);
                }

                // メニュー・入力候補などは小さいので、小さくても検査する(設定でOFFにしたときは従来どおり除外)
                bool detectable = w.IsPopupLike && Settings.Detection.IncludePopups
                    ? w.FrameRect.Width >= 16 && w.FrameRect.Height >= 12
                    : !w.IsToolWindow && w.FrameRect.Width >= 100 && w.FrameRect.Height >= 40;
                if (detect is not null && detectable && Settings.Detection.Enabled)
                {
                    AddTarget(targets, w, ocr: true, detectStyle: detect.Style, fullImage: false, windowStyle: MaskStyle.Solid);
                    var id = w.Handle.ToInt64();
                    detectIds.Add(id);

                    // スクロール中: ホイールを回した直後は、ウィンドウ全体を隠す
                    bool wheeling = Settings.Detection.MaskOnWheel &&
                                    _wheelUntil.TryGetValue(w.Handle, out var wheelUntil) && wheelUntil > now;
                    // 大きな変化(スクロール・ページ切り替えなど): 動いている間と、未確認が広い間は全体を隠す
                    var pending = Settings.Detection.MaskUnverifiedChanges ? _worker.GetPending(w.Handle) : null;
                    bool largeChange = Settings.Detection.MaskOnLargeChange && pending is not null &&
                                       (pending.Fraction >= LargeChangeFraction ||
                                        (now - pending.LastMotionUtc).TotalMilliseconds < Math.Clamp(Settings.Detection.WheelMaskMs, 100, 5000));
                    if (hide is null && (wheeling || largeChange))
                    {
                        visuals.Add(new MaskVisual(w.FrameRect.Inflate(WindowMaskPaddingPx), MaskKind.Window, MaskStyle.Solid, null));
                        scrollMasks++;
                    }
                    // フェイルクローズ: 検査できていないウィンドウは、部分マスクではなく全体を隠す
                    else if (hide is null && Settings.Detection.FailClosed && !_scanStatus.IsSafe(id, now, maxScanAge))
                    {
                        var c = Content(detect.Content, MaskStyle.Solid);
                        visuals.Add(new MaskVisual(w.FrameRect.Inflate(WindowMaskPaddingPx), MaskKind.Window, MaskStyle.Solid, null,
                                                   Text: c.Text, ContentImage: _contentImages.Get(c.ImagePath)));
                        failClosed++;
                        failReason ??= $"{w.ProcessName}: {_scanStatus.GetFailureReason(id) ?? "検査待ち"}";
                    }
                    else if (hide is null) // 全体を隠しているウィンドウには重ねて描く必要がない
                    {
                        var c = Content(detect.Content, detect.Style);
                        var contentImage = _contentImages.Get(c.ImagePath);
                        foreach (var item in _hold.GetActive(id, now))
                        {
                            var screen = MapToScreen(item, w);
                            if (screen.IsEmpty) continue;
                            visuals.Add(new MaskVisual(screen, MaskKind.Detection, detect.Style, item.Payload?.Image,
                                                       Text: c.Text, ContentImage: contentImage));
                            detectionMasks++;
                            if (Settings.Detection.ShowDebugBoxes)
                                visuals.Add(new MaskVisual(screen, MaskKind.DebugBox, MaskStyle.Solid, null));
                        }

                        // まだOCRで検査していない(取り込み後に変化した)部分は、検査が終わるまで塗りつぶす
                        if (pending is not null)
                        {
                            foreach (var r in pending.Rects)
                            {
                                var screen = MapFrameRect(r, pending.Geometry, w).Inflate(2);
                                if (screen.IsEmpty) continue;
                                visuals.Add(new MaskVisual(screen, MaskKind.Detection, MaskStyle.Solid, null));
                                pendingMasks++;
                            }
                        }
                    }
                }
            }

            // 手動マスクは最後に追加する(編集時の当たり判定を優先する)
            foreach (var m in Settings.ManualMasks)
            {
                if (!m.Enabled && !EditMode) continue;
                if (m.Width <= 0 || m.Height <= 0) continue;
                var rect = new RectD(m.X, m.Y, m.Width, m.Height);
                ImageSource? mosaic = null;
                if (m.Style != MaskStyle.Solid)
                {
                    manualTargets.Add(new ManualMosaicTarget(m.Id, rect, m.Style));
                    _manualImages.TryGetValue(m.Id, out mosaic);
                }
                var c = Content(m.Content, m.Style);
                visuals.Add(new MaskVisual(rect, MaskKind.Manual, m.Style, mosaic, m.Id,
                                           Text: c.Text, ContentImage: _contentImages.Get(c.ImagePath)));
            }
        }

        _targets = targets.Values.ToArray();
        _manualTargets = manualTargets.ToArray();
        foreach (var key in _manualImages.Keys)
            if (!manualTargets.Any(t => t.Id == key)) _manualImages.TryRemove(key, out _);
        _hold.RetainOnly(detectIds);
        _scanStatus.RetainOnly(detectIds);
        // 検出結果を捨てたウィンドウは、ワーカー側の「確認済み」も捨てる(戻ってきたときに必ず検査し直すため)
        _worker.RetainStates(detectIds.Select(i => new IntPtr(i)).ToHashSet());
        foreach (var key in _windowImages.Keys)
            if (!targets.TryGetValue(key, out var t) || !t.FullImage) _windowImages.TryRemove(key, out _);

        HiddenWindowCount = hiddenCount;
        DetectTargetCount = detectIds.Count;
        DetectionMaskCount = detectionMasks;
        FailClosedCount = failClosed;
        FailClosedReason = failReason;
        PendingMaskCount = pendingMasks;
        ScrollMaskCount = scrollMasks;
        foreach (var kv in _wheelUntil)
            if (kv.Value <= now) _wheelUntil.TryRemove(kv.Key, out _);

        int signature = ComputeSignature(visuals);
        if (!_forceRender && signature == _lastSignature) return;
        _forceRender = false;
        _lastSignature = signature;

        var state = new OverlayState(
            visuals,
            Panic,
            EditMode,
            _solidBrush,
            Settings.Appearance.PanicMessage,
            $"編集モード: ドラッグで追加 / マスクをドラッグで移動 / 右下の■でサイズ変更 / 右クリックで削除   終了: {Settings.Hotkeys.EditMode}(パネルを出す: {Settings.Hotkeys.ShowPanel})");
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var o in _overlays) o.Render(state);
        _renderTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        _renderCount++;
        if (visuals.Count > _maxVisuals) _maxVisuals = visuals.Count;
    }

    // 描画の負荷の計測(診断ログ用。UIスレッドだけで読み書きする)
    private long _renderTicks;
    private int _renderCount, _maxVisuals;

    /// <summary>
    /// 前回呼んでからの描画回数・描画にかかった時間(ミリ秒)・マスクの最大数を返してリセットする。
    /// 時間はマスクの部品を組み立てる処理(UIスレッド)だけで、実際の画面への描画(描画スレッド・GPU)は含まない。
    /// </summary>
    public (int Renders, double RenderMs, int MaxVisuals) TakeRenderStats()
    {
        var r = (_renderCount, _renderTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, _maxVisuals);
        _renderCount = 0;
        _renderTicks = 0;
        _maxVisuals = 0;
        return r;
    }

    private ResolvedMaskContent Content(MaskContentOverride? custom, MaskStyle style)
        => MaskContentResolver.Resolve(Settings.Appearance, custom, style);

    /// <summary>手前のウィンドウに完全に隠れていないウィンドウ(画面全体モードの検査対象)。</summary>
    private static HashSet<IntPtr> ComputeVisibleWindows(List<WindowInfo> windowsTopToBottom)
    {
        var above = new List<RectD>();
        var visible = new HashSet<IntPtr>();
        foreach (var w in windowsTopToBottom)
        {
            if (w.IsMinimized) continue;
            if (!RectD.IsFullyCovered(w.FrameRect, above)) visible.Add(w.Handle);
            above.Add(w.FrameRect);
        }
        return visible;
    }

    private static void AddTarget(Dictionary<IntPtr, WorkTarget> targets, WindowInfo w,
                                  bool ocr, MaskStyle detectStyle, bool fullImage, MaskStyle windowStyle)
    {
        if (targets.TryGetValue(w.Handle, out var existing))
        {
            targets[w.Handle] = existing with
            {
                Window = w,
                Ocr = existing.Ocr || ocr,
                DetectStyle = ocr ? detectStyle : existing.DetectStyle,
                FullImage = existing.FullImage || fullImage,
                WindowStyle = fullImage ? windowStyle : existing.WindowStyle,
            };
        }
        else
        {
            targets[w.Handle] = new WorkTarget(w.Handle, w, ocr, detectStyle, fullImage, windowStyle);
        }
    }

    /// <summary>検出結果(キャプチャ画像の座標)を、現在のウィンドウ位置に合わせた画面座標に変換する。</summary>
    private static RectD MapToScreen(DetectionHoldBuffer<DetectionPayload>.Item item, WindowInfo w)
        => MapFrameRect(item.Rect, item.Payload?.Geometry, w);

    /// <summary>キャプチャ画像の座標を、現在のウィンドウ位置に合わせた画面座標に変換する。</summary>
    private static RectD MapFrameRect(RectD r, FrameGeometry? g, WindowInfo w)
    {
        if (g is null || g.Width <= 0 || g.Height <= 0) return default;
        var origin = g.Origin == CaptureOrigin.WindowRect ? w.WindowRect : w.FrameRect;
        double sx = origin.Width / g.Width, sy = origin.Height / g.Height;
        var screen = new RectD(origin.X + r.X * sx, origin.Y + r.Y * sy, r.Width * sx, r.Height * sy);
        return screen.Intersect(w.FrameRect.Inflate(2));
    }

    // ------------------------------------------------------------------
    // ホイール検知
    // ------------------------------------------------------------------

    private void UpdateWheelMonitor()
    {
        if (Settings.Detection.MaskOnWheel && Settings.Detection.Enabled) _wheelMonitor.Start();
        else _wheelMonitor.Stop();
    }

    /// <summary>フック専用スレッドから呼ばれる。すぐに返すこと。</summary>
    private void OnWheelFromHook(int x, int y)
    {
        Interlocked.Increment(ref _wheelEvents);
        // カーソルの下のウィンドウ = 手前から順に見て、最初にカーソル位置を含むウィンドウ。
        // 自分で列挙したウィンドウ(このアプリのマスク用ウィンドウは含まない)の位置で判定する
        IntPtr target = IntPtr.Zero;
        foreach (var w in _windowsTopToBottom)
        {
            if (w.IsMinimized) continue;
            var r = w.FrameRect;
            if (x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height) { target = w.Handle; break; }
        }
        if (target == IntPtr.Zero) return;
        _wheelUntil[target] = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Clamp(Settings.Detection.WheelMaskMs, 100, 5000));
        // 次の描画を待たずに、すぐマスクを描く(連続で回したときに描画要求を溜めない)
        if (Interlocked.Exchange(ref _wheelTickQueued, 1) == 0)
            _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                Volatile.Write(ref _wheelTickQueued, 0);
                Tick();
            }));
    }

    private int ComputeSignature(List<MaskVisual> visuals)
    {
        var hc = new HashCode();
        hc.Add(Panic);
        hc.Add(EditMode);
        foreach (var v in visuals)
        {
            hc.Add((int)v.ScreenRect.X);
            hc.Add((int)v.ScreenRect.Y);
            hc.Add((int)v.ScreenRect.Width);
            hc.Add((int)v.ScreenRect.Height);
            hc.Add(v.Kind);
            hc.Add(v.Style);
            hc.Add(v.Image is null ? 0 : RuntimeHelpers.GetHashCode(v.Image));
            hc.Add(v.ManualId);
            hc.Add(v.Text);
            hc.Add(v.ContentImage is null ? 0 : RuntimeHelpers.GetHashCode(v.ContentImage));
        }
        return hc.ToHashCode();
    }

    // ------------------------------------------------------------------
    // オーバーレイ
    // ------------------------------------------------------------------

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        RecordEvent("画面構成が変化");
        _dispatcher.BeginInvoke(new Action(RecreateOverlays));
    }

    /// <summary>モニター構成に合わせてオーバーレイを作り直す。</summary>
    public void RecreateOverlays()
    {
        foreach (var o in _overlays) o.Close();
        _overlays.Clear();

        foreach (var m in MonitorEnumerator.GetMonitors())
        {
            var overlay = new OverlayWindow(m.Bounds);
            overlay.ManualMaskCreated += rect =>
            {
                Settings.ManualMasks.Add(new ManualMask
                {
                    Label = $"マスク{Settings.ManualMasks.Count + 1}",
                    X = (int)Math.Round(rect.X),
                    Y = (int)Math.Round(rect.Y),
                    Width = (int)Math.Round(rect.Width),
                    Height = (int)Math.Round(rect.Height),
                });
                OnManualMasksEdited();
            };
            overlay.ManualMaskChanged += (id, rect) =>
            {
                var m2 = Settings.ManualMasks.FirstOrDefault(x => x.Id == id);
                if (m2 is null) return;
                m2.X = (int)Math.Round(rect.X);
                m2.Y = (int)Math.Round(rect.Y);
                m2.Width = (int)Math.Round(rect.Width);
                m2.Height = (int)Math.Round(rect.Height);
                OnManualMasksEdited();
            };
            overlay.ManualMaskDeleted += RemoveManualMask;
            overlay.Show();
            _overlays.Add(overlay);
        }
        RaiseKeptWindow();
        _forceRender = true;
        Tick();
    }

    /// <summary>操作パネルをマスクより前に出す。</summary>
    public void RaiseKeptWindow()
    {
        var h = KeepAboveOverlays;
        if (h == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------------
    // 設定
    // ------------------------------------------------------------------

    private void ApplyAppearance()
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(Settings.Appearance.SolidColor)!;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            _solidBrush = brush;
        }
        catch (Exception)
        {
            // 不正な色指定(空文字・書式誤り)は黒で塗る
            _solidBrush = Brushes.Black;
            LastError = $"塗りつぶし色「{Settings.Appearance.SolidColor}」を解釈できないため黒を使います";
        }
    }

    private WorkerConfig BuildWorkerConfig()
    {
        var d = Settings.Detection;
        return new WorkerConfig(
            d.Enabled,
            d.ToDetectorOptions(),
            d.Capture,
            d.UseEnglishEngine,
            Math.Clamp(d.IntervalMs, 150, 10_000),
            Math.Clamp(d.HoldMs, 0, 60_000),
            Math.Clamp(d.PaddingPx, 0, 100),
            Math.Clamp(Settings.Appearance.MosaicBlockSize, 4, 100),
            Math.Clamp(Settings.Appearance.BlurStrength, 4, 100),
            Math.Clamp(Settings.Appearance.MosaicRefreshMs, 100, 5000),
            d.MaskUnverifiedChanges,
            Math.Clamp(d.ChangeCheckMs, 50, 2000),
            LargeChangeFraction);
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _wheelMonitor.Dispose();
        _timer.Stop();
        _worker.Dispose();
        foreach (var o in _overlays) o.Close();
        _overlays.Clear();
    }
}
