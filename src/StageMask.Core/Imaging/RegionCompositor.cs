using System;
using StageMask.Core.Geometry;

namespace StageMask.Core.Imaging;

/// <summary>
/// 画面の一部分(region)の画像を、ウィンドウごとのキャプチャを重ねて組み立てる。
/// 画面そのものを取り込むと StageMask のマスク自身が写り込むため、
/// マスクの下にあるウィンドウを奥から順に描き重ねて「マスクがない状態の画面」を再現する。
/// 座標はすべて仮想スクリーン上の物理ピクセル。
/// </summary>
public sealed class RegionCompositor
{
    public RectD Region { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>BGRA32、stride = Width * 4</summary>
    public byte[] Pixels { get; }

    public RegionCompositor(RectD region, byte background = 0x40)
    {
        Region = new RectD(Math.Floor(region.X), Math.Floor(region.Y), Math.Ceiling(region.Width), Math.Ceiling(region.Height));
        Width = Math.Max(0, (int)Region.Width);
        Height = Math.Max(0, (int)Region.Height);
        Pixels = new byte[Width * Height * 4];
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i] = Pixels[i + 1] = Pixels[i + 2] = background; // 壁紙が取れない場合の下地(灰色)
            Pixels[i + 3] = 255;
        }
    }

    /// <summary>
    /// ウィンドウの画像を重ねる。奥のウィンドウから順に呼ぶこと。
    /// </summary>
    /// <param name="src">ウィンドウの画像(BGRA32)</param>
    /// <param name="srcOriginX">画像の左上が対応する画面座標</param>
    /// <param name="clip">描いてよい画面範囲(ウィンドウの見た目どおりの範囲)</param>
    public void Draw(byte[] src, int srcWidth, int srcHeight, int srcStride, double srcOriginX, double srcOriginY, RectD clip)
    {
        var area = Region.Intersect(clip).Intersect(new RectD(srcOriginX, srcOriginY, srcWidth, srcHeight));
        if (area.IsEmpty || src.Length < srcStride * srcHeight) return;

        int x0 = (int)Math.Ceiling(area.X), y0 = (int)Math.Ceiling(area.Y);
        int x1 = (int)Math.Floor(area.Right), y1 = (int)Math.Floor(area.Bottom);
        int ox = (int)Math.Round(srcOriginX), oy = (int)Math.Round(srcOriginY);
        int rx = (int)Region.X, ry = (int)Region.Y;

        for (int y = y0; y < y1; y++)
        {
            int sy = y - oy, dy = y - ry;
            if (sy < 0 || sy >= srcHeight || dy < 0 || dy >= Height) continue;
            int sx0 = x0 - ox, dx0 = x0 - rx;
            int count = x1 - x0;
            if (sx0 < 0) { count += sx0; dx0 -= sx0; sx0 = 0; }
            if (dx0 < 0) { count += dx0; sx0 -= dx0; dx0 = 0; }
            count = Math.Min(count, Math.Min(srcWidth - sx0, Width - dx0));
            if (count <= 0) continue;
            Buffer.BlockCopy(src, sy * srcStride + sx0 * 4, Pixels, (dy * Width + dx0) * 4, count * 4);
        }
    }
}
