using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using StageMask.App.Capture;
using StageMask.App.Native;
using StageMask.App.Ocr;
using StageMask.Core.Detection;
using StageMask.Core.Geometry;
using StageMask.Core.Imaging;
using StageMask.Core.Settings;

namespace StageMask.App.Services;

/// <summary>キャプチャ画像のサイズと、その左上がウィンドウのどこに当たるか。</summary>
internal sealed record FrameGeometry(CaptureOrigin Origin, int Width, int Height);

/// <summary>検出結果に付随する情報(座標変換用のジオメトリと、モザイク/ぼかし画像)。</summary>
internal sealed record DetectionPayload(FrameGeometry Geometry, ImageSource? Image);

/// <summary>ワーカーに処理させる対象ウィンドウ。</summary>
internal sealed record WorkTarget(
    IntPtr Hwnd,
    WindowInfo Window,
    bool Ocr,                 // OCRで機密情報を検出する
    MaskStyle DetectStyle,    // 検出領域のマスクの見た目
    bool FullImage,           // ウィンドウ全体のモザイク/ぼかし画像を作る
    MaskStyle WindowStyle);   // ウィンドウ全体マスクの見た目

/// <summary>モザイク/ぼかし表示の手動マスク(画面の範囲を取り込んで縮小画像を作る)。</summary>
internal sealed record ManualMosaicTarget(Guid Id, RectD ScreenRect, MaskStyle Style);

/// <summary>ワーカーの設定(UIスレッドで作って丸ごと差し替える)。</summary>
internal sealed record WorkerConfig(
    bool DetectionEnabled,
    DetectorOptions DetectorOptions,
    CaptureMethod Capture,
    bool UseEnglish,
    int IntervalMs,
    int HoldMs,
    int PaddingPx,
    int MosaicBlockSize,
    int BlurStrength,
    int MosaicRefreshMs,
    bool ChangeMask,       // 確認前の変化を先に隠す
    int ChangeCheckMs,     // 変化を確認する間隔
    bool IgnoreClocks,     // 時計・タイマーのような数字だけの小さな変化は先に隠さない
    bool VideoTileMask,    // 動き続けるウィンドウ(会議の映像など)は全体ではなく変わった部分だけ隠す
    double LargeChangeFraction); // この割合以上のタイルが変わったら「大きな変化」とする

/// <summary>未確認(OCRでまだ検査していない)の範囲。座標はキャプチャ画像の座標。</summary>
/// <param name="Fraction">未確認のタイルの割合(0〜1)。大きいときはウィンドウ全体を隠す</param>
/// <param name="LastMotionUtc">前回の確認から大きく動いた(スクロールなど)最後の時刻</param>
/// <param name="VideoLike">動き続けている(会議の映像・動画など)。スクロールではない</param>
internal sealed record PendingSnapshot(IReadOnlyList<RectD> Rects, FrameGeometry Geometry, double Fraction, DateTime LastMotionUtc, bool VideoLike);

/// <summary>
/// 自動検出の対象ウィンドウごとの状態。
/// Verified は「最後にOCRで検査した内容」の指紋。いま取り込んだ内容と比べて違う部分が未確認(Pending)。
/// </summary>
internal sealed class WindowScanState
{
    public readonly object Gate = new();
    public FrameSignature? Verified;
    public CapturedFrame? LatestFrame;
    public FrameSignature? LatestSig;
    public DateTime LatestUtc;
    public FrameGeometry? Geometry;
    public List<RectD> Pending = new();
    public double PendingFraction;
    public double LastMotionFraction;

    /// <summary>会議の映像・動画のように動き続けているか(判断の中身は VideoMotionTracker)</summary>
    public readonly VideoMotionTracker Video = new();
    public bool IsVideoLike(DateTime now) => Video.IsVideoLike(now);
    /// <summary>映像のウィンドウで、一度隠したマスをしばらく隠したままにする(ちらつき防止)</summary>
    private readonly StickyTiles _sticky = new();
    public static readonly TimeSpan VideoPendingHold = TimeSpan.FromMilliseconds(1500);
    public DateTime LastMotionUtc;

    /// <summary>Verified と比べた未確認の範囲を更新する(Gate をロックした状態で呼ぶ)。</summary>
    /// <summary>時計・タイマーのような数字だけのマス(毎秒変わっても先に隠さない)</summary>
    public ClockTileMap? Clocks;

