using System;

namespace StageMask.Core.Imaging;

public static class FrameHeuristics
{
    /// <summary>
    /// 画像がほぼ真っ黒か(キャプチャ失敗の典型)を判定する。
    /// 格子状に間引いて調べ、指定割合以上の画素が暗ければ true。
    /// 真っ黒な画像は「検査できなかった」とみなし、フェイルクローズの対象にする。
    /// </summary>
    public static bool IsLikelyBlackFrame(byte[] bgra, int width, int height, int stride,
                                          int darkThreshold = 8, double ratio = 0.995)
    {
        if (width <= 0 || height <= 0 || bgra.Length < stride * height) return true;

        int stepX = Math.Max(1, width / 64);
        int stepY = Math.Max(1, height / 64);
        long total = 0, dark = 0;
        for (int y = 0; y < height; y += stepY)
        {
            int row = y * stride;
            for (int x = 0; x < width; x += stepX)
            {
                int p = row + x * 4;
                total++;
                if (bgra[p] < darkThreshold && bgra[p + 1] < darkThreshold && bgra[p + 2] < darkThreshold) dark++;
            }
        }
        return total == 0 || dark >= total * ratio;
    }
}
