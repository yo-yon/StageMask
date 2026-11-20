using System;
using StageMask.Core.Geometry;

namespace StageMask.Core.Mirror;

/// <summary>共有用ミラーの状態(判断に使う情報だけ)。</summary>
public readonly record struct MirrorStatus(
    bool SourceFound,
    bool CaptureFailed,
    bool HasFrame,
    RectD SourceBounds,
    RectD MirrorBounds,
    bool Panic);

/// <summary>
/// 共有用ミラーの安全のための判断。
/// ミラーが正しく映せないときは、相手側に何も見せない(真っ黒にする)、
/// そして自分の画面のマスクを通常の表示に戻す(ミラーを使わず画面を直接共有されても隠れるように)。
/// </summary>
public static class MirrorSafety
{
    /// <summary>ミラーを真っ黒にする理由。映してよければ null。</summary>
    public static string? BlankReason(MirrorStatus s)
    {
        if (s.Panic) return null; // パニックはパニック用の表示にする(呼び出し側で扱う)
        if (!s.SourceFound) return "取り込み元のモニターが見つかりません";
        if (s.CaptureFailed) return "画面を取り込めません";
        if (!s.HasFrame) return "準備中";
        // ミラーが取り込み元のモニターに重なると、合わせ鏡になり、マスクの位置も合わなくなる
        if (s.MirrorBounds.IntersectsWith(s.SourceBounds)) return "共有用ミラーが取り込み元のモニターに重なっています。別のモニターに移してください";
        return null;
    }

    /// <summary>
    /// 自分の画面のマスクを「枠だけ」にしてよいか。ミラーが正常に映せているときだけ true。
    /// (ミラーが止まっている・閉じているのに枠だけにすると、どこにもマスクがない状態になるため)
    /// </summary>
    public static bool CanUseOutlineOnly(MirrorStatus s, bool mirrorOpen)
        => mirrorOpen && !s.Panic && BlankReason(s) is null;

    /// <summary>画面上の矩形(物理ピクセル)を、取り込み元のモニターの左上を原点とした座標にする。はみ出しは切り取る。</summary>
    public static RectD ToSourceLocal(RectD screen, RectD source)
    {
        var clipped = screen.Intersect(source);
        if (clipped.IsEmpty) return default;
        return new RectD(clipped.X - source.X, clipped.Y - source.Y, clipped.Width, clipped.Height);
    }
}