    public void UpdatePending(FrameSignature sig, bool changeMask, bool ignoreClocks, bool videoLike = false)
    {
        if (!changeMask)
        {
            Pending = new List<RectD>();
            PendingFraction = 0;
            _sticky.Clear();
            return;
        }
        var now = DateTime.UtcNow;
        var changed = sig.ChangedTiles(Verified);
        // 時計・会議タイマーのように数字だけが変わる小さな変化は、先に隠さない(OCR は行う)
        if (ignoreClocks && Clocks is not null && Clocks.IsSameLayout(sig))
            changed = Clocks.ExcludeClockTiles(changed, now);
        PendingFraction = sig.TileCount == 0 ? 0 : (double)changed.Count / sig.TileCount;
        // 隣のタイルまで広げて隠す(行の境目にまたがった文字・確認の合間に変わった部分の取りこぼしを防ぐ)
        var tiles = sig.Dilate(changed, 1);
        // 映像のウィンドウでは、確認のたびにマスクが消えたり出たりしないよう、少しの間は隠したままにする
        if (videoLike) tiles = _sticky.Apply(tiles, sig.Columns, sig.Rows, now, VideoPendingHold);
        else _sticky.Clear();
        Pending = DetectionWorker.TilesToRects(sig, tiles);
    }
}

/// <summary>
/// バックグラウンドで「キャプチャ → OCR → 検出」を繰り返す。
/// UIスレッドとは、対象一覧(_getTargets)・検出結果(HoldBuffer)・ウィンドウ画像(_setWindowImage)でやり取りする。
/// </summary>
internal sealed class DetectionWorker : IDisposable
{
    // ---- 調査用: OCR の読み取り結果を一定時間だけファイルに書き出す ----
    private long _dumpUntilTicks;
    private readonly object _dumpGate = new();
    private string? _dumpPath;

    /// <summary>
    /// これから seconds 秒の間に行う OCR の結果(単語・位置・どの検出に当たったか)を書き出す。書き出し先のパスを返す。
    /// 画面の文字がそのまま保存されるため、ダミーのデータを表示しているときだけ使う。
    /// </summary>
    public string StartOcrDump(int seconds)
    {
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StageMask", "ocr-dump");
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, $"ocr-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        lock (_dumpGate)
        {
            _dumpPath = path;
            System.IO.File.WriteAllText(path, $"StageMask OCR dump {AppInfo.Version} {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n", new System.Text.UTF8Encoding(true));
        }
        Interlocked.Exchange(ref _dumpUntilTicks, DateTime.UtcNow.AddSeconds(seconds).Ticks);
        _forceFullOcr = true;
        return path;
    }

    private volatile bool _forceFullOcr;

    private void DumpOcr(string app, int band, int engine, IReadOnlyList<OcrLineBox> lines, IReadOnlyList<DetectionResult> hits)
    {
        if (DateTime.UtcNow.Ticks > Interlocked.Read(ref _dumpUntilTicks)) return;
        var sb = new System.Text.StringBuilder();
        sb.Append("=== ").Append(app).Append(" band=").Append(band).Append(" engine=").Append(engine).Append('\n');
        for (int li = 0; li < lines.Count; li++)
        {
            sb.Append("line ").Append(li).Append(':');
            foreach (var w in lines[li].Words)
                sb.Append(" [").Append(w.Text).Append(' ').Append($"{w.Box.X:0},{w.Box.Y:0},{w.Box.Width:0}x{w.Box.Height:0}").Append(']');
            sb.Append('\n');
        }
        foreach (var h in hits)
            sb.Append("hit ").Append(h.Category).Append(' ').Append($"{h.Box.X:0},{h.Box.Y:0},{h.Box.Width:0}x{h.Box.Height:0}").Append(" '").Append(h.Text).Append("'\n");
        lock (_dumpGate)
        {
            try { if (_dumpPath is not null) System.IO.File.AppendAllText(_dumpPath, sb.ToString()); }
            catch (Exception ex) { Logger.Write("OCR dump", ex); }
        }
    }

    private readonly Func<IReadOnlyList<WorkTarget>> _getTargets;
    private readonly DetectionHoldBuffer<DetectionPayload> _hold;
    private readonly ScanStatusTracker _scanStatus;
    private readonly Action<IntPtr, ImageSource?> _setWindowImage;
    private readonly Func<IReadOnlyList<ManualMosaicTarget>> _getManualTargets;
    private readonly Action<Guid, ImageSource?> _setManualImage;
    private readonly Func<IReadOnlyList<WindowInfo>> _getWindowsTopToBottom;
    // 手動マスクのモザイク用の合成では、黄色い枠が出ない PrintWindow を常に使う
    private readonly PrintWindowCapturer _composeCapturer = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile WorkerConfig _config;
    private Task? _loop;
    private Task? _checkLoop;
    private readonly ConcurrentDictionary<IntPtr, WindowScanState> _states = new();
    private readonly object _componentGate = new();
    private int _rotate;
    // 取り込みは同時に1つだけ(Windows.Graphics.Capture のセッションを2つのループで取り合わないように)
    private readonly SemaphoreSlim _captureGate = new(1, 1);

