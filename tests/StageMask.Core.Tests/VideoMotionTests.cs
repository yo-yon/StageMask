using System;
using System.Collections.Generic;
using StageMask.Core.Imaging;
using Xunit;

namespace StageMask.Core.Tests;

public class VideoMotionTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SmallVideoArea_BecomesVideoLikeAfterThreeSeconds()
    {
        var v = new VideoMotionTracker();
        for (int i = 0; i <= 30; i++) v.Observe(T0.AddMilliseconds(i * 100), 6, false);
        Assert.True(v.IsVideoLike(T0.AddSeconds(3)));
    }

    [Fact]
    public void CursorBlink_IsNotVideo()
    {
        var v = new VideoMotionTracker();
        for (int i = 0; i <= 50; i++) v.Observe(T0.AddMilliseconds(i * 100), 2, false);
        Assert.False(v.IsVideoLike(T0.AddSeconds(5)));
    }

    [Fact]
    public void ShortPause_KeepsVideo_ScrollEndsIt()
    {
        var v = new VideoMotionTracker();
        for (int i = 0; i <= 30; i++) v.Observe(T0.AddMilliseconds(i * 100), 6, false);
        // 2秒止まっても映像のまま
        v.Observe(T0.AddSeconds(5), 0, false);
        Assert.True(v.IsVideoLike(T0.AddSeconds(5)));
        // スクロールしたら映像扱いをやめる
        v.Observe(T0.AddSeconds(5.1), 40, true);
        Assert.False(v.IsVideoLike(T0.AddSeconds(5.2)));
        Assert.False(v.IsVideoLike(T0.AddSeconds(8)));
    }

    [Fact]
    public void HoldExpires()
    {
        var v = new VideoMotionTracker();
        for (int i = 0; i <= 30; i++) v.Observe(T0.AddMilliseconds(i * 100), 6, false);
        Assert.False(v.IsVideoLike(T0.AddSeconds(14)));
    }

    [Fact]
    public void StickyTiles_KeepRecentTiles()
    {
        var s = new StickyTiles();
        var hold = TimeSpan.FromMilliseconds(1500);
        s.Apply(new List<(int, int)> { (1, 1) }, 10, 10, T0, hold);
        var r = s.Apply(new List<(int, int)> { (2, 2) }, 10, 10, T0.AddSeconds(1), hold);
        Assert.Equal(2, r.Count);
        r = s.Apply(new List<(int, int)>(), 10, 10, T0.AddSeconds(2), hold);
        Assert.Single(r);
        // 大きさが変わったら捨てる
        r = s.Apply(new List<(int, int)>(), 12, 10, T0.AddSeconds(2.1), hold);
        Assert.Empty(r);
    }
}
