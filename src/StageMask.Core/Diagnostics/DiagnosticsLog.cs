using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace StageMask.Core.Diagnostics;

/// <summary>
/// 診断ログの1行分。画面の内容(OCRで読んだ文字・NGワード・ウィンドウのタイトル)は持たない。
/// 数値と、決まった文言のイベント名だけを記録する。
/// </summary>
public sealed record DiagnosticsSample
{
    public DateTime Time { get; init; }
    public double CpuPercent { get; init; }
    public double WorkingSetMb { get; init; }
    public double PrivateMb { get; init; }
    public double GcHeapMb { get; init; }
    public int Threads { get; init; }
    public int Overlays { get; init; }
    public int HiddenWindows { get; init; }
    public int DetectTargets { get; init; }
    public int DetectionMasks { get; init; }
    public int PendingMasks { get; init; }
    public int ScrollMasks { get; init; }
    /// <summary>動き続けているため、変わった部分だけを隠しているウィンドウの数</summary>
    public int VideoWindows { get; init; }
    public int FailClosed { get; init; }
    public int OcrCycleMs { get; init; }
    public int CheckRoundMs { get; init; }
    /// <summary>この1行の間に行った OCR(全体)の回数</summary>
    public int OcrFull { get; init; }
    /// <summary>この1行の間に行った OCR(変化した行だけ)の回数</summary>
    public int OcrBands { get; init; }
    /// <summary>この1行の間に、変化がないため OCR を飛ばした回数</summary>
    public int OcrSkipped { get; init; }
    /// <summary>この1行の間の取り込み失敗の回数</summary>
    public int CaptureFailures { get; init; }
    /// <summary>この1行の間のホイール操作の回数</summary>
    public int WheelEvents { get; init; }
    /// <summary>この1行の間にマスクを描き直した回数</summary>
    public int Renders { get; init; }
    /// <summary>この1行の間に、マスクの描き直し(部品の組み立て)にかかった時間の合計(ミリ秒)</summary>
    public double RenderMs { get; init; }
    /// <summary>この1行の間に一度に描いたマスクの最大数</summary>
    public int MaxVisuals { get; init; }
    /// <summary>この1行の間に、変化の確認でウィンドウの取り込みにかかった時間の合計(ミリ秒)</summary>
    public double CheckCaptureMs { get; init; }
    /// <summary>この1行の間に、変化の確認で指紋の計算にかかった時間の合計(ミリ秒)</summary>
    public double CheckSignatureMs { get; init; }
    /// <summary>この1行の間で、1回の取り込みにかかった最長時間(ミリ秒)。遅いウィンドウがあるかの目安</summary>
    public double MaxCaptureMs { get; init; }
    /// <summary>.NET のGCが確保しているメモリ(MB)。private_mb との差が大きければ、.NET 以外(OCR・描画など)が使っている</summary>
    public double GcCommittedMb { get; init; }
    public bool MasksEnabled { get; init; }
    public bool DetectionEnabled { get; init; }
    public bool Panic { get; init; }
    public bool EditMode { get; init; }
    /// <summary>この1行の間に起きた出来事(決まった文言。例: "設定を保存", "パニックON")</summary>
    public string Events { get; init; } = "";
}

/// <summary>診断ログを CSV に書き出す。ファイルの大きさと数に上限を設ける。</summary>
public sealed class DiagnosticsLog
{
    public static readonly string[] Columns =
    {
        "time", "cpu_percent", "working_set_mb", "private_mb", "gc_heap_mb", "threads",
        "overlays", "hidden_windows", "detect_targets", "detection_masks", "pending_masks", "scroll_masks", "video_windows", "fail_closed",
        "ocr_cycle_ms", "check_round_ms", "ocr_full", "ocr_bands", "ocr_skipped", "capture_failures", "wheel_events",
        "renders", "render_ms", "max_visuals", "check_capture_ms", "check_signature_ms", "max_capture_ms", "gc_committed_mb",
        "masks_enabled", "detection_enabled", "panic", "edit_mode", "events",
    };

