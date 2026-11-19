using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StageMask.App.Native;
using StageMask.Core.Geometry;
using StageMask.Core.Imaging;

namespace StageMask.App.Capture;

/// <summary>
/// 画面の一部分の画像を、そこに重なるウィンドウを奥から順に取り込んで組み立てる(手動マスクのモザイク/ぼかし用)。
///
/// 画面をそのまま取り込む方法(BitBlt)では、StageMask のマスク自身が写り込むことを実機で確認した(v0.2.0)。
/// ウィンドウ単位の取り込み(PrintWindow)にはマスクが写らないため、それを重ね合わせて「マスクがない画面」を再現する。
/// 一番下にはデスクトップ(壁紙とアイコン)を描く。取り込めない場合は灰色の下地になる。
/// 制限: タスクバーは取り込まない。半透明のウィンドウは不透明として重なる。
/// </summary>
internal static class WindowCompositeCapturer
{
    /// <param name="region">組み立てる範囲(画面座標)</param>
    /// <param name="windowsTopToBottom">表示中のウィンドウ(手前から奥の順)</param>
    /// <param name="capture">ウィンドウの画像を取得する関数(同じサイクル内ではキャッシュを返す想定)</param>
    public static async Task<CapturedFrame?> ComposeAsync(RectD region, IReadOnlyList<WindowInfo> windowsTopToBottom,
        Func<IntPtr, Task<CapturedFrame?>> capture, CancellationToken ct)
    {
        var compositor = new RegionCompositor(region);
        if (compositor.Width == 0 || compositor.Height == 0) return null;

        // 手前のウィンドウに完全に隠れているものは取り込まない(無駄を省く)。
        // 取り込めなかった・真っ黒だったウィンドウは「覆っている」側に数えない(奥のウィンドウを描くため)
        var above = new List<RectD>();
        var needed = new List<(WindowInfo Window, CapturedFrame Frame)>();
        foreach (var w in windowsTopToBottom)
        {
            if (w.IsMinimized) continue;
            // クリックが素通りする透明なウィンドウや最前面の半透明オーバーレイ(画面録画ツールなど)は、
            // PrintWindow で取り込むと真っ黒になり、下の画面をすべて覆ってしまうため使わない
            if (!w.CanOcclude) continue;
            var visiblePart = w.FrameRect.Intersect(compositor.Region);
            if (visiblePart.IsEmpty) continue;
            if (RectD.IsFullyCovered(visiblePart, above)) continue;
            ct.ThrowIfCancellationRequested();
            var frame = await capture(w.Handle).ConfigureAwait(false);
            if (frame is null || FrameHeuristics.IsLikelyBlackFrame(frame.Pixels, frame.Width, frame.Height, frame.Stride)) continue;
            needed.Add((w, frame));
            above.Add(w.FrameRect);
            if (RectD.IsFullyCovered(compositor.Region, above)) break; // これより奥は見えない
        }

        // 一番奥にデスクトップ(壁紙・アイコン)を描く。
        // ※ GetShellWindow のウィンドウを PrintWindow で取り込めるかは Windows のバージョンにより異なる(未確認)。
        //   取り込めない・真っ黒な場合は灰色の下地のまま。
        if (!RectD.IsFullyCovered(compositor.Region, above))
        {
            var shell = NativeMethods.GetShellWindow();
            if (shell != IntPtr.Zero && NativeMethods.GetWindowRect(shell, out var sr))
            {
                var desktop = await capture(shell).ConfigureAwait(false);
                if (desktop is not null && !FrameHeuristics.IsLikelyBlackFrame(desktop.Pixels, desktop.Width, desktop.Height, desktop.Stride))
                {
                    var rect = new RectD(sr.Left, sr.Top, sr.Width, sr.Height);
                    compositor.Draw(desktop.Pixels, desktop.Width, desktop.Height, desktop.Stride, rect.X, rect.Y, rect);
                }
            }
        }

        // 奥から順に描く
        for (int i = needed.Count - 1; i >= 0; i--)
        {
            var (w, frame) = needed[i];
            var origin = frame.Origin == CaptureOrigin.WindowRect ? w.WindowRect : w.FrameRect;
            compositor.Draw(frame.Pixels, frame.Width, frame.Height, frame.Stride, origin.X, origin.Y, w.FrameRect);
        }

        return new CapturedFrame(compositor.Pixels, compositor.Width, compositor.Height, compositor.Width * 4, CaptureOrigin.WindowRect);
    }
}
