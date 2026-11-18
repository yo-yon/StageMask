using System;
using System.Threading;
using System.Threading.Tasks;

namespace StageMask.App.Capture;

/// <summary>キャプチャ画像の左上が、ウィンドウのどの矩形に対応するか。</summary>
internal enum CaptureOrigin
{
    /// <summary>GetWindowRect の左上(不可視のリサイズ枠を含む)。PrintWindow はこちら。</summary>
    WindowRect,
    /// <summary>DWMWA_EXTENDED_FRAME_BOUNDS の左上。Windows.Graphics.Capture はこちらを想定。</summary>
    FrameRect,
}

/// <summary>BGRA32 のキャプチャ画像。</summary>
internal sealed record CapturedFrame(byte[] Pixels, int Width, int Height, int Stride, CaptureOrigin Origin)
{
    /// <summary>指定範囲を切り出し、詰めた(stride = 幅×4)配列を返す。</summary>
    public byte[] Crop(int x, int y, int width, int height)
    {
        var dst = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(Pixels, (y + row) * Stride + x * 4, dst, row * width * 4, width * 4);
        return dst;
    }
}

internal interface IWindowCapturer : IDisposable
{
    /// <summary>
    /// 複数のウィンドウを同時に取り込んでよいか。
    /// PrintWindow は対象アプリごとに独立しているので同時に呼べる(時間が「一番遅い1つ分」で済む)。
    /// </summary>
    bool SupportsConcurrentCapture { get; }

    /// <summary>ウィンドウの現在の画像を取得する。取得できなければ null。</summary>
    Task<CapturedFrame?> CaptureAsync(IntPtr hwnd, CancellationToken ct);

    /// <summary>指定したウィンドウ以外のキャプチャ資源を解放する。</summary>
    void RetainOnly(System.Collections.Generic.IReadOnlyCollection<IntPtr> hwnds);
}
