using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using StageMask.App.Native;

namespace StageMask.App.Capture;

/// <summary>
/// PrintWindow(PW_RENDERFULLCONTENT) でウィンドウの内容を取得する。
/// - 他のウィンドウ(このアプリのマスクを含む)に隠れていても、ウィンドウ自身の内容を取得できる。
/// - Windows.Graphics.Capture と違い、キャプチャ中の黄色い枠が表示されない。
/// 注意: PW_RENDERFULLCONTENT は公式ドキュメントに記載のないフラグ。アプリによっては黒い画像になる。
/// </summary>
internal sealed class PrintWindowCapturer : IWindowCapturer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    // 応答しないアプリへの PrintWindow は、タイムアウト後もスレッドを占有したまま残る。
    // 同じウィンドウへの呼び出しが終わっていない間は新しく呼ばない(スレッドが溜まり続けるのを防ぐ)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, Task<CapturedFrame?>> _inFlight = new();

    public async Task<CapturedFrame?> CaptureAsync(IntPtr hwnd, CancellationToken ct)
    {
        if (_inFlight.TryGetValue(hwnd, out var running) && !running.IsCompleted)
            return null; // 前回の取り込みがまだ終わっていない = 応答していない → 取り込み失敗として扱う(全体を隠す)

        // PrintWindow は対象アプリが応答しないとブロックするため、別スレッド+タイムアウトで呼ぶ
        var task = Task.Run(() => Capture(hwnd), CancellationToken.None);
        _inFlight[hwnd] = task;
        _ = task.ContinueWith(t => _inFlight.TryRemove(new KeyValuePair<IntPtr, Task<CapturedFrame?>>(hwnd, t)),
                              TaskScheduler.Default);
        try
        {
            return await task.WaitAsync(Timeout, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public void RetainOnly(IReadOnlyCollection<IntPtr> hwnds) { /* 保持する資源なし */ }

    public void Dispose() { }

    private static CapturedFrame? Capture(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return null;
        int w = r.Width, h = r.Height;
        if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return null;

        IntPtr memDc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
            if (memDc == IntPtr.Zero) return null;

            var bmi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // 負の値でトップダウン
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                },
            };
            bmp = NativeMethods.CreateDIBSection(memDc, ref bmi, 0 /* DIB_RGB_COLORS */, out var bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero || bits == IntPtr.Zero) return null;
            old = NativeMethods.SelectObject(memDc, bmp);

            if (!NativeMethods.PrintWindow(hwnd, memDc, NativeMethods.PW_RENDERFULLCONTENT)) return null;

            var pixels = new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            // GDI はアルファを 0 のままにすることが多い。
            // そのままだと OCR や表示で「透明=黒」と扱われるため、不透明にそろえる。
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

            return new CapturedFrame(pixels, w, h, w * 4, CaptureOrigin.WindowRect);
        }
        finally
        {
            if (old != IntPtr.Zero) NativeMethods.SelectObject(memDc, old);
            if (bmp != IntPtr.Zero) NativeMethods.DeleteObject(bmp);
            if (memDc != IntPtr.Zero) NativeMethods.DeleteDC(memDc);
        }
    }
}
