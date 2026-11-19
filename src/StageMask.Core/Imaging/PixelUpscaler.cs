using System;

namespace StageMask.Core.Imaging;

/// <summary>
/// OCR の前に画像を拡大する。Windows の OCR は小さい文字(メモ帳の既定サイズなど)を
/// 読み落としたり、1つの単語を途中で分けたりしやすいため、小さい画像は2倍にしてから読む。
/// </summary>
public static class PixelUpscaler
{
    /// <summary>
    /// BGRA32(詰めた配列: stride = 幅×4)の画像を、縦横2倍に双線形補間で拡大する。
    /// 戻り値の stride は (幅×2)×4。
    /// </summary>
    public static byte[] Upscale2x(byte[] src, int width, int height)
    {
        if (width <= 0 || height <= 0) return Array.Empty<byte>();
        var dst = new byte[width * 2 * height * 2 * 4];
        Upscale2x(src, width, height, dst);
        return dst;
    }

    /// <summary>
    /// 出力先を渡す版(呼び出し側で配列を使い回し、大きな配列を毎回作らないようにするため)。
    /// dst は (幅×2)×(高さ×2)×4 バイト以上。
    /// </summary>
    public static void Upscale2x(byte[] src, int width, int height, byte[] dst)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        if (width <= 0 || height <= 0) return;
        if (src.Length < width * height * 4) throw new ArgumentException("画像のサイズが合いません", nameof(src));

        int dw = width * 2, dh = height * 2;
        if (dst.Length < dw * dh * 4) throw new ArgumentException("出力先が小さすぎます", nameof(dst));
        for (int y = 0; y < dh; y++)
        {
            // 出力画素の中心を元画像の座標に戻す: (y + 0.5) / 2 - 0.5
            double sy = (y + 0.5) / 2.0 - 0.5;
            int y0 = (int)Math.Floor(sy);
            double fy = sy - y0;
            int ya = Math.Clamp(y0, 0, height - 1), yb = Math.Clamp(y0 + 1, 0, height - 1);
            for (int x = 0; x < dw; x++)
            {
                double sx = (x + 0.5) / 2.0 - 0.5;
                int x0 = (int)Math.Floor(sx);
                double fx = sx - x0;
                int xa = Math.Clamp(x0, 0, width - 1), xb = Math.Clamp(x0 + 1, 0, width - 1);

                int p00 = (ya * width + xa) * 4, p01 = (ya * width + xb) * 4;
                int p10 = (yb * width + xa) * 4, p11 = (yb * width + xb) * 4;
                int o = (y * dw + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    double top = src[p00 + c] * (1 - fx) + src[p01 + c] * fx;
                    double bottom = src[p10 + c] * (1 - fx) + src[p11 + c] * fx;
                    dst[o + c] = (byte)Math.Clamp((int)Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
                }
            }
        }
    }
}
