using System;
using System.Collections.Generic;

namespace StageMask.Core.Geometry;

/// <summary>
/// 倍精度の矩形。画像座標・画面座標(物理ピクセル)のどちらにも使う。
/// </summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectD FromLTRB(double left, double top, double right, double bottom)
        => new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public RectD Union(RectD other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return FromLTRB(Math.Min(X, other.X), Math.Min(Y, other.Y),
                        Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public RectD Inflate(double amount)
        => FromLTRB(X - amount, Y - amount, Right + amount, Bottom + amount);

    public RectD Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    public RectD Scale(double sx, double sy) => new(X * sx, Y * sy, Width * sx, Height * sy);

    public bool IntersectsWith(RectD other)
        => !IsEmpty && !other.IsEmpty &&
           other.X < Right && X < other.Right && other.Y < Bottom && Y < other.Bottom;

    public RectD Intersect(RectD other)
    {
        if (!IntersectsWith(other)) return default;
        return FromLTRB(Math.Max(X, other.X), Math.Max(Y, other.Y),
                        Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
    }

    public double Area => IsEmpty ? 0 : Width * Height;

    /// <summary>この矩形から other を取り除いた残り(最大4つの矩形)。</summary>
    public List<RectD> Subtract(RectD other)
    {
        var result = new List<RectD>();
        if (IsEmpty) return result;
        if (!IntersectsWith(other)) { result.Add(this); return result; }
        var i = Intersect(other);
        // 上・下の帯(横幅いっぱい)と、左・右の帯(交差部分の高さ)に分ける
        if (i.Y > Y) result.Add(FromLTRB(X, Y, Right, i.Y));
        if (i.Bottom < Bottom) result.Add(FromLTRB(X, i.Bottom, Right, Bottom));
        if (i.X > X) result.Add(FromLTRB(X, i.Y, i.X, i.Bottom));
        if (i.Right < Right) result.Add(FromLTRB(i.Right, i.Y, Right, i.Bottom));
        return result;
    }

    /// <summary>
    /// target が covers の矩形群で完全に覆われているか(1ピクセル未満の残りは無視)。
    /// 手前のウィンドウに完全に隠れたウィンドウを、検査対象から外すために使う。
    /// </summary>
    public static bool IsFullyCovered(RectD target, IEnumerable<RectD> covers)
    {
        var remaining = new List<RectD> { target };
        foreach (var c in covers)
        {
            if (c.IsEmpty) continue;
            var next = new List<RectD>();
            foreach (var r in remaining) next.AddRange(r.Subtract(c));
            remaining = next;
            remaining.RemoveAll(r => r.Width < 1 || r.Height < 1);
            if (remaining.Count == 0) return true;
            if (remaining.Count > 4096) return false; // 計算が膨らんだら「見えている」側に倒す(検査する=安全側)
        }
        return remaining.Count == 0;
    }

    /// <summary>重なっている(または隣接距離 gap 以内の)矩形同士を結合する。</summary>
    public static List<RectD> MergeOverlapping(IEnumerable<RectD> rects, double gap = 0)
    {
        var list = new List<RectD>();
        foreach (var r in rects) if (!r.IsEmpty) list.Add(r);

        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < list.Count && !merged; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (list[i].Inflate(gap / 2).IntersectsWith(list[j].Inflate(gap / 2)))
                    {
                        list[i] = list[i].Union(list[j]);
                        list.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
            }
        }
        return list;
    }
}
