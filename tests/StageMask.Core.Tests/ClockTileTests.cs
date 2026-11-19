using System.Collections.Generic;
using StageMask.Core.Detection;
using StageMask.Core.Geometry;
using StageMask.Core.Imaging;
using Xunit;

namespace StageMask.Core.Tests;

/// <summary>v0.5.11: 時計・会議タイマーのマスを先に隠さない</summary>
public class ClockTileTests
{
    private static FrameSignature Layout() => FrameSignature.Compute(new byte[192 * 144 * 4], 192, 144, 192 * 4); // 4x3 タイル

    [Theory]
    [InlineData("12:34")]
    [InlineData("01:02:03")]
    [InlineData("2026/11/19")]
    [InlineData("3")]          // 件数バッジ
    [InlineData("１２：３４")]  // 全角
    public void ClockLikeText(string text) => Assert.True(ClockTileMap.IsClockLikeText(text));

    [Theory]
    [InlineData("123456")]          // ワンタイムコード
    [InlineData("090 1234 5678")]   // 電話番号
    [InlineData("12:34 会議")]
    [InlineData("yoh@contoso.com")]
    [InlineData("1234")]            // 区切りのない4桁(暗証番号など)
    public void NotClockLikeText(string text) => Assert.False(ClockTileMap.IsClockLikeText(text));

    [Fact]
    public void ClockTile_IsExcludedFromSmallChanges()
    {
        var map = new ClockTileMap(Layout());
        map.Update(new[] { (1, 1) }, new[] { new OcrWordBox("05:12", new RectD(52, 60, 30, 12)) });
        Assert.True(map.IsClock(1, 1));
        var result = map.ExcludeClockTiles(new List<(int, int)> { (1, 1), (3, 2) });
        Assert.Single(result);
        Assert.Equal((3, 2), result[0]);
    }

    [Fact]
    public void TileWithOtherText_IsNotClock()
    {
        var map = new ClockTileMap(Layout());
        map.Update(new[] { (1, 1) }, new[]
        {
            new OcrWordBox("05:12", new RectD(52, 60, 20, 12)),
            new OcrWordBox("secret", new RectD(74, 60, 20, 12)),
        });
        Assert.False(map.IsClock(1, 1));
    }

    [Fact]
    public void ClockTile_BecomesNormal_WhenTextAppears()
    {
        var map = new ClockTileMap(Layout());
        map.Update(new[] { (1, 1) }, new[] { new OcrWordBox("05:12", new RectD(52, 60, 30, 12)) });
        map.Update(new[] { (1, 1) }, new[] { new OcrWordBox("password", new RectD(52, 60, 30, 12)) });
        Assert.False(map.IsClock(1, 1));
    }

    [Fact]
    public void LargeChange_KeepsClockTiles()
    {
        var map = new ClockTileMap(Layout());
        map.Update(new[] { (1, 1) }, new[] { new OcrWordBox("05:12", new RectD(52, 60, 30, 12)) });
        var many = new List<(int, int)>();
        for (int r = 0; r < 3; r++) for (int c = 0; c < 4; c++) many.Add((c, r));
        Assert.Equal(12, map.ExcludeClockTiles(many).Count); // スクロールなどの大きな変化では除かない
    }

    [Fact]
    public void Setting_IsOnByDefault() => Assert.True(new StageMask.Core.Settings.DetectionSettings().IgnoreClockChanges);
}

/// <summary>v0.5.12: スクロールと動画を見分ける</summary>
public class ShiftEstimateTests
{
    private const int W = 192, H = 192;

    private static byte[] Text(int offsetY)
    {
        var px = new byte[W * H * 4];
        for (int i = 0; i < px.Length; i++) px[i] = 255;
        for (int line = 0; line < 12; line++)
        {
            int y0 = 8 + line * 16 - offsetY;
            for (int y = y0; y < y0 + 8; y++)
            {
                if (y < 0 || y >= H) continue;
                int len = 40 + (line * 37) % 140; // 行ごとに長さを変える
                for (int x = 4; x < 4 + len && x < W; x++)
                {
                    int p = (y * W + x) * 4;
                    px[p] = px[p + 1] = px[p + 2] = 0;
                }
            }
        }
        return px;
    }

    private static FrameSignature Sig(byte[] px) => FrameSignature.Compute(px, W, H, W * 4);

    [Fact]
    public void Scroll_IsDetectedAsShift()
    {
        var shift = Sig(Text(20)).EstimateVerticalShift(Sig(Text(0)));
        Assert.NotNull(shift);
        Assert.InRange(shift!.Value, 16, 24);
    }

    [Fact]
    public void SameContent_IsNotShift() => Assert.Null(Sig(Text(0)).EstimateVerticalShift(Sig(Text(0))));

