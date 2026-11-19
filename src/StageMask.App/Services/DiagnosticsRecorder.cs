using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StageMask.Core.Diagnostics;

namespace StageMask.App.Services;

/// <summary>
/// 診断ログ(%APPDATA%\StageMask\diag\diag-日時.csv)を1秒ごとに書く。
/// 記録するのは数値と決まった文言だけで、画面の内容・NGワード・ウィンドウのタイトルは書かない。
/// </summary>
internal sealed class DiagnosticsRecorder
{
    public static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StageMask", "diag");

    private readonly MaskController _controller;
    private readonly ResourceMonitor _resources;
    private DiagnosticsLog? _log;
    private bool _wasEnabled;
    private long _lastFull, _lastBands, _lastSkipped, _lastCaptureFail, _lastWheel;
    private double _lastCaptureMs, _lastSignatureMs;

    public DiagnosticsRecorder(MaskController controller, ResourceMonitor resources)
    {
        _controller = controller;
        _resources = resources;
    }

    public string? CurrentPath => _log?.CurrentPath;
    public string? Error { get; private set; }

    /// <summary>1秒ごとに呼ぶ(ResourceMonitor.Sample の後)。</summary>
    public void Tick()
    {
        var cfg = _controller.Settings.Diagnostics;
        if (!cfg.Enabled)
        {
            if (_wasEnabled) _log?.Rotate(); // 次に ON にしたら新しいファイルにする
            _wasEnabled = false;
            _controller.TakeEvents(); // 記録しない間の出来事は捨てる
            _controller.TakeRenderStats();
            return;
        }

        var events = new List<string>();
        if (!_wasEnabled)
        {
            _log = new DiagnosticsLog(DefaultDirectory,
                Math.Clamp(cfg.MaxFileMb, 1, 500) * 1024L * 1024L,
                Math.Clamp(cfg.MaxFiles, 1, 1000));
            ResetCounters();
            events.Add($"記録開始 v{AppInfo.Version} 論理CPU{Environment.ProcessorCount} {Environment.OSVersion.VersionString}");
            _wasEnabled = true;
        }

        // 同じ出来事が続いたときは「×回数」にまとめる
        events.AddRange(_controller.TakeEvents()
            .GroupBy(e => e)
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Key}×{g.Count()}"));

        var c = _controller;
        var render = c.TakeRenderStats();
        var sample = new DiagnosticsSample
        {
            Time = DateTime.UtcNow,
            CpuPercent = _resources.CpuPercent,
            WorkingSetMb = _resources.WorkingSetMb,
            PrivateMb = _resources.PrivateMb,
            GcHeapMb = GC.GetTotalMemory(false) / 1024.0 / 1024.0,
            Threads = _resources.Threads,
            Overlays = c.OverlayCount,
            HiddenWindows = c.HiddenWindowCount,
            DetectTargets = c.DetectTargetCount,
            DetectionMasks = c.DetectionMaskCount,
            PendingMasks = c.PendingMaskCount,
            ScrollMasks = c.ScrollMaskCount,
            VideoWindows = c.VideoWindowCount,
            FailClosed = c.FailClosedCount,
            OcrCycleMs = (int)c.WorkerCycle.TotalMilliseconds,
            CheckRoundMs = (int)c.CheckRound.TotalMilliseconds,
            OcrFull = Delta(c.OcrFullCount, ref _lastFull),
            OcrBands = Delta(c.OcrBandCount, ref _lastBands),
            OcrSkipped = Delta(c.OcrSkippedCount, ref _lastSkipped),
            CaptureFailures = Delta(c.CaptureFailureCount, ref _lastCaptureFail),
            WheelEvents = Delta(c.WheelEventCount, ref _lastWheel),
            Renders = render.Renders,
            RenderMs = render.RenderMs,
            MaxVisuals = render.MaxVisuals,
            CheckCaptureMs = DeltaMs(c.CheckCaptureMsTotal, ref _lastCaptureMs),
            CheckSignatureMs = DeltaMs(c.CheckSignatureMsTotal, ref _lastSignatureMs),
            MaxCaptureMs = c.TakeMaxCaptureMs(),
            GcCommittedMb = GC.GetGCMemoryInfo().TotalCommittedBytes / 1024.0 / 1024.0,
            MasksEnabled = c.MasksEnabled,
            DetectionEnabled = c.Settings.Detection.Enabled,
            Panic = c.Panic,
            EditMode = c.EditMode,
            Events = string.Join(" / ", events),
            OccludedMasks = c.OccludedMaskCount,
            OccluderApps = c.OccluderApps,
        };

        try
        {
            _log!.Append(sample);
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "診断ログを書き込めません: " + ex.Message;
        }
    }

    private void ResetCounters()
    {
        _lastFull = _controller.OcrFullCount;
        _lastBands = _controller.OcrBandCount;
        _lastSkipped = _controller.OcrSkippedCount;
        _lastCaptureFail = _controller.CaptureFailureCount;
        _lastWheel = _controller.WheelEventCount;
        _lastCaptureMs = _controller.CheckCaptureMsTotal;
        _lastSignatureMs = _controller.CheckSignatureMsTotal;
        _controller.TakeMaxCaptureMs();
    }

    private static double DeltaMs(double current, ref double last)
    {
        double d = current - last;
        last = current;
        return Math.Max(0, d);
    }

    private static int Delta(long current, ref long last)
    {
        long d = current - last;
        last = current;
        return (int)Math.Clamp(d, 0, int.MaxValue);
    }
}