    /// <summary>
    /// 変化の確認ですべての対象を1回ずつ見終わるまでの時間の目安。
    /// 手前の3つは毎回、それ以外は1回に1つずつ順番に見るため、対象が多いと長くなる。
    /// </summary>
    public TimeSpan CheckRotationPeriod { get; private set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 確認2回続けてこの割合以上が変わったら「動いている」とする。
    /// 文字入力(1回に1〜2マス)では届かず、スクロール(表示中の文字のマスがほぼすべて変わる)では届く値。
    /// </summary>
    private const double ContinuousMotionFraction = 0.03;

    private IWindowCapturer? _capturer;
    private CaptureMethod _capturerKind;
    private OcrService? _ocr;
    private bool _ocrEnglish;
    private SensitiveTextDetector? _detector;
    private DetectorOptions? _detectorOptions;

    public string OcrStatus { get; private set; } = "初期化中";
    public string CaptureStatus { get; private set; } = "";
    public string? LastError { get; private set; }
    public TimeSpan LastCycle { get; private set; }
    /// <summary>変化の確認1回分にかかった時間</summary>
    public TimeSpan LastCheckRound { get; private set; }

    // 診断ログ用の累計カウンター(読む側が前回との差を取る)
    private long _ocrFull, _ocrBands, _ocrSkipped, _captureFailures;
    private long _captureTicks, _signatureTicks; // 変化の確認での取り込み・指紋計算にかかった時間(Stopwatch のティック)
    public double CaptureMsTotal => Interlocked.Read(ref _captureTicks) * 1000.0 / Stopwatch.Frequency;
    public double SignatureMsTotal => Interlocked.Read(ref _signatureTicks) * 1000.0 / Stopwatch.Frequency;
    private long _maxCaptureTicks; // 1回の取り込みにかかった最長時間(読むとリセット)
    /// <summary>前回呼んでからの、1回の取り込みの最長時間(ミリ秒)を返してリセットする。</summary>
    public double TakeMaxCaptureMs() => Interlocked.Exchange(ref _maxCaptureTicks, 0) * 1000.0 / Stopwatch.Frequency;
    public long OcrFullCount => Interlocked.Read(ref _ocrFull);
    public long OcrBandCount => Interlocked.Read(ref _ocrBands);
    public long OcrSkippedCount => Interlocked.Read(ref _ocrSkipped);
    public long CaptureFailureCount => Interlocked.Read(ref _captureFailures);

    /// <summary>未確認の範囲を返す(UIスレッドから呼ぶ)。対象でなければ null。</summary>
    public PendingSnapshot? GetPending(IntPtr hwnd)
    {
        if (!_states.TryGetValue(hwnd, out var st)) return null;
        lock (st.Gate)
        {
            if (st.Geometry is null) return null;
            return new PendingSnapshot(st.Pending.ToArray(), st.Geometry, st.PendingFraction, st.LastMotionUtc,
                                       _config.VideoTileMask && st.IsVideoLike(DateTime.UtcNow));
        }
    }

    public DetectionWorker(Func<IReadOnlyList<WorkTarget>> getTargets,
                           DetectionHoldBuffer<DetectionPayload> hold,
                           ScanStatusTracker scanStatus,
                           Action<IntPtr, ImageSource?> setWindowImage,
                           Func<IReadOnlyList<ManualMosaicTarget>> getManualTargets,
                           Action<Guid, ImageSource?> setManualImage,
                           Func<IReadOnlyList<WindowInfo>> getWindowsTopToBottom,
                           WorkerConfig config)
    {
        _getWindowsTopToBottom = getWindowsTopToBottom;
        _getManualTargets = getManualTargets;
        _setManualImage = setManualImage;
        _getTargets = getTargets;
        _hold = hold;
        _scanStatus = scanStatus;
        _setWindowImage = setWindowImage;
        _config = config;
    }

    public void UpdateConfig(WorkerConfig config) => _config = config;

    /// <summary>
    /// 対象から外れたウィンドウの状態(確認済みの内容)を捨てる。UIスレッドから毎フレーム呼ぶ。
    /// UI側で検出結果を捨てたのに「確認済み」が残ると、次に対象に戻ったとき
    /// 「変化なし → OCRしない」となり、機密情報がマスクなしで表示されてしまうため。
    /// </summary>
    public void RetainStates(IReadOnlyCollection<IntPtr> hwnds)
    {
        foreach (var key in _states.Keys)
            if (!hwnds.Contains(key)) _states.TryRemove(key, out _);
    }

    private async Task<CapturedFrame?> CaptureSerializedAsync(IWindowCapturer capturer, IntPtr hwnd, CancellationToken ct)
    {
        // 同時に取り込める方式(PrintWindow)なら順番待ちしない
        if (capturer.SupportsConcurrentCapture) return await capturer.CaptureAsync(hwnd, ct).ConfigureAwait(false);
        await _captureGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await capturer.CaptureAsync(hwnd, ct).ConfigureAwait(false); }
        finally { _captureGate.Release(); }
    }

