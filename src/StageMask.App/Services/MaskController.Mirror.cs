using System;
using System.Windows.Threading;
using StageMask.App.Capture;
using StageMask.App.Native;
using StageMask.App.Overlay;
using StageMask.Core.Geometry;
using StageMask.Core.Mirror;

namespace StageMask.App.Services;

/// <summary>
/// 共有用ミラー(Teams・OBS 用)。
/// ON のときは、取り込み元のモニターの映像にマスクを重ねて MirrorWindow に映す。
/// ミラーが正常に映っている間だけ、取り込み元のモニターではマスクの代わりに枠だけを描く。
/// ミラーが止まった・閉じた・取り込めないときは、すぐに通常のマスク表示に戻す(どこにもマスクがない状態を作らない)。
/// </summary>
internal sealed partial class MaskController
{
    private MirrorWindow? _mirror;
    private MonitorCapturer? _mirrorCapturer;
    private DispatcherTimer? _mirrorTimer;
    private MonitorInfo? _mirrorSource;
    private MonitorInfo? _mirrorTarget; // 全画面で表示するモニター(null なら通常のウィンドウ)
    private OverlayState? _lastOverlayState;
    private long _mirrorFrameVersion;
    private bool _mirrorHealthy;
    private string? _mirrorBlankReason = "準備中";
    private bool _mirrorLayoutDirty;
    private DateTime _mirrorRestartUtc = DateTime.MinValue;

    /// <summary>共有用ミラーの状態(操作パネルの表示用)。OFF なら null</summary>
    public string? ShareMirrorStatus => _mirror is null ? null
        : _mirrorHealthy ? $"共有用ミラー: 表示中(取り込み元 {_mirrorSource?.DisplayName}。自分の画面では枠だけを表示)"
        : $"共有用ミラー: 一時停止中({_mirrorBlankReason})。通常のマスク表示に戻しています";

    /// <summary>設定に合わせて、共有用ミラーを開始・変更・停止する。</summary>
    public void ApplyShareMirror()
    {
        var cfg = Settings.ShareMirror;
        if (!cfg.Enabled)
        {
            StopShareMirror();
            return;
        }

        var monitors = MonitorEnumerator.GetMonitors();
        _monitorCache = monitors;
        var source = MonitorEnumerator.Find(monitors, cfg.SourceMonitor);
        var target = string.IsNullOrEmpty(cfg.MirrorMonitor) ? null : MonitorEnumerator.Find(monitors, cfg.MirrorMonitor);
        if (target is not null && source is not null && target.Handle == source.Handle) target = null; // 同じモニターには全画面にしない

        if (_mirror is null)
        {
            _mirror = new MirrorWindow();
            _mirror.Closed += OnMirrorClosed;
            _mirror.Show();
            RecordEvent("共有用ミラー開始");
        }

        // 取り込み元が変わったら取り込み直す
        if (source is null || _mirrorSource?.Handle != source.Handle || _mirrorCapturer is null)
        {
            _mirrorCapturer?.Dispose();
            _mirrorCapturer = null;
            _mirrorFrameVersion = 0;
            if (source is not null)
            {
                _mirrorCapturer = new MonitorCapturer(source.Handle, cfg.MaxFps);
                _mirrorCapturer.Start();
            }
        }
        _mirrorCapturer?.SetMaxFps(cfg.MaxFps);
        _mirrorSource = source;
        _mirrorTarget = target;
        _mirror.Topmost = target is not null; // 全画面のときは、他のウィンドウが上に重なって共有されないように最前面にする
        _mirror.SetFullscreen(target?.Bounds);
        if (_lastOverlayState is not null && source is not null) _mirror.RenderMasks(_lastOverlayState, source.Bounds);

        if (_mirrorTimer is null)
        {
            _mirrorTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
            _mirrorTimer.Tick += (_, _) => MirrorTick();
            _mirrorTimer.Start();
        }
        _mirrorLayoutDirty = false;
        _forceRender = true;
        MirrorTick();
    }

