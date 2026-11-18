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