    private Task? _manualLoop;

    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
        // 変化の確認(0.1秒ごと): OCRの完了を待たずに、未確認の表示を先に隠すため
        _checkLoop ??= Task.Run(() => CheckLoopAsync(_cts.Token));
        // 手動マスクのモザイク/ぼかしは、OCRより短い間隔で別に更新する(下の画面の変化に早く追従させるため)
        _manualLoop ??= Task.Run(() => ManualLoopAsync(_cts.Token));
    }

    private async Task ManualLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = _config;
            try
            {
                await RunManualOnceAsync(cfg, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Write("ManualMosaic", ex);
            }
            try { await Task.Delay(Math.Max(100, cfg.MosaicRefreshMs), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunManualOnceAsync(WorkerConfig cfg, CancellationToken ct)
    {
        // 手動マスクのモザイク/ぼかし: マスクの下のウィンドウを重ね合わせて画像を作る
        var manualTargets = _getManualTargets();
        if (manualTargets.Count > 0)
        {
            // ウィンドウの位置と重なり順は、毎回その場で取り直す(動かした直後のずれを減らすため)
            IReadOnlyList<WindowInfo> windows;
            try { windows = WindowEnumerator.GetWindows(); }
            catch (Exception) { windows = _getWindowsTopToBottom(); }
            var cache = new Dictionary<IntPtr, CapturedFrame?>(); // 同じサイクル内では1ウィンドウ1回だけ取り込む
            async Task<CapturedFrame?> CaptureCached(IntPtr h)
            {
                if (!cache.TryGetValue(h, out var f))
                {
                    try { f = await _composeCapturer.CaptureAsync(h, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { f = null; }
                    cache[h] = f;
                }
                return f;
            }

            foreach (var m in manualTargets)
            {
                ct.ThrowIfCancellationRequested();
                var shot = await WindowCompositeCapturer.ComposeAsync(m.ScreenRect, windows, CaptureCached, ct).ConfigureAwait(false);
                if (shot is null) continue; // 画像がない間は塗りつぶしで表示される
                _setManualImage(m.Id, ImageFactory.CreateMaskImage(shot, new RectD(0, 0, shot.Width, shot.Height),
                                                                    m.Style, cfg.MosaicBlockSize, cfg.BlurStrength));
            }
        }

    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            var cfg = _config;
            try
            {
                await RunOnceAsync(cfg, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Logger.Write("DetectionWorker", ex);
            }
            LastCycle = sw.Elapsed;

            int wait = Math.Max(100, cfg.IntervalMs - (int)sw.ElapsedMilliseconds);
            try { await Task.Delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void EnsureComponents(WorkerConfig cfg)
    {
        lock (_componentGate) EnsureComponentsCore(cfg);
    }

    private void EnsureComponentsCore(WorkerConfig cfg)
    {
        if (_capturer is null || _capturerKind != cfg.Capture)
        {
            _capturer?.Dispose();
            if (cfg.Capture == CaptureMethod.GraphicsCapture && GraphicsCaptureCapturer.IsSupported())
            {
                _capturer = new GraphicsCaptureCapturer();
                CaptureStatus = "Windows.Graphics.Capture";
            }
            else
            {
                _capturer = new PrintWindowCapturer();
                CaptureStatus = cfg.Capture == CaptureMethod.GraphicsCapture
                    ? "PrintWindow(Graphics Capture 非対応のため切り替え)"
                    : "PrintWindow";
            }
            _capturerKind = cfg.Capture;
        }

        if (_ocr is null || _ocrEnglish != cfg.UseEnglish)
        {
            _ocr = new OcrService(cfg.UseEnglish);
            _ocrEnglish = cfg.UseEnglish;
            OcrStatus = _ocr.Status;
        }

        if (_detector is null || !ReferenceEquals(_detectorOptions, cfg.DetectorOptions))
        {
            bool changed = _detector is not null;
            _detector = new SensitiveTextDetector(cfg.DetectorOptions);
            _detectorOptions = cfg.DetectorOptions;
            if (changed)
            {
                // 検出する情報・NGワードが変わったら、どのウィンドウも全体を読み直す。
                // 「確認済み」のままだと、内容が変わるまで古い判定(追加したNGワードが隠れない、
                // OFFにした種類が隠れたまま)が続くため。これまでの検出結果は読み直しで置き換わるまで残す(安全側)
                foreach (var st in _states.Values)
                    lock (st.Gate) st.Verified = null;
            }
        }
    }

    // ------------------------------------------------------------------
    // 変化の確認(高速ループ)
    // ------------------------------------------------------------------

    private async Task CheckLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            var cfg = _config;
            try
            {
                if (cfg.DetectionEnabled) await RunCheckOnceAsync(cfg, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Write("ChangeCheck", ex);
            }
            LastCheckRound = sw.Elapsed;
            int wait = Math.Max(30, cfg.ChangeCheckMs - (int)sw.ElapsedMilliseconds);
            try { await Task.Delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunCheckOnceAsync(WorkerConfig cfg, CancellationToken ct)
    {
        EnsureComponents(cfg);
        IWindowCapturer capturer;
        OcrService ocr;
        lock (_componentGate) { capturer = _capturer!; ocr = _ocr!; }

        var targets = _getTargets().Where(t => t.Ocr).ToList();
        if (targets.Count == 0) return;

        // 負荷を抑えるため、毎回確認するのは手前の3つまで。残りは1回に1つずつ順番に確認する
        var round = targets.Take(3).ToList();
        int restCount = Math.Max(0, targets.Count - 3);
        if (restCount > 0)
        {
            var rest = targets.Skip(3).ToList();
            round.Add(rest[(int)((uint)_rotate++ % (uint)rest.Count)]);
        }
        double roundMs = Math.Max(cfg.ChangeCheckMs, LastCheckRound.TotalMilliseconds);
        CheckRotationPeriod = TimeSpan.FromMilliseconds(roundMs * Math.Max(1, restCount));
        // 確認済みの内容から変わっていない場所の検出結果は、次に確認するまで保持し続ける
        var holdUnchanged = TimeSpan.FromMilliseconds(Math.Max(cfg.HoldMs, 3 * CheckRotationPeriod.TotalMilliseconds + 1000));

        // 取り込み: 同時に取り込める方式なら全ウィンドウを同時に取り込む(1回の時間が「一番遅い1つ分」になる)
        async Task<(CapturedFrame? Frame, long Took)> CaptureTimedAsync(IntPtr hwnd)
        {
            long c0 = Stopwatch.GetTimestamp();
            CapturedFrame? f;
            try { f = await CaptureSerializedAsync(capturer, hwnd, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { f = null; Logger.Write("ChangeCheckCapture", ex); }
            return (f, Stopwatch.GetTimestamp() - c0);
        }

        (CapturedFrame? Frame, long Took)[] captures;
        if (capturer.SupportsConcurrentCapture)
        {
            captures = await Task.WhenAll(round.Select(t => CaptureTimedAsync(t.Hwnd))).ConfigureAwait(false);
        }
        else
        {
            captures = new (CapturedFrame?, long)[round.Count];
            for (int i = 0; i < round.Count; i++) captures[i] = await CaptureTimedAsync(round[i].Hwnd).ConfigureAwait(false);
        }

        for (int ri = 0; ri < round.Count; ri++)
        {
            var t = round[ri];
            ct.ThrowIfCancellationRequested();
            long id = t.Hwnd.ToInt64();
            var (frame, took) = captures[ri];
            // check_capture_ms は「取り込みにかかった時間の合計」(同時に取り込んだ分も足す)
            Interlocked.Add(ref _captureTicks, took);
            long cur;
            while (took > (cur = Interlocked.Read(ref _maxCaptureTicks)) &&
                   Interlocked.CompareExchange(ref _maxCaptureTicks, took, cur) != cur) { }

            var now = DateTime.UtcNow;
            if (frame is null)
            {
                _scanStatus.RecordFailure(id, now, "画面を取り込めません");
                Interlocked.Increment(ref _captureFailures);
                continue;
            }
            if (FrameHeuristics.IsLikelyBlackFrame(frame.Pixels, frame.Width, frame.Height, frame.Stride))
            {
                _scanStatus.RecordFailure(id, now, "取り込んだ画像が真っ黒です(キャプチャ方式の切り替えを検討してください)");
                continue;
            }

            var state = _states.GetOrAdd(t.Hwnd, _ => new WindowScanState());
            CapturedFrame? prevFrame;
            FrameSignature? prevSig;
            lock (state.Gate) { prevFrame = state.LatestFrame; prevSig = state.LatestSig; }
            long s0 = Stopwatch.GetTimestamp();
            // 前回と画素がまったく同じタイルは計算を省く
            var sig = FrameSignature.Compute(frame.Pixels, frame.Width, frame.Height, frame.Stride,
                                             prevSig, prevFrame?.Pixels, prevFrame?.Stride ?? 0);
            Interlocked.Add(ref _signatureTicks, Stopwatch.GetTimestamp() - s0);
            lock (state.Gate)
            {
                // 前回の確認から大きく変わった(スクロール・ページ切り替えなど)かどうか
                if (state.LatestSig is { } prev && prev.IsSameLayout(sig) && sig.TileCount > 0)
                {
                    int changedCount = sig.ChangedTiles(prev).Count;
                    double motion = (double)changedCount / sig.TileCount;
                    // 大きく変わった、または中くらいの変化が続いている(タッチパッドのゆっくりしたスクロールなど)
                    bool moving = motion >= cfg.LargeChangeFraction ||
                                  (motion >= ContinuousMotionFraction && state.LastMotionFraction >= ContinuousMotionFraction);
                    if (moving) state.LastMotionUtc = now;
                    // 内容が上下にずれていればスクロール(動画ではない)
                    bool scrolled = moving && sig.EstimateVerticalShift(prev) is not null;
                    state.Video.Observe(now, changedCount, scrolled);
                    state.LastMotionFraction = motion;
                }
                state.LatestFrame = frame;
                state.LatestSig = sig;
                state.LatestUtc = now;
                state.Geometry = new FrameGeometry(frame.Origin, frame.Width, frame.Height);
                state.UpdatePending(sig, cfg.ChangeMask, cfg.IgnoreClocks, cfg.VideoTileMask && state.IsVideoLike(now));
                // 内容が変わっていない場所の検出結果は、まだ有効なので期限を延ばす
                // (延ばさないと、OCRの順番待ちの間に期限切れになり、変わっていない機密情報が見えてしまう)
                if (cfg.ChangeMask) _hold.ExtendOutside(id, state.Pending, now, holdUnchanged);
            }

            // 未確認の部分は Pending で隠れるので、取り込めていれば「検査できている」とみなす。
            // (変化を先に隠す設定がOFFのときは、従来どおりOCRの完了でのみ成功とする)
            if (cfg.ChangeMask && ocr.IsAvailable) _scanStatus.RecordSuccess(id, now);
            else if (!ocr.IsAvailable) _scanStatus.RecordFailure(id, now, "OCRエンジンが使えません");
        }
    }

    /// <summary>変化したタイルを、行ごとに横につなげた矩形にする(描画する数を減らすため)。</summary>
    internal static List<RectD> TilesToRects(FrameSignature sig, List<(int Column, int Row)> tiles)
    {
        var rects = new List<RectD>();
        foreach (var row in tiles.GroupBy(t => t.Row))
        {
            var cols = row.Select(t => t.Column).OrderBy(c => c).ToList();
            int start = cols[0], prev = cols[0];
            for (int i = 1; i <= cols.Count; i++)
            {
                if (i < cols.Count && cols[i] == prev + 1) { prev = cols[i]; continue; }
                rects.Add(sig.TileRect(start, row.Key).Union(sig.TileRect(prev, row.Key)));
                if (i < cols.Count) { start = prev = cols[i]; }
            }
        }
        return rects;
    }

    private int PendingCount(IntPtr hwnd)
    {
        if (!_states.TryGetValue(hwnd, out var st)) return int.MaxValue; // まだ一度も見ていない → 最優先
        lock (st.Gate) return st.Pending.Count;
    }

    // ------------------------------------------------------------------
    // OCR(変化した行だけ読み取る)
    // ------------------------------------------------------------------

    private async Task RunOnceAsync(WorkerConfig cfg, CancellationToken ct)
    {
        EnsureComponents(cfg);
        IWindowCapturer capturer;
        OcrService ocr;
        SensitiveTextDetector detector;
        lock (_componentGate) { capturer = _capturer!; ocr = _ocr!; detector = _detector!; }

        // 未確認の部分が多いウィンドウから先に検査する
        var targets = _getTargets().OrderByDescending(t => t.Ocr ? PendingCount(t.Hwnd) : -1).ToList();
        var handles = targets.Select(t => t.Hwnd).ToList();
        await _captureGate.WaitAsync(ct).ConfigureAwait(false);
        try { capturer.RetainOnly(handles); }
        finally { _captureGate.Release(); }
        foreach (var key in _states.Keys)
            if (!handles.Contains(key)) _states.TryRemove(key, out _);

        string? cycleError = null;
        foreach (var t in targets)
        {
            ct.ThrowIfCancellationRequested();
            long id = t.Hwnd.ToInt64();
            bool wantOcr = t.Ocr && cfg.DetectionEnabled;
            if (!wantOcr && !t.FullImage) continue;

            // 「検査できなかった」ことを記録する。記録されたウィンドウは、UI側でウィンドウ全体が隠される(フェイルクローズ)
            void Fail(string reason)
            {
                if (wantOcr) _scanStatus.RecordFailure(id, DateTime.UtcNow, reason);
                cycleError = $"{t.Window.ProcessName}: {reason}";
            }

            if (wantOcr && !ocr.IsAvailable)
            {
                Fail("OCRエンジンが使えません");
                if (!t.FullImage) continue;
            }

            // 変化の確認ループが直前に取り込んだ画像があれば使う(取り込みの重複を避ける)
            var state = _states.GetOrAdd(t.Hwnd, _ => new WindowScanState());
            CapturedFrame? frame = null;
            FrameSignature? sig = null;
            lock (state.Gate)
            {
                if (state.LatestFrame is not null && state.LatestSig is not null &&
                    (DateTime.UtcNow - state.LatestUtc).TotalMilliseconds < 300)
                {
                    frame = state.LatestFrame;
                    sig = state.LatestSig;
                }
            }
            if (frame is null)
            {
                try
                {
                    frame = await CaptureSerializedAsync(capturer, t.Hwnd, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Fail("画面を取り込めません(" + ex.Message + ")");
                    Interlocked.Increment(ref _captureFailures);
                    continue;
                }
                if (frame is null)
                {
                    Fail("画面を取り込めません");
                    Interlocked.Increment(ref _captureFailures);
                    continue;
                }
            }

            var geometry = new FrameGeometry(frame.Origin, frame.Width, frame.Height);

            if (t.FullImage)
            {
                var crop = FrameRectInImage(t.Window, frame);
                _setWindowImage(t.Hwnd, ImageFactory.CreateMaskImage(frame, crop, t.WindowStyle, cfg.MosaicBlockSize, cfg.BlurStrength));
            }

            if (!wantOcr || !ocr.IsAvailable) continue;

            // 真っ黒な画像は取り込み失敗とみなす(PrintWindow が描画できないアプリで起きる)
            if (FrameHeuristics.IsLikelyBlackFrame(frame.Pixels, frame.Width, frame.Height, frame.Stride))
            {
                Fail("取り込んだ画像が真っ黒です(キャプチャ方式の切り替えを検討してください)");
                continue;
            }

            try
            {
                sig ??= FrameSignature.Compute(frame.Pixels, frame.Width, frame.Height, frame.Stride);
                FrameSignature? verified;
                lock (state.Gate) verified = state.Verified;

                var hold = TimeSpan.FromMilliseconds(Math.Max(0, cfg.HoldMs));
                var dirty = sig.ChangedTiles(verified);
                // 調査用の書き出し中は、変化がなくても全体を読み直す
                if (dirty.Count == 0 && _forceFullOcr && DateTime.UtcNow.Ticks <= Interlocked.Read(ref _dumpUntilTicks))
                    dirty = Enumerable.Range(0, sig.Rows).SelectMany(r => Enumerable.Range(0, sig.Columns).Select(c => (c, r))).ToList();
                if (dirty.Count == 0)
                {
                    // 内容が変わっていない: OCRし直さず、これまでの検出結果を保持し続ける
                    Interlocked.Increment(ref _ocrSkipped);
                    var t0 = DateTime.UtcNow;
                    _hold.ExtendOutside(id, Array.Empty<RectD>(), t0, hold);
                    _scanStatus.RecordSuccess(id, t0);
                    continue;
                }

                // 変化した行(と上下1行)だけを横帯にしてOCRする。変化が広い・初回は全体を読む
                var bands = sig.Bands(dirty, margin: 1);
                double bandArea = bands.Sum(b => sig.BandRect(b.RowStart, b.RowEnd).Area);
                bool dumping = DateTime.UtcNow.Ticks <= Interlocked.Read(ref _dumpUntilTicks);
                if (!sig.IsSameLayout(verified) || bandArea > 0.6 * frame.Width * frame.Height || (dumping && _forceFullOcr))
                    bands = new List<(int RowStart, int RowEnd)> { (0, sig.Rows - 1) };
                var bandRects = bands.Select(b => sig.BandRect(b.RowStart, b.RowEnd)).ToList();
                if (bands.Count == 1 && bands[0].RowStart == 0 && bands[0].RowEnd == sig.Rows - 1) Interlocked.Increment(ref _ocrFull);
                else Interlocked.Increment(ref _ocrBands);

                var bounds = new RectD(0, 0, frame.Width, frame.Height);
                var items = new List<(RectD, DetectionCategory, DetectionPayload?)>();
                var words = new List<OcrWordBox>(); // 時計のマスの判定用(画像全体の座標)
                foreach (var band in bandRects)
                {
                    int by = (int)band.Y, bh = (int)band.Height;
                    if (bh <= 0) continue;
                    var bandFrame = new CapturedFrame(frame.Crop(0, by, frame.Width, bh), frame.Width, bh, frame.Width * 4, frame.Origin);
                    var perEngine = await ocr.RecognizeAsync(bandFrame, ct).ConfigureAwait(false);
                    for (int ei = 0; ei < perEngine.Count; ei++)
                    {
                        var lines = perEngine[ei];
                        foreach (var line in lines)
                            foreach (var w in line.Words)
                                words.Add(w with { Box = w.Box.Offset(0, by) });
                        var hits = detector.Detect(lines);
                        if (dumping) DumpOcr(t.Window.ProcessName, by, ei, lines, hits);
                        foreach (var d in hits)
                        {
                            var r = d.Box.Offset(0, by).Inflate(cfg.PaddingPx).Intersect(bounds);
                            if (r.IsEmpty) continue;
                            var image = ImageFactory.CreateMaskImage(frame, r, t.DetectStyle, cfg.MosaicBlockSize, cfg.BlurStrength);
                            items.Add((r, d.Category, new DetectionPayload(geometry, image)));
                        }
                    }
                }

                var now = DateTime.UtcNow;
                // 検出結果を登録してから「確認済み」を更新する(先に更新すると、マスクがない一瞬ができる)
                // 内容が変わったマスにあった古い検出は消す(スクロールで文字が動くと、古い位置のマスクがずれて残るため)
                var dirtyRects = TilesToRects(sig, dirty);
                _hold.ReplaceIn(id, dirtyRects, items, now, hold);
                // 変わっていない場所の検出結果は保持し続ける。帯の中でも、変わっていないマスにある検出は
                // 今回のOCRで読み落としても消さない(OCRは毎回同じ結果になるとは限らないため)
                _hold.ExtendOutside(id, dirtyRects, now, hold);
                lock (state.Gate)
                {
                    state.Verified = FrameSignature.WithRowsFrom(verified, sig, bands);
                    state.Geometry ??= geometry;
                    if (cfg.IgnoreClocks)
                    {
                        if (state.Clocks is null || !state.Clocks.IsSameLayout(sig)) state.Clocks = new ClockTileMap(sig);
                        state.Clocks.Update(dirty, words, items.Select(i => i.Item1), now);
                    }
                    if (state.LatestSig is not null) state.UpdatePending(state.LatestSig, cfg.ChangeMask, cfg.IgnoreClocks, cfg.VideoTileMask && state.IsVideoLike(DateTime.UtcNow));
                }
                _scanStatus.RecordSuccess(id, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Fail("OCRに失敗しました(" + ex.Message + ")");
                Logger.Write("OCR", ex);
            }
        }
        LastError = cycleError;
    }

    /// <summary>キャプチャ画像のうち、見た目どおりのウィンドウ範囲(拡張フレーム)に当たる部分。</summary>
    private static RectD FrameRectInImage(WindowInfo w, CapturedFrame f)
    {
        if (f.Origin == CaptureOrigin.WindowRect)
            return new RectD(w.FrameRect.X - w.WindowRect.X, w.FrameRect.Y - w.WindowRect.Y, w.FrameRect.Width, w.FrameRect.Height);
        return new RectD(0, 0, f.Width, f.Height);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* 終了時の例外は無視 */ }
        try { _checkLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* 終了時の例外は無視 */ }
        try { _manualLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* 終了時の例外は無視 */ }
        _capturer?.Dispose();
        _cts.Dispose();
    }
}
