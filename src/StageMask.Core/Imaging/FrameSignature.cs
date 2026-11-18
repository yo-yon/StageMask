using System;
using System.Collections.Generic;
using StageMask.Core.Geometry;

namespace StageMask.Core.Imaging;

/// <summary>
/// 画像をタイル(既定 48px 四方)に分け、タイルごとに間引いた明るさを記録した「指紋」。
/// 2枚の指紋を比べて、内容が変わったタイルを素早く見つける(OCRよりはるかに速い)。
///
/// 使い方:
/// - 「確認済み」の指紋(最後にOCRで検査した時点の内容)と、いま取り込んだ指紋を比べる
/// - 変わったタイル = まだ検査していない表示 → すぐに隠し、その行だけOCRする
/// - OCRが終わったら、その行の指紋を「確認済み」に写す
/// </summary>
public sealed class FrameSignature
{
    public int Width { get; }
    public int Height { get; }
    public int TileSize { get; }
    public int Step { get; }
    public int Columns { get; }
    public int Rows { get; }

    // タイルごとの明るさのサンプル(タイル内の位置ごと)
    private readonly byte[][] _tiles;

    private FrameSignature(int width, int height, int tileSize, int step, byte[][] tiles)
    {
        Width = width;
        Height = height;
        TileSize = tileSize;
        Step = step;
        Columns = (width + tileSize - 1) / tileSize;
        Rows = (height + tileSize - 1) / tileSize;
        _tiles = tiles;
    }

    /// <summary>
    /// BGRA32 画像から指紋を作る。step×step ピクセルの明るさの平均を1つのサンプルにする。
    /// v0.5.2 までは step=4 の点で間引いていたため、点と点の間にある細い文字の線を拾えず、
    /// 「変わったのに変わっていない」と判定されるタイルが出て、文字が見える穴になった。
    /// 平均にすると、ブロック内のどこかに線があれば値が変わるので見逃さない。
    /// </summary>
    public static FrameSignature Compute(byte[] bgra, int width, int height, int stride, int tileSize = 48, int step = 2)
        => Compute(bgra, width, height, stride, null, null, 0, tileSize, step);

    /// <summary>
    /// 前回の画像(previousPixels)と指紋(previous)があれば、画素がまったく同じタイルは前回の値を使い回す。
    /// 画素の比較(メモリの一致判定)は明るさの計算よりずっと速いため、変化が少ないときの負荷が下がる。
    /// 結果は previous なしで計算した場合と同じになる。
    /// </summary>
    public static FrameSignature Compute(byte[] bgra, int width, int height, int stride,
                                         FrameSignature? previous, byte[]? previousPixels, int previousStride,
                                         int tileSize = 48, int step = 2)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (bgra.Length < stride * height) throw new ArgumentException("画像のバイト数が足りません", nameof(bgra));
        tileSize = Math.Max(8, tileSize);
        step = Math.Clamp(step, 1, tileSize);