    private readonly string _directory;
    private readonly long _maxBytesPerFile;
    private readonly int _maxFiles;
    private string? _currentPath;

    public DiagnosticsLog(string directory, long maxBytesPerFile = 10 * 1024 * 1024, int maxFiles = 10)
    {
        _directory = directory;
        _maxBytesPerFile = Math.Max(1024, maxBytesPerFile);
        _maxFiles = Math.Max(1, maxFiles);
    }

    public string Directory => _directory;
    public string? CurrentPath => _currentPath;

    public void Append(DiagnosticsSample s)
    {
        System.IO.Directory.CreateDirectory(_directory);
        if (_currentPath is null || !File.Exists(_currentPath) || new FileInfo(_currentPath).Length >= _maxBytesPerFile)
            StartNewFile(s.Time);
        File.AppendAllText(_currentPath!, FormatRow(s) + "\r\n", new UTF8Encoding(false));
    }

    /// <summary>次の Append で新しいファイルに書き始める(記録を ON にし直したときなど)。</summary>
    public void Rotate() => _currentPath = null;

    private void StartNewFile(DateTime time)
    {
        var name = $"diag-{time.ToLocalTime():yyyyMMdd-HHmmss}.csv";
        var path = Path.Combine(_directory, name);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(_directory, $"diag-{time.ToLocalTime():yyyyMMdd-HHmmss}-{i}.csv");
        // Excel で文字化けしないよう、UTF-8 の BOM を付ける
        File.WriteAllText(path, string.Join(",", Columns) + "\r\n", new UTF8Encoding(true));
        _currentPath = path;
        DeleteOldFiles();
    }

    private void DeleteOldFiles()
    {
        var files = new DirectoryInfo(_directory).GetFiles("diag-*.csv")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        foreach (var f in files.Skip(_maxFiles))
        {
            try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static string FormatRow(DiagnosticsSample s)
    {
        var c = CultureInfo.InvariantCulture;
        var v = new List<string>
        {
            s.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", c),
            s.CpuPercent.ToString("0.0", c),
            s.WorkingSetMb.ToString("0.0", c),
            s.PrivateMb.ToString("0.0", c),
            s.GcHeapMb.ToString("0.0", c),
            s.Threads.ToString(c),
            s.Overlays.ToString(c),
            s.HiddenWindows.ToString(c),
            s.DetectTargets.ToString(c),
            s.DetectionMasks.ToString(c),
            s.PendingMasks.ToString(c),
            s.ScrollMasks.ToString(c),
            s.VideoWindows.ToString(c),
            s.FailClosed.ToString(c),
            s.OcrCycleMs.ToString(c),
            s.CheckRoundMs.ToString(c),
            s.OcrFull.ToString(c),
            s.OcrBands.ToString(c),
            s.OcrSkipped.ToString(c),
            s.CaptureFailures.ToString(c),
            s.WheelEvents.ToString(c),
            s.Renders.ToString(c),
            s.RenderMs.ToString("0.0", c),
            s.MaxVisuals.ToString(c),
            s.CheckCaptureMs.ToString("0.0", c),
            s.CheckSignatureMs.ToString("0.0", c),
            s.MaxCaptureMs.ToString("0.0", c),
            s.GcCommittedMb.ToString("0.0", c),
            Bool(s.MasksEnabled),
            Bool(s.DetectionEnabled),
            Bool(s.Panic),
            Bool(s.EditMode),
            Escape(s.Events),
        };
        return string.Join(",", v);
    }

    private static string Bool(bool b) => b ? "1" : "0";

    /// <summary>CSV のフィールドとして安全な形にする(カンマ・引用符・改行を含む場合は引用符で囲む)。</summary>
    public static string Escape(string? value)
    {
        value ??= "";
        // 表計算ソフトで式として解釈されないようにする
        if (value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value;
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
