using System;
using System.Collections.Generic;

namespace StageMask.Core.Imaging;

/// <summary>
/// ウィンドウが「会議の映像・動画のように動き続けているか」を判断する。
/// 以前は「ウィンドウの大部分が動き続けているか」で判断していたため、
/// 大きなウィンドウの一部に映像がある会議アプリ(Teams など)は対象にならず、
/// 映像の切り替わりのたびにウィンドウ全体が隠れて(ちらつきの原因)いた。
/// </summary>
public sealed class VideoMotionTracker
{
    /// <summary>このマス数以上が変わった確認を「動いている」とする(カーソルの点滅などの1〜2マスは数えない)</summary>
    public const int MinActiveTiles = 4;
    /// <summary>動いている状態がこの時間続いたら映像とみなす</summary>
    public static readonly TimeSpan ContinuousTime = TimeSpan.FromSeconds(3);
    /// <summary>動きがこの時間止まったら数え直す</summary>
    public static readonly TimeSpan GapTime = TimeSpan.FromSeconds(1);
    /// <summary>一度映像とみなしたら、この時間は映像のまま扱う(映像が一瞬止まるたびに全体マスクに戻らないように)</summary>
    public static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(10);
    /// <summary>スクロール(内容の上下のずれ)を見てから、この時間は映像とみなさない</summary>
    public static readonly TimeSpan ScrollBlockTime = TimeSpan.FromMilliseconds(1500);

    private DateTime _activeStart = DateTime.MinValue, _lastActive = DateTime.MinValue;
    private DateTime _lastScroll = DateTime.MinValue, _videoUntil = DateTime.MinValue;

    /// <param name="changedTiles">前回の確認から変わったマスの数</param>
    /// <param name="scrolled">内容が上下にずれた(スクロール)と判断できたか</param>
    public void Observe(DateTime now, int changedTiles, bool scrolled)
    {
        if (scrolled)
        {
            _lastScroll = now;
            _videoUntil = DateTime.MinValue; // スクロールしたら映像扱いをやめる(全体を隠す通常の動きに戻す)
        }
        if (changedTiles >= MinActiveTiles)
        {
            if (now - _lastActive > GapTime) _activeStart = now;
            _lastActive = now;
        }
        bool continuous = now - _lastActive < GapTime && now - _activeStart >= ContinuousTime;
        if (continuous && now - _lastScroll >= ScrollBlockTime) _videoUntil = now + HoldTime;
    }

    public bool IsVideoLike(DateTime now) => now < _videoUntil && now - _lastScroll >= ScrollBlockTime;
}

/// <summary>
/// 一度隠したマスを、しばらく隠したままにする(映像の部分のマスクが、確認のたびに消えたり出たりしないように)。
/// 隠す範囲が広がるだけなので、見えてしまう方向には働かない。
/// </summary>
public sealed class StickyTiles
{
    private readonly Dictionary<(int, int), DateTime> _last = new();
    private int _columns = -1, _rows = -1;

    public List<(int Column, int Row)> Apply(List<(int Column, int Row)> tiles, int columns, int rows, DateTime now, TimeSpan hold)
    {
        if (columns != _columns || rows != _rows) { _last.Clear(); _columns = columns; _rows = rows; }
        foreach (var t in tiles) _last[t] = now;
        var result = new List<(int Column, int Row)>(Math.Max(tiles.Count, _last.Count));
        var expired = new List<(int, int)>();
        foreach (var (t, at) in _last)
        {
            if (now - at <= hold) result.Add(t);
            else expired.Add(t);
        }
        foreach (var t in expired) _last.Remove(t);
        return result;
    }

    public void Clear() => _last.Clear();
}