        int cols = (width + tileSize - 1) / tileSize;
        int rows = (height + tileSize - 1) / tileSize;
        var tiles = new byte[cols * rows][];
        bool canReuse = previous is not null && previousPixels is not null &&
                        previous.Width == width && previous.Height == height &&
                        previous.TileSize == tileSize && previous.Step == step &&
                        previousStride == stride && previousPixels.Length >= stride * height;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int x0 = c * tileSize, y0 = r * tileSize;
                int x1 = Math.Min(width, x0 + tileSize), y1 = Math.Min(height, y0 + tileSize);
                if (canReuse && SameTilePixels(bgra, previousPixels!, stride, x0, y0, x1, y1))
                {
                    tiles[r * cols + c] = previous!._tiles[r * cols + c]; // 配列は書き換えないので共有してよい
                    continue;
                }
                int nx = (x1 - x0 + step - 1) / step, ny = (y1 - y0 + step - 1) / step;
                var samples = new byte[nx * ny];
                int i = 0;
                for (int by = y0; by < y1; by += step)
                {
                    int bh = Math.Min(step, y1 - by);
                    for (int bx = x0; bx < x1; bx += step)
                    {
                        int bw = Math.Min(step, x1 - bx);
                        int sum = 0;
                        for (int y = by; y < by + bh; y++)
                        {
                            int p = y * stride + bx * 4;
                            for (int x = 0; x < bw; x++, p += 4)
                                sum += (bgra[p + 2] * 77 + bgra[p + 1] * 150 + bgra[p] * 29) >> 8; // 明るさ(BT.601 の近似)
                        }
                        samples[i++] = (byte)(sum / (bw * bh));
                    }
                }
                tiles[r * cols + c] = samples;
            }
        }
        return new FrameSignature(width, height, tileSize, step, tiles);
    }

    private static bool SameTilePixels(byte[] a, byte[] b, int stride, int x0, int y0, int x1, int y1)
    {
        int len = (x1 - x0) * 4;
        for (int y = y0; y < y1; y++)
        {
            int off = y * stride + x0 * 4;
            if (!a.AsSpan(off, len).SequenceEqual(b.AsSpan(off, len))) return false;
        }
        return true;
    }

    public bool IsSameLayout(FrameSignature? other)
        => other is not null && other.Width == Width && other.Height == Height &&
           other.TileSize == TileSize && other.Step == Step;

    private int SamplesPerRow(int column)
    {
        int x0 = column * TileSize, x1 = Math.Min(Width, x0 + TileSize);
        return (x1 - x0 + Step - 1) / Step;
    }

    /// <summary>
    /// basis(確認済みの指紋)と比べて、内容が変わったタイルを返す。
    /// basis がない・大きさが違う場合は、すべてのタイルを「変わった」とする(=全体が未確認)。
    /// </summary>
    /// <param name="pixelThreshold">明るさがこの値を超えて違うサンプルを「変化あり」と数える</param>
    /// <param name="minSamples">タイル内で変化したサンプルがこの数以上あれば、そのタイルを変化ありとする</param>
    /// <remarks>
    /// 点滅するカーソルを無視するため、変化した範囲の幅がサンプル2列分(= 2×step ピクセル)以下のタイルは
    /// 変化なしとする。v0.5.2 までは「タイルの3%以上」という割合で判定していたが、
    /// 文字の一部だけが入ったタイル(行の境目)を見逃して穴になったため、数と幅で判定する方法に変えた。
    /// </remarks>
    public List<(int Column, int Row)> ChangedTiles(FrameSignature? basis, int pixelThreshold = 24, int minSamples = 2)
    {
        var result = new List<(int, int)>();
        bool all = !IsSameLayout(basis);
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                if (all) { result.Add((c, r)); continue; }
                var a = _tiles[r * Columns + c];
                var b = basis!._tiles[r * Columns + c];
                int perRow = SamplesPerRow(c);
                int changed = 0, minX = int.MaxValue, maxX = -1;
                for (int i = 0; i < a.Length; i++)
                {
                    if (Math.Abs(a[i] - b[i]) <= pixelThreshold) continue;
                    changed++;
                    int x = i % perRow;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                }
                bool caretLike = changed > 0 && maxX - minX < 2; // 幅2サンプル以内の縦に細い線(カーソル)だけの変化
                if (changed >= minSamples && !caretLike) result.Add((c, r));
            }
        }
        return result;
    }

    /// <summary>タイルの数</summary>
    public int TileCount => Columns * Rows;

    /// <summary>
    /// タイルを周囲 radius タイルまで広げる(上下左右・斜め)。
    /// 隠すときの「穴」をなくすために使う(行の境目にまたがった文字・確認の合間に変わった隣のタイル)。
    /// </summary>
    public List<(int Column, int Row)> Dilate(IEnumerable<(int Column, int Row)> tiles, int radius = 1)
    {
        var flags = new bool[Columns * Rows];
        foreach (var (c, r) in tiles)
            for (int dr = -radius; dr <= radius; dr++)
                for (int dc = -radius; dc <= radius; dc++)
                {
                    int cc = c + dc, rr = r + dr;
                    if (cc >= 0 && cc < Columns && rr >= 0 && rr < Rows) flags[rr * Columns + cc] = true;
                }
        var result = new List<(int, int)>();
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
                if (flags[r * Columns + c]) result.Add((c, r));
        return result;
    }

    /// <summary>タイルの範囲(画像座標)。</summary>
    public RectD TileRect(int column, int row)
    {
        int x0 = column * TileSize, y0 = row * TileSize;
        return new RectD(x0, y0, Math.Min(TileSize, Width - x0), Math.Min(TileSize, Height - y0));
    }

    /// <summary>
    /// 変化したタイルの行を、上下に margin 行広げてまとめた横帯(行の範囲)にする。OCRはこの帯ごとに行う。
    /// (タイルの境目で文字が切れないよう、横幅いっぱい・上下に余裕をとる)
    /// </summary>
    public List<(int RowStart, int RowEnd)> Bands(IEnumerable<(int Column, int Row)> tiles, int margin = 1)
    {
        var flags = new bool[Rows];
        foreach (var (_, r) in tiles)
            for (int i = Math.Max(0, r - margin); i <= Math.Min(Rows - 1, r + margin); i++) flags[i] = true;

        var bands = new List<(int, int)>();
        int start = -1;
        for (int r = 0; r <= Rows; r++)
        {
            bool on = r < Rows && flags[r];
            if (on && start < 0) start = r;
            if (!on && start >= 0) { bands.Add((start, r - 1)); start = -1; }
        }
        return bands;
    }

    /// <summary>行の範囲を画像座標の横帯にする。</summary>
    public RectD BandRect(int rowStart, int rowEnd)
    {
        int y0 = rowStart * TileSize;
        int y1 = Math.Min(Height, (rowEnd + 1) * TileSize);
        return new RectD(0, y0, Width, Math.Max(0, y1 - y0));
    }

    /// <summary>
    /// 確認済みの指紋を更新する: source の指定行を写したコピーを返す。
    /// 自分(this)が source と大きさが違う場合は、source 全体を写す(=全体を検査したときだけ使うこと)。
    /// </summary>
    public static FrameSignature WithRowsFrom(FrameSignature? verified, FrameSignature source, IEnumerable<(int RowStart, int RowEnd)> rows)
    {
        if (!source.IsSameLayout(verified)) return source.Clone();
        var tiles = (byte[][])verified!._tiles.Clone();
        foreach (var (rs, re) in rows)
            for (int r = Math.Max(0, rs); r <= Math.Min(source.Rows - 1, re); r++)
                for (int c = 0; c < source.Columns; c++)
                    tiles[r * source.Columns + c] = source._tiles[r * source.Columns + c];
        return new FrameSignature(source.Width, source.Height, source.TileSize, source.Step, tiles);
    }

    public FrameSignature Clone() => new(Width, Height, TileSize, Step, (byte[][])_tiles.Clone());
}
