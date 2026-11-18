using System;
using System.Diagnostics;

namespace StageMask.App.Services;

/// <summary>
/// このアプリ(StageMask)自身の CPU・メモリ使用量を測る。
/// - CPU: 前回からの CPU 時間の増加 ÷ (経過時間 × 論理プロセッサ数)。全コアを使い切った状態を 100% とする
/// - メモリ: 作業セット(WorkingSet64 = 物理メモリ上のページ。共有分を含む)と
///           プライベート(PrivateMemorySize64 = 他のプロセスと共有しない確保量。Private Bytes 相当)
/// </summary>
internal sealed class ResourceMonitor : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private long _lastTimestamp;
    private double _smoothedCpu = -1;

    public ResourceMonitor()
    {
        _lastCpu = _process.TotalProcessorTime;
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>CPU 使用率(%)。直近数秒をならした値</summary>
    public double CpuPercent { get; private set; }
    /// <summary>作業セット(MB)</summary>
    public double WorkingSetMb { get; private set; }
    /// <summary>プライベートメモリ(MB)</summary>
    public double PrivateMb { get; private set; }
    /// <summary>スレッド数</summary>
    public int Threads { get; private set; }

    /// <summary>1秒程度ごとに呼ぶ。</summary>
    public void Sample()
    {
        try
        {
            long now = Stopwatch.GetTimestamp();
            double elapsedMs = (now - _lastTimestamp) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs < 500) return; // 間隔が短すぎると値がばらつくため、前回の値を使う

            _process.Refresh(); // 呼ばないとメモリ値が古いまま(公式ドキュメントの注記)
            var cpu = _process.TotalProcessorTime;
            if (elapsedMs > 0)
            {
                double pct = (cpu - _lastCpu).TotalMilliseconds / (elapsedMs * Environment.ProcessorCount) * 100.0;
                pct = Math.Clamp(pct, 0, 100);
                // 表示がばたつかないよう軽くならす
                _smoothedCpu = _smoothedCpu < 0 ? pct : _smoothedCpu * 0.5 + pct * 0.5;
                CpuPercent = _smoothedCpu;
            }
            _lastCpu = cpu;
            _lastTimestamp = now;

            WorkingSetMb = _process.WorkingSet64 / 1024.0 / 1024.0;
            PrivateMb = _process.PrivateMemorySize64 / 1024.0 / 1024.0;
            Threads = _process.Threads.Count;
        }
        catch (Exception ex)
        {
            Logger.Write("ResourceMonitor", ex);
        }
    }

    public void Dispose() => _process.Dispose();
}
