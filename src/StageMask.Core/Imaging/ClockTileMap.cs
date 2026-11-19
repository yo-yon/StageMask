using System;
using System.Collections.Generic;
using StageMask.Core.Detection;
using StageMask.Core.Geometry;

namespace StageMask.Core.Imaging;

/// <summary>
/// 「時計・タイマーのような数字だけが表示されているマス」を覚えておく。
/// Teams の会議の経過時間や時計は毎秒変わるため、そのたびに「未確認」として塗りつぶすとちらつく。
/// 直前のOCRで、そのマスに時刻のような短い数字しかなかった場合に限り、小さな変化なら先に隠さない
/// (OCR はこれまでどおり行い、文字が出てきたら次から通常どおり隠す)。
/// </summary>
public sealed class ClockTileMap
{
    private readonly int _columns, _rows, _tileSize;
    private readonly bool[] _clock;
    // 「繰り返し少しずつ変わり、読み直しても隠す情報が出てこないマス」(タイマーの周りの表示など)。
    // OCR の読み取りに頼らず、変わり方で判断する(小さい時計の数字は読み違えやすく、隣の文字と同じマスになることもあるため)
    private readonly int[] _tickCount;
    private readonly DateTime[] _lastTick;

    /// <summary>この時間(秒)以内に繰り返し変わったマスを「繰り返し変わるマス」とみなす</summary>
    public const double TickWindowSeconds = 3.0;
    /// <summary>この回数以上続けて、隠す情報が出てこないまま変わったら「繰り返し変わるマス」とみなす</summary>
    public const int TickThreshold = 3;
    /// <summary>1回の読み直しで変わったマスがこれより多いときは、繰り返しの変化とはみなさない(スクロール・画面切り替えなど)</summary>
    public const int SmallChangeTiles = 12;

    public ClockTileMap(FrameSignature layout)
    {
        _columns = layout.Columns;
        _rows = layout.Rows;
        _tileSize = layout.TileSize;
        _clock = new bool[_columns * _rows];
        _tickCount = new int[_columns * _rows];
        _lastTick = new DateTime[_columns * _rows];
    }

    /// <summary>
    /// 繰り返し少しずつ変わり、そのたびに読み直しても隠す情報(メール・NGワードなど)が出てこないマスか。
    /// </summary>
    public bool IsTicker(int column, int row, DateTime now)
    {
        if (column < 0 || row < 0 || column >= _columns || row >= _rows) return false;
        int i = row * _columns + column;
        return _tickCount[i] >= TickThreshold && (now - _lastTick[i]).TotalSeconds <= TickWindowSeconds;
    }

    public bool IsSameLayout(FrameSignature sig) =>
        sig.Columns == _columns && sig.Rows == _rows && sig.TileSize == _tileSize;

    public bool IsClock(int column, int row) =>
        column >= 0 && row >= 0 && column < _columns && row < _rows && _clock[row * _columns + column];

    /// <summary>
    /// OCR し直したマス(dirtyTiles)について、そこにあった文字から「時計のマスか」を決め直す。
    /// words の座標は画像全体の座標。どのエンジンの結果も渡してよい(1つでも時計以外の文字があれば時計ではない)。
    /// </summary>
    public void Update(IEnumerable<(int Column, int Row)> dirtyTiles, IEnumerable<OcrWordBox> words)
        => Update(dirtyTiles, words, Array.Empty<RectD>(), DateTime.UtcNow);

