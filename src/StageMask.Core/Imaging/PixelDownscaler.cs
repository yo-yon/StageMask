using System;

namespace StageMask.Core.Imaging;

/// <summary>縮小結果(BGRA32、stride = Width * 4)。</summary>
public sealed record DownscaledImage(byte[] Pixels, int Width, int Height);

/// <summary>
/// BGRA32 画像の指定範囲を、平均値で縮小する。
/// 縮小した画像を「最近傍補間で拡大」すればモザイク、「線形補間で拡大」すればぼかしになる。
/// </summary>
public static class PixelDownscaler
{
    /// <param name="src">BGRA32 のピクセル配列</param>
    /// <param name="srcWidth">元画像の幅</param>
    /// <param name="srcHeight">元画像の高さ</param>
    /// <param name="stride">1行のバイト数</param>
    /// <param name="cropX">切り出し範囲(元画像座標)</param>
    /// <param name="targetWidth">縮小後の幅(1以上)</param>
    /// <param name="targetHeight">縮小後の高さ(1以上)</param>
    /// <returns>縮小後の画像(アルファは255)。範囲外なら null。切り出し範囲より大きい縮小サイズは切り出し範囲に合わせる。</returns>
    public static DownscaledImage? Downscale(byte[] src, int srcWidth, int srcHeight, int stride,
                                    int cropX, int cropY, int cropWidth, int cropHeight,
                                    int targetWidth, int targetHeight)
    {
        // 切り出し範囲を画像内に収める
        int x0 = Math.Clamp(cropX, 0, srcWidth);
        int y0 = Math.Clamp(cropY, 0, srcHeight);
        int x1 = Math.Clamp(cropX + cropWidth, 0, srcWidth);
        int y1 = Math.Clamp(cropY + cropHeight, 0, srcHeight);
        int cw = x1 - x0, ch = y1 - y0;
        if (cw <= 0 || ch <= 0 || targetWidth <= 0 || targetHeight <= 0) return null;
        if (src.Length < stride * srcHeight) return null;

        targetWidth = Math.Min(targetWidth, cw);
        targetHeight = Math.Min(targetHeight, ch);

        var dst = new byte[targetWidth * targetHeight * 4];
        for (int ty = 0; ty < targetHeight; ty++)
        {
            int sy0 = y0 + ty * ch / targetHeight;
            int sy1 = Math.Max(sy0 + 1, y0 + (ty + 1) * ch / targetHeight);
            for (int tx = 0; tx < targetWidth; tx++)
            {
                int sx0 = x0 + tx * cw / targetWidth;
                int sx1 = Math.Max(sx0 + 1, x0 + (tx + 1) * cw / targetWidth);

                long b = 0, g = 0, r = 0, n = 0;
                for (int sy = sy0; sy < sy1; sy++)
                {
                    int row = sy * stride;
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        int p = row + sx * 4;
                        b += src[p];
                        g += src[p + 1];
                        r += src[p + 2];
                        n++;
                    }
                }
                int d = (ty * targetWidth + tx) * 4;
                dst[d] = (byte)(b / n);
                dst[d + 1] = (byte)(g / n);
                dst[d + 2] = (byte)(r / n);
                dst[d + 3] = 255;
            }
        }
        return new DownscaledImage(dst, targetWidth, targetHeight);
    }

    /// <summary>モザイク/ぼかしの縮小後サイズを計算する(最低1px)。</summary>
    public static (int Width, int Height) TargetSize(int cropWidth, int cropHeight, int blockSize)
    {
        blockSize = Math.Max(1, blockSize);
        return (Math.Max(1, cropWidth / blockSize), Math.Max(1, cropHeight / blockSize));
    }
}
