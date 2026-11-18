using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StageMask.App.Capture;
using StageMask.Core.Geometry;
using StageMask.Core.Imaging;
using StageMask.Core.Settings;

namespace StageMask.App.Services;

/// <summary>キャプチャ画像から、モザイク/ぼかし表示用の縮小画像を作る。</summary>
internal static class ImageFactory
{
    /// <summary>
    /// frame の crop 範囲(画像座標)を縮小した BitmapSource を返す。
    /// モザイクは最近傍補間、ぼかしは線形補間で拡大表示する前提。別スレッドから使えるよう Freeze 済み。
    /// </summary>
    public static ImageSource? CreateMaskImage(CapturedFrame frame, RectD crop, MaskStyle style, int mosaicBlockSize, int blurStrength)
    {
        if (style == MaskStyle.Solid) return null;

        int x = (int)Math.Floor(crop.X), y = (int)Math.Floor(crop.Y);
        int w = (int)Math.Ceiling(crop.Width), h = (int)Math.Ceiling(crop.Height);
        int block = style == MaskStyle.Mosaic ? Math.Max(4, mosaicBlockSize) : Math.Max(4, blurStrength);
        var (tw, th) = PixelDownscaler.TargetSize(w, h, block);

        var image = PixelDownscaler.Downscale(frame.Pixels, frame.Width, frame.Height, frame.Stride, x, y, w, h, tw, th);
        if (image is null) return null;

        var bmp = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Width * 4);
        bmp.Freeze();
        return bmp;
    }
}