    /// <param name="detections">今回の読み直しで見つかった、隠す情報の範囲(画像全体の座標)</param>
    public void Update(IEnumerable<(int Column, int Row)> dirtyTiles, IEnumerable<OcrWordBox> words,
                       IEnumerable<RectD> detections, DateTime now)
    {
        var dirty = new HashSet<int>();
        foreach (var (c, r) in dirtyTiles)
            if (c >= 0 && r >= 0 && c < _columns && r < _rows) dirty.Add(r * _columns + c);

        // 繰り返し変わるマスの記録。隠す情報が出てきたマスは数え直す
        var sensitive = new HashSet<int>();
        foreach (var d in detections)
            foreach (var i in TilesOf(d)) sensitive.Add(i);
        bool small = dirty.Count <= SmallChangeTiles;
        foreach (var i in dirty)
        {
            if (!small || sensitive.Contains(i)) { _tickCount[i] = 0; _lastTick[i] = now; continue; }
            _tickCount[i] = (now - _lastTick[i]).TotalSeconds <= TickWindowSeconds ? _tickCount[i] + 1 : 1;
            _lastTick[i] = now;
        }
        foreach (var i in sensitive) if (!dirty.Contains(i)) _tickCount[i] = 0;

        var hasClock = new HashSet<int>();
        var hasOther = new HashSet<int>();
        foreach (var w in words)
        {
            var kind = Classify(w.Text);
            if (kind == TextKind.Neutral) continue;
            int c0 = Math.Max(0, (int)Math.Floor(w.Box.X / _tileSize));
            int c1 = Math.Min(_columns - 1, (int)Math.Floor((w.Box.X + w.Box.Width - 1) / _tileSize));
            int r0 = Math.Max(0, (int)Math.Floor(w.Box.Y / _tileSize));
            int r1 = Math.Min(_rows - 1, (int)Math.Floor((w.Box.Y + w.Box.Height - 1) / _tileSize));
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                    (kind == TextKind.Clock ? hasClock : hasOther).Add(r * _columns + c);
        }

        foreach (var i in dirty)
            _clock[i] = hasClock.Contains(i) && !hasOther.Contains(i);
    }

    /// <summary>
    /// 先に隠す対象から、時計のマスを除く。変化が小さいとき(maxTiles 以下)だけ除く。
    /// 大きな変化(スクロールなど)のときは、時計のマスも含めてそのまま返す。
    /// </summary>
    public List<(int Column, int Row)> ExcludeClockTiles(List<(int Column, int Row)> changed, int maxTiles = 6)
        => ExcludeClockTiles(changed, DateTime.UtcNow, maxTiles);

    /// <summary>
    /// 先に隠す対象から、時計のマスと「繰り返し変わるマス」を除く。変化が小さいとき(maxTiles 以下)だけ除く。
    /// 大きな変化(スクロールなど)のときは、そのまま返す。除いたマスも OCR はこれまでどおり行う。
    /// </summary>
    public List<(int Column, int Row)> ExcludeClockTiles(List<(int Column, int Row)> changed, DateTime now, int maxTiles = SmallChangeTiles)
    {
        if (changed.Count > maxTiles) return changed;
        var result = new List<(int, int)>(changed.Count);
        foreach (var t in changed)
            if (!IsClock(t.Column, t.Row) && !IsTicker(t.Column, t.Row, now)) result.Add(t);
        return result;
    }

    private IEnumerable<int> TilesOf(RectD r)
    {
        int c0 = Math.Max(0, (int)Math.Floor(r.X / _tileSize));
        int c1 = Math.Min(_columns - 1, (int)Math.Floor((r.X + r.Width - 1) / _tileSize));
        int r0 = Math.Max(0, (int)Math.Floor(r.Y / _tileSize));
        int r1 = Math.Min(_rows - 1, (int)Math.Floor((r.Y + r.Height - 1) / _tileSize));
        for (int y = r0; y <= r1; y++)
            for (int x = c0; x <= c1; x++)
                yield return y * _columns + x;
    }

    internal enum TextKind { Neutral, Clock, Other }

    /// <summary>
    /// 時刻・経過時間・日付のような短い数字か。
    /// 数字と「: / .」だけで、区切り記号があれば数字8個以下(時刻・日付)。区切り記号がない場合は数字3個以下(件数バッジなど)。
    /// 6桁のワンタイムコードや電話番号のように、数字だけで区切り記号がない長いものは時計とみなさない。
    /// </summary>
    internal static TextKind Classify(string text)
    {
        var s = TextNormalizer.Normalize(text).Trim();
        if (s.Length == 0) return TextKind.Neutral;
        int digits = 0;
        bool separator = false;
        foreach (var ch in s)
        {
            if (ch >= '0' && ch <= '9') digits++;
            else if (ch is ':' or '/' or '.') separator = true;
            else if (ch == ' ') { }
            else return TextKind.Other;
        }
        if (digits == 0) return TextKind.Neutral; // 「:」だけなど
        if (!separator) return digits <= 3 ? TextKind.Clock : TextKind.Other;
        if (digits > 8) return TextKind.Other; // 日付(2026/11/19 = 8桁)まで
        return TextKind.Clock;
    }

    /// <summary>テスト用</summary>
    public static bool IsClockLikeText(string text) => Classify(text) == TextKind.Clock;
}
