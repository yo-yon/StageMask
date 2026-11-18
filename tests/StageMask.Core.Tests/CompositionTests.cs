using System.Linq;
using StageMask.Core.Geometry;
using StageMask.Core.Imaging;
using Xunit;

namespace StageMask.Core.Tests;

/// <summary>ウィンドウの合成(手動マスクのモザイク用)と、隠れたウィンドウの判定のテスト。</summary>
public class CompositionTests
{
    private static byte[] Solid(int w, int h, byte v)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
        return px;
    }

    private static byte At(RegionCompositor c, int x, int y) => c.Pixels[(y * c.Width + x) * 4];

    // ---------- RectD.Subtract / IsFullyCovered ----------
    [Fact]
    public void Subtract_Center_LeavesFourStrips()
    {
        var parts = new RectD(0, 0, 10, 10).Subtract(new RectD(3, 3, 4, 4));
        Assert.Equal(4, parts.Count);
        Assert.Equal(100 - 16, parts.Sum(p => p.Area));
    }

    [Fact]
    public void Subtract_NoOverlap_ReturnsSelf()
    {
        var r = new RectD(0, 0, 10, 10);
        Assert.Equal(r, Assert.Single(r.Subtract(new RectD(20, 20, 5, 5))));
    }

    [Fact]
    public void Subtract_FullCover_ReturnsEmpty()
    {
        Assert.Empty(new RectD(2, 2, 5, 5).Subtract(new RectD(0, 0, 10, 10)));
    }

    [Fact]
    public void FullyCovered_ByTwoHalves()
    {
        var target = new RectD(0, 0, 100, 100);
        Assert.True(RectD.IsFullyCovered(target, new[] { new RectD(0, 0, 60, 100), new RectD(50, 0, 60, 100) }));
    }

    [Fact]
    public void NotCovered_WhenSliverRemains()
    {
        // 右端 2px が見えている → 見えている(検査対象)と判定する
        var target = new RectD(0, 0, 100, 100);
        Assert.False(RectD.IsFullyCovered(target, new[] { new RectD(0, 0, 98, 100) }));
    }

    [Fact]
    public void NotCovered_WithNoCovers()
    {
        Assert.False(RectD.IsFullyCovered(new RectD(0, 0, 10, 10), System.Array.Empty<RectD>()));
    }

    // ---------- RegionCompositor ----------
    [Fact]
    public void Compositor_StartsWithBackground()
    {
        var c = new RegionCompositor(new RectD(0, 0, 4, 4), background: 0x40);
        Assert.Equal(0x40, At(c, 0, 0));
        Assert.Equal(255, c.Pixels[3]);
    }

    [Fact]
    public void Compositor_LaterWindowIsDrawnOnTop()
    {
        var c = new RegionCompositor(new RectD(0, 0, 10, 10));
        c.Draw(Solid(10, 10, 100), 10, 10, 40, 0, 0, new RectD(0, 0, 10, 10));  // 奥のウィンドウ
        c.Draw(Solid(5, 5, 200), 5, 5, 20, 5, 5, new RectD(5, 5, 5, 5));      // 手前のウィンドウ
        Assert.Equal(100, At(c, 1, 1));
        Assert.Equal(200, At(c, 6, 6));
    }

    [Fact]
    public void Compositor_RespectsClip()
    {
        // 画像は 10x10 だが、見た目の範囲(clip)は内側 6x6 だけ(不可視の枠を描かない)
        var c = new RegionCompositor(new RectD(0, 0, 10, 10), background: 0);
        c.Draw(Solid(10, 10, 200), 10, 10, 40, 0, 0, new RectD(2, 2, 6, 6));
        Assert.Equal(0, At(c, 1, 1));
        Assert.Equal(200, At(c, 2, 2));
        Assert.Equal(0, At(c, 8, 8));
    }

    [Fact]
    public void Compositor_RegionOffsetAndNegativeCoordinates()
    {
        // 左側のモニター(負の座標)にある領域
        var c = new RegionCompositor(new RectD(-100, 50, 20, 20), background: 0);
        c.Draw(Solid(50, 50, 150), 50, 50, 200, -110, 40, new RectD(-110, 40, 50, 50));
        Assert.Equal(150, At(c, 0, 0));
        Assert.Equal(150, At(c, 19, 19));
    }

    [Fact]
    public void Compositor_PartiallyOutsideRegion_IsClipped()
    {
        var c = new RegionCompositor(new RectD(10, 10, 10, 10), background: 0);
        c.Draw(Solid(15, 15, 90), 15, 15, 60, 0, 0, new RectD(0, 0, 15, 15));
        Assert.Equal(90, At(c, 0, 0));   // 画面(10,10)
        Assert.Equal(90, At(c, 4, 4));   // 画面(14,14)
        Assert.Equal(0, At(c, 5, 5));    // 画面(15,15) はウィンドウ外
    }

    [Fact]
    public void Compositor_BadSource_IsIgnored()
    {
        var c = new RegionCompositor(new RectD(0, 0, 4, 4), background: 7);
        c.Draw(new byte[3], 10, 10, 40, 0, 0, new RectD(0, 0, 10, 10));
        Assert.Equal(7, At(c, 0, 0));
    }
}
