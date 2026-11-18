using System;
using System.Collections.Generic;
using System.Linq;
using StageMask.Core.Geometry;

namespace StageMask.Core.Detection;

/// <summary>
/// 検出結果を一定時間「保持」するバッファ。
/// OCRは数百ミリ秒ごとにしか走らず、スクロール中などは一時的に読めなくなることがある。
/// 一度見つかった領域はしばらくマスクし続けることで、ちらつきと一瞬の露出を防ぐ。
///
/// 座標は「対象ウィンドウのキャプチャ画像内の座標」で保持し、
/// 描画時にその時点のウィンドウ位置へ変換する(ウィンドウを動かしてもマスクが追従する)。
/// スレッドセーフ。
/// </summary>
public sealed class DetectionHoldBuffer<TPayload>
{
    public sealed record Item(RectD Rect, DetectionCategory Category, DateTime ExpiresUtc, TPayload? Payload);

    private readonly object _gate = new();
    private readonly Dictionary<long, List<Item>> _items = new();

    /// <summary>対象(ウィンドウ)の最新の検出結果を登録する。</summary>
    public void Update(long targetId, IEnumerable<(RectD Rect, DetectionCategory Category, TPayload? Payload)> detections,
                       DateTime nowUtc, TimeSpan hold)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(targetId, out var list))
            {
                list = new List<Item>();
                _items[targetId] = list;
            }

            list.RemoveAll(i => i.ExpiresUtc <= nowUtc);

            foreach (var (rect, cat, payload) in detections)
            {
                // ほぼ同じ位置の既存項目は期限を延長して置き換える
                list.RemoveAll(i => IsSameRegion(i.Rect, rect));
                list.Add(new Item(rect, cat, nowUtc + hold, payload));
            }
        }
    }

    /// <summary>
    /// 指定した範囲(regions)に重ならない項目の期限を延ばす。
    /// 内容が変わっていない(=確認済みのまま)部分の検出結果を、OCRし直さずに保持し続けるために使う。
    /// regions に重なる項目は延長しない(スクロールで消えた内容のマスクは、保持時間が過ぎれば消える)。
    /// </summary>
    /// <summary>
    /// 内容が変わった範囲(regions)にある既存の検出を消してから、新しい検出を登録する(1回のロックで行う)。
    /// スクロールで文字が動いたあと、古い位置に残ったマスク(ずれたマスク)を残さないために使う。
    /// 変わっていない範囲の検出は、これまでどおり保持する。
    /// </summary>
    public void ReplaceIn(long targetId, IReadOnlyCollection<RectD> regions,
                          IEnumerable<(RectD Rect, DetectionCategory Category, TPayload? Payload)> detections,
                          DateTime nowUtc, TimeSpan hold)
    {
        lock (_gate) // Update も同じロックを取る(同じスレッドなら再入できる)ので、消してから登録するまでの間に描画されない
        {
            if (_items.TryGetValue(targetId, out var list))
                list.RemoveAll(i => regions.Any(r => r.IntersectsWith(i.Rect)));
            Update(targetId, detections, nowUtc, hold);
        }
    }

    public void ExtendOutside(long targetId, IReadOnlyCollection<RectD> regions, DateTime nowUtc, TimeSpan hold)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(targetId, out var list)) return;
            list.RemoveAll(i => i.ExpiresUtc <= nowUtc);
            var until = nowUtc + hold;
            for (int k = 0; k < list.Count; k++)
            {
                var item = list[k];
                if (item.ExpiresUtc >= until) continue;
                bool touched = false;
                foreach (var r in regions)
                    if (item.Rect.IntersectsWith(r)) { touched = true; break; }
                if (!touched) list[k] = item with { ExpiresUtc = until };
            }
        }
    }

    /// <summary>有効期限内の項目を返す。</summary>
    public IReadOnlyList<Item> GetActive(long targetId, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(targetId, out var list)) return Array.Empty<Item>();
            list.RemoveAll(i => i.ExpiresUtc <= nowUtc);
            return list.ToArray();
        }
    }

    /// <summary>指定した対象以外の項目を削除する(閉じたウィンドウの掃除)。</summary>
    public void RetainOnly(IEnumerable<long> targetIds)
    {
        var keep = new HashSet<long>(targetIds);
        lock (_gate)
        {
            foreach (var key in _items.Keys.Where(k => !keep.Contains(k)).ToList())
                _items.Remove(key);
        }
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
    }

    private static bool IsSameRegion(RectD a, RectD b)
    {
        var inter = a.Intersect(b).Area;
        var min = Math.Min(a.Area, b.Area);
        return min > 0 && inter / min >= 0.8;
    }
}