    [Fact]
    public void InPlaceChange_IsNotShift()
    {
        // 動画のように、その場で明るさが変わる(ずれではない)
        var a = Text(0);
        var b = (byte[])a.Clone();
        var rnd = new System.Random(1);
        for (int i = 0; i < b.Length; i += 4)
        {
            byte v = (byte)rnd.Next(256);
            b[i] = b[i + 1] = b[i + 2] = v;
        }
        Assert.Null(Sig(b).EstimateVerticalShift(Sig(a)));
    }
    // v0.5.28: OCR で時計と読めなくても、繰り返し変わり隠す情報が出てこないマスは先に隠さない
    [Fact]
    public void RepeatedlyChangingTileWithoutDetections_BecomesTicker()
    {
        var map = new ClockTileMap(FrameSignature.Compute(new byte[192 * 144 * 4], 192, 144, 192 * 4));
        var t0 = new System.DateTime(2026, 11, 19, 12, 0, 0, System.DateTimeKind.Utc);
        var noise = new[] { new OcrWordBox("録画中 12:3A", new RectD(52, 60, 60, 12)) }; // 読み違えて時計と判定できない
        for (int k = 0; k < 3; k++)
            map.Update(new[] { (1, 1) }, noise, System.Array.Empty<RectD>(), t0.AddSeconds(k));
        Assert.False(map.IsClock(1, 1));
        Assert.True(map.IsTicker(1, 1, t0.AddSeconds(2.5)));
        Assert.Empty(map.ExcludeClockTiles(new List<(int, int)> { (1, 1) }, t0.AddSeconds(2.5)));
        // しばらく変わらなければ元に戻る
        Assert.False(map.IsTicker(1, 1, t0.AddSeconds(10)));
    }

    [Fact]
    public void TileWithDetection_IsNotTicker()
    {
        var map = new ClockTileMap(FrameSignature.Compute(new byte[192 * 144 * 4], 192, 144, 192 * 4));
        var t0 = new System.DateTime(2026, 11, 19, 12, 0, 0, System.DateTimeKind.Utc);
        for (int k = 0; k < 3; k++)
            map.Update(new[] { (1, 1) }, System.Array.Empty<OcrWordBox>(), System.Array.Empty<RectD>(), t0.AddSeconds(k));
        // 隠す情報が出てきたら数え直す
        map.Update(new[] { (1, 1) }, System.Array.Empty<OcrWordBox>(), new[] { new RectD(50, 50, 20, 10) }, t0.AddSeconds(3));
        Assert.False(map.IsTicker(1, 1, t0.AddSeconds(3)));
    }

    [Fact]
    public void LargeChanges_DoNotCountAsTicks()
    {
        var map = new ClockTileMap(FrameSignature.Compute(new byte[240 * 144 * 4], 240, 144, 240 * 4));
        var t0 = new System.DateTime(2026, 11, 19, 12, 0, 0, System.DateTimeKind.Utc);
        var all = new List<(int, int)>();
        for (int r = 0; r < 3; r++) for (int c = 0; c < 5; c++) all.Add((c, r)); // 15件(スクロールなどの大きな変化)
        for (int k = 0; k < 4; k++)
            map.Update(all, System.Array.Empty<OcrWordBox>(), System.Array.Empty<RectD>(), t0.AddSeconds(k));
        Assert.False(map.IsTicker(1, 1, t0.AddSeconds(3)));
    }

    [Fact]
    public void TimerTile_IsTickerEvenWhileVideoAreaChanges()
    {
        // 12x4 タイル。左端の1マス(タイマー)と、右側の大きなかたまり(映像)が同時に変わり続ける
        var map = new ClockTileMap(FrameSignature.Compute(new byte[576 * 192 * 4], 576, 192, 576 * 4));
        var t0 = new System.DateTime(2026, 11, 19, 12, 0, 0, System.DateTimeKind.Utc);
        var changed = new List<(int, int)> { (0, 0) };
        for (int r = 0; r < 4; r++) for (int c = 4; c < 12; c++) changed.Add((c, r)); // 32マスの映像
        for (int k = 0; k < 3; k++)
            map.Update(changed, System.Array.Empty<OcrWordBox>(), System.Array.Empty<RectD>(), t0.AddSeconds(k));
        Assert.True(map.IsTicker(0, 0, t0.AddSeconds(2.5))); // タイマーは小さなかたまりなので対象
        Assert.False(map.IsTicker(5, 1, t0.AddSeconds(2.5))); // 映像の部分は大きなかたまりなので対象外
        var result = map.ExcludeClockTiles(changed, t0.AddSeconds(2.5));
        Assert.False(result.Contains((0, 0)));
        Assert.Equal(32, result.Count);
    }
}
