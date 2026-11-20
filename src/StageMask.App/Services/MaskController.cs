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
internal sealed partial class MaskController : IDisposable
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
    private IntPtr _lastForeground;
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
    /// <summary>動き続けているため、変わった部分だけを隠しているウィンドウの数</summary>
    public int VideoWindowCount { get; private set; }
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

    /// <summary>LastError が指定の種類のエラーなら消す(直った後も状態欄に残り続けないように)。</summary>
    private void ClearError(string prefix)
    {
        if (LastError?.StartsWith(prefix, StringComparison.Ordinal) == true) LastError = null;
    }
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
        ApplyShareMirror();
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
        ApplyShareMirror();
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
            ClearError("設定の保存に失敗"); // 保存できたら、前回の失敗の表示を消す
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
        // 前面のウィンドウが切り替わったら、すぐに重なり順を取り直す
        // (古い重なり順のままだと、前に出てきたウィンドウのマスクを描き漏らすため)
        var foreground = NativeMethods.GetForegroundWindow();
        if (now - _lastEnumUtc >= EnumerateInterval || foreground != _lastForeground)
        {
            _lastForeground = foreground;
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
        var pendingByApp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _occludedMasks = 0;
        _occluderApps.Clear();
        var targets = new Dictionary<IntPtr, WorkTarget>();
        var detectIds = new List<long>();
        var manualTargets = new List<ManualMosaicTarget>();
        int hiddenCount = 0, detectionMasks = 0, failClosed = 0, pendingMasks = 0, scrollMasks = 0, videoWindows = 0;
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
                // このウィンドウより手前にあるウィンドウ。そこに重なるマスクは描かない
                // (後ろのウィンドウのマスクが、手前のウィンドウの内容を隠してしまうため)
                var occluders = OccludersAbove(w);

                if (hide is not null)
                {
                    ImageSource? image = null;
                    if (hide.Style != MaskStyle.Solid && _windowImages.TryGetValue(w.Handle, out var img)) image = img;
                    var c = Content(hide.Content, hide.Style);
                    AddClipped(visuals, new MaskVisual(w.FrameRect.Inflate(WindowMaskPaddingPx), MaskKind.Window, hide.Style, image,
                                               Text: c.Text, ContentImage: _contentImages.Get(c.ImagePath)), occluders);
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
                    // 会議の映像・動画のように動き続けるウィンドウは、全体ではなく変わった部分だけを隠す(下の pending の描画)
                    if (pending is { VideoLike: true }) videoWindows++;
                    bool largeChange = Settings.Detection.MaskOnLargeChange && pending is not null && !pending.VideoLike &&
                                       (pending.Fraction >= LargeChangeFraction ||
                                        (now - pending.LastMotionUtc).TotalMilliseconds < Math.Clamp(Settings.Detection.WheelMaskMs, 100, 5000));
                    if (hide is null && (wheeling || largeChange))
                    {
                        AddClipped(visuals, new MaskVisual(w.FrameRect.Inflate(WindowMaskPaddingPx), MaskKind.Window, MaskStyle.Solid, null), occluders);
                        scrollMasks++;
                    }
                    // フェイルクローズ: 検査できていないウィンドウは、部分マスクではなく全体を隠す
                    else if (hide is null && Settings.Detection.FailClosed && !_scanStatus.IsSafe(id, now, maxScanAge))
                    {
                        var c = Content(detect.Content, MaskStyle.Solid);
                        AddClipped(visuals, new MaskVisual(w.FrameRect.Inflate(WindowMaskPaddingPx), MaskKind.Window, MaskStyle.Solid, null,
                                                   Text: c.Text, ContentImage: _contentImages.Get(c.ImagePath)), occluders);
                        failClosed++;
                        failReason ??= $"{w.ProcessName}({w.ClassName} {w.FrameRect.Width:0}x{w.FrameRect.Height:0} @{w.FrameRect.X:0},{w.FrameRect.Y:0}): {_scanStatus.GetFailureReason(id) ?? "検査待ち"}";
                    }
                    else if (hide is null) // 全体を隠しているウィンドウには重ねて描く必要がない
                    {
                        var c = Content(detect.Content, detect.Style);
                        var contentImage = _contentImages.Get(c.ImagePath);
                        foreach (var item in _hold.GetActive(id, now))
                        {
                            var screen = MapToScreen(item, w);
                            if (screen.IsEmpty) continue;
                            AddClipped(visuals, new MaskVisual(screen, MaskKind.Detection, detect.Style, item.Payload?.Image,
                                                       Text: c.Text, ContentImage: contentImage), occluders);
                            detectionMasks++;
                            if (Settings.Detection.ShowDebugBoxes)
                                AddClipped(visuals, new MaskVisual(screen, MaskKind.DebugBox, MaskStyle.Solid, null), occluders);
                        }

                        // まだOCRで検査していない(取り込み後に変化した)部分は、検査が終わるまで塗りつぶす
                        if (pending is not null)
                        {
                            foreach (var r in pending.Rects)
                            {
                                var screen = MapFrameRect(r, pending.Geometry, w).Inflate(2);
                                if (screen.IsEmpty) continue;
                                AddClipped(visuals, new MaskVisual(screen, MaskKind.Detection, MaskStyle.Solid, null), occluders);
                                pendingMasks++;
                                pendingByApp[w.ProcessName] = pendingByApp.GetValueOrDefault(w.ProcessName) + 1;
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
        VideoWindowCount = videoWindows;
        OccludedMaskCount = _occludedMasks;
        PendingApps = string.Join(" ", pendingByApp.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key}:{p.Value}"));
        OccluderApps = string.Join(" ", _occluderApps.OrderBy(a => a, StringComparer.OrdinalIgnoreCase));
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
        RenderMirrorMasks(state);
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

    /// <summary>
    /// w より手前(重なり順で上)にあり、後ろを覆い隠すウィンドウの範囲。
    /// w と重なるものだけ、位置を取り直して返す(列挙は 250ms ごとなので、動かした直後のずれを防ぐ)。
    /// </summary>
    private List<(RectD Rect, string App)> OccludersAbove(WindowInfo w)
    {
        var result = new List<(RectD Rect, string App)>();
        foreach (var o in _windows)
        {
            if (o.Handle == w.Handle) return result;
            if (!o.CanOcclude || o.IsMinimized) continue;
            if (!o.FrameRect.Inflate(64).IntersectsWith(w.FrameRect)) continue;
            var fresh = WindowEnumerator.RefreshGeometry(o);
            if (fresh is null || fresh.IsMinimized) continue;
            if (fresh.FrameRect.IntersectsWith(w.FrameRect)) result.Add((fresh.FrameRect, o.ProcessName));
        }
        // 列挙した一覧に w がない(列挙の後に現れた): 重なり順が分からないので、何も覆っていないとみなす(安全側)
        return new List<(RectD Rect, string App)>();
    }

    // 診断ログ用: 手前のウィンドウに重なったため省いた・切り抜いたマスクの数と、そのウィンドウのアプリ名(毎回数え直す)
    private int _occludedMasks;
    private readonly HashSet<string> _occluderApps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>未確認の部分を先に隠しているマスクの数を、アプリ名ごとに(例: "ms-teams:12")</summary>
    public string PendingApps { get; private set; } = "";
    /// <summary>手前のウィンドウに重なったため省いた・切り抜いたマスクの数(直近の描画時点)</summary>
    public int OccludedMaskCount { get; private set; }
    /// <summary>そのとき手前にあったウィンドウのアプリ名(プロセス名、空白区切り)</summary>
    public string OccluderApps { get; private set; } = "";

    /// <summary>
    /// 手前のウィンドウに重なる部分を除いてマスクを追加する。完全に隠れていれば追加しない。
    /// 塗りつぶしだけのマスクは、見えている部分だけに切り分ける。モザイク・ぼかし・文字や画像入りのマスクは
    /// 切り分けると見た目が崩れるため、1つのまま描いて、重なる部分を描画時に切り抜く。
    /// </summary>
    private void AddClipped(List<MaskVisual> visuals, MaskVisual v, List<(RectD Rect, string App)> occluders)
    {
        if (occluders.Count == 0) { visuals.Add(v); return; }
        var hitPairs = occluders.Where(o => o.Rect.IntersectsWith(v.ScreenRect)).ToList();
        if (hitPairs.Count == 0) { visuals.Add(v); return; }
        if (v.Kind != MaskKind.DebugBox)
        {
            _occludedMasks++;
            foreach (var h in hitPairs) _occluderApps.Add(h.App);
        }
        var hits = hitPairs.Select(h => h.Rect).ToList();
        if (RectD.IsFullyCovered(v.ScreenRect, hits)) return;
        if (v.Image is not null || !string.IsNullOrEmpty(v.Text) || v.ContentImage is not null || v.ManualId is not null)
        {
            // 切り分けると見た目が崩れるので、1つのまま描いて重なる部分だけ切り抜く
            visuals.Add(v with { Occluded = hits });
            return;
        }
        var pieces = new List<RectD> { v.ScreenRect };
        foreach (var o in hits)
        {
            var next = new List<RectD>();
            foreach (var r in pieces) next.AddRange(r.Subtract(o));
            pieces = next;
            pieces.RemoveAll(r => r.Width < 1 || r.Height < 1);
            if (pieces.Count > 64) { visuals.Add(v with { Occluded = hits }); return; } // 細かく割れすぎたら切り抜きで描く
        }
        foreach (var r in pieces) visuals.Add(v with { ScreenRect = r });
    }

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
        _mirrorLayoutDirty = true; // モニターの構成が変わったら、ミラーの取り込み元・表示先を選び直す
        UpdateOverlayModes();
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
            ClearError("塗りつぶし色"); // 正しい色に直したら、前回の警告を消す
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
            d.IgnoreClockChanges,
            d.VideoTileMask,
            LargeChangeFraction);
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _wheelMonitor.Dispose();
        _timer.Stop();
        _worker.Dispose();
        StopShareMirror();
        foreach (var o in _overlays) o.Close();
        _overlays.Clear();
    }
}