    private void OnMirrorClosed(object? sender, EventArgs e)
    {
        // ユーザーがミラーを閉じたら、共有用ミラーをやめて通常のマスク表示に戻す
        if (_mirror is null) return;
        _mirror = null;
        Settings.ShareMirror.Enabled = false;
        StopShareMirror();
        Save();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StopShareMirror()
    {
        _mirrorTimer?.Stop();
        _mirrorTimer = null;
        _mirrorCapturer?.Dispose();
        _mirrorCapturer = null;
        bool hadMirror = _mirror is not null || _mirrorHealthy;
        if (_mirror is { } m)
        {
            _mirror = null;
            m.Closed -= OnMirrorClosed;
            m.Close();
        }
        _mirrorSource = null;
        _mirrorTarget = null;
        _mirrorHealthy = false;
        _mirrorBlankReason = "準備中";
        UpdateOverlayModes();
        if (hadMirror) RecordEvent("共有用ミラー停止");
        _forceRender = true;
    }

    private void MirrorTick()
    {
        if (_mirror is null) return;
        if (_mirrorLayoutDirty) { ApplyShareMirror(); return; }

        var now = DateTime.UtcNow;
        var cap = _mirrorCapturer;
        // 取り込みが止まったら、3秒ごとに取り込み直す
        if (cap is { Failed: true } && _mirrorSource is not null && now - _mirrorRestartUtc > TimeSpan.FromSeconds(3))
        {
            _mirrorRestartUtc = now;
            cap.Dispose();
            _mirrorFrameVersion = 0;
            _mirrorCapturer = cap = new MonitorCapturer(_mirrorSource.Handle, Settings.ShareMirror.MaxFps);
            cap.Start();
            RecordEvent("共有用ミラーの取り込みをやり直し");
        }

        var mirror = _mirror;
        cap?.TryReadLatest(ref _mirrorFrameVersion, mirror.UpdateFrame);

        var status = new MirrorStatus(
            SourceFound: _mirrorSource is not null,
            CaptureFailed: cap is null || cap.Failed,
            HasFrame: _mirrorFrameVersion > 0,
            SourceBounds: _mirrorSource?.Bounds ?? default,
            MirrorBounds: mirror.ScreenBounds,
            Panic: Panic);
        var reason = MirrorSafety.BlankReason(status);
        if (!Panic) mirror.SetBlank(reason);
        bool healthy = MirrorSafety.CanUseOutlineOnly(status, mirrorOpen: true);
        if (healthy != _mirrorHealthy || reason != _mirrorBlankReason)
        {
            if (healthy != _mirrorHealthy) RecordEvent(healthy ? "共有用ミラー表示中" : "共有用ミラー一時停止");
            _mirrorHealthy = healthy;
            _mirrorBlankReason = reason;
            UpdateOverlayModes();
            _forceRender = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>オーバーレイごとに、通常のマスク・枠だけ・何も描かない を切り替える。</summary>
    private void UpdateOverlayModes()
    {
        bool showOutlines = Settings.ShareMirror.ShowLocalOutlines;
        foreach (var o in _overlays)
        {
            if (_mirrorHealthy && _mirrorSource is not null && o.MonitorBounds == _mirrorSource.Bounds)
                o.SetOutlineMode(true, showOutlines);
            // 全画面のミラーを表示しているモニターには何も描かない(ミラーの映像の上にマスクが重なって共有されないように)
            else if (_mirrorHealthy && _mirrorTarget is not null && o.MonitorBounds == _mirrorTarget.Bounds)
                o.SetOutlineMode(true, false);
            else
                o.SetOutlineMode(false, true);
        }
    }

    /// <summary>
    /// マスクするモニターの範囲。null ならすべてのモニター。
    /// 共有用ミラーが正常に映っている間は、取り込み元のモニターだけ(共有するのはその画面だけのため)。
    /// それ以外は設定の「マスクするモニター」。
    /// </summary>
    private RectD? TargetArea()
    {
        if (_mirrorHealthy && _mirrorSource is not null) return _mirrorSource.Bounds;
        if (string.IsNullOrEmpty(Settings.TargetMonitor)) return null;
        var m = MonitorEnumerator.Find(_monitorCache ??= MonitorEnumerator.GetMonitors(), Settings.TargetMonitor);
        // 設定したモニターが見つからない(外した)ときは、すべてのモニターを対象にする(安全側)
        return m?.Bounds;
    }

    private System.Collections.Generic.List<MonitorInfo>? _monitorCache;

    private void RenderMirrorMasks(OverlayState state)
    {
        _lastOverlayState = state;
        if (_mirror is null || _mirrorSource is null) return;
        _mirror.RenderMasks(state, _mirrorSource.Bounds);
    }
}
