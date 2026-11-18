using System;
using System.Collections.Generic;
using System.Linq;

namespace StageMask.Core.Detection;

/// <summary>
/// 自動検出の対象ウィンドウごとに「最後に正常に検査できた時刻」を記録し、
/// いまそのウィンドウを部分マスクだけで見せてよいか(=安全か)を判定する。
///
/// フェイルクローズの考え方:
/// - 一度も検査が成功していない(起動直後・OCR未導入)… 安全でない
/// - 直近の検査が失敗した(取り込み失敗・真っ黒な画像・OCRエラー)… 安全でない
/// - 最後の成功から時間が経ちすぎた(処理が止まった・遅延)… 安全でない
/// 安全でないウィンドウは、呼び出し側でウィンドウ全体をマスクする。
/// スレッドセーフ。
/// </summary>
public sealed class ScanStatusTracker
{
    private sealed class Entry
    {
        public DateTime? LastSuccessUtc;
        public DateTime? LastFailureUtc;
        public string? LastFailureReason;
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _entries = new();

    public void RecordSuccess(long targetId, DateTime nowUtc)
    {
        lock (_gate) Get(targetId).LastSuccessUtc = nowUtc;
    }

    public void RecordFailure(long targetId, DateTime nowUtc, string reason)
    {
        lock (_gate)
        {
            var e = Get(targetId);
            e.LastFailureUtc = nowUtc;
            e.LastFailureReason = reason;
        }
    }

    /// <summary>部分マスクだけで見せてよい状態か。</summary>
    public bool IsSafe(long targetId, DateTime nowUtc, TimeSpan maxAge)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(targetId, out var e) || e.LastSuccessUtc is not DateTime ok) return false;
            if (e.LastFailureUtc is DateTime ng && ng >= ok) return false;
            return nowUtc - ok <= maxAge;
        }
    }

    /// <summary>直近の失敗理由(成功していれば null)。</summary>
    public string? GetFailureReason(long targetId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(targetId, out var e)) return null;
            if (e.LastFailureUtc is DateTime ng && (e.LastSuccessUtc is not DateTime ok || ng >= ok))
                return e.LastFailureReason;
            return null;
        }
    }

    public void RetainOnly(IEnumerable<long> targetIds)
    {
        var keep = new HashSet<long>(targetIds);
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(k => !keep.Contains(k)).ToList())
                _entries.Remove(key);
        }
    }

    private Entry Get(long id)
    {
        if (!_entries.TryGetValue(id, out var e))
        {
            e = new Entry();
            _entries[id] = e;
        }
        return e;
    }
}
