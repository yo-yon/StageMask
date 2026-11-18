using System;
using System.IO;
using StageMask.Core.Detection;
using StageMask.Core.Geometry;
using StageMask.Core.Imaging;
using StageMask.Core.Settings;
using Xunit;

namespace StageMask.Core.Tests;

/// <summary>ユーティリティ類の境界値・異常系のテスト。</summary>
public class UtilityEdgeCaseTests
{
    // ---------- RectD ----------
    [Fact]
    public void RectD_UnionWithEmpty_ReturnsOther()
    {
        var r = new RectD(1, 2, 3, 4);
        Assert.Equal(r, default(RectD).Union(r));
        Assert.Equal(r, r.Union(default));
    }

    [Fact]
    public void RectD_InflateOffsetScale()
    {
        Assert.Equal(new RectD(-2, -2, 14, 14), new RectD(0, 0, 10, 10).Inflate(2));
        Assert.Equal(new RectD(5, 7, 10, 10), new RectD(0, 0, 10, 10).Offset(5, 7));
        Assert.Equal(new RectD(2, 4, 20, 40), new RectD(1, 2, 10, 20).Scale(2, 2));
    }

    [Fact]
    public void RectD_FromLTRB_ReversedIsEmpty()
    {
        var r = RectD.FromLTRB(10, 10, 5, 5);
        Assert.True(r.IsEmpty);
        Assert.Equal(0, r.Area);
    }

    [Fact]
    public void RectD_TouchingEdges_DoNotIntersect()
    {
        Assert.False(new RectD(0, 0, 10, 10).IntersectsWith(new RectD(10, 0, 10, 10)));
    }

    [Fact]
    public void RectD_MergeOverlapping_WithGap_MergesNearbyRects()
    {
        var rects = new[] { new RectD(0, 0, 10, 10), new RectD(13, 0, 10, 10) }; // 3px 離れている
        Assert.Equal(2, RectD.MergeOverlapping(rects).Count);
        Assert.Single(RectD.MergeOverlapping(rects, gap: 4));
    }

    // ---------- TextNormalizer ----------
    [Theory]
    [InlineData("Ａ", "a")]
    [InlineData("ｶﾞ", "ガ")]
    [InlineData("＠", "@")]
    [InlineData(" ", "")]
    [InlineData("　", "")] // 全角スペース
    public void Normalizer_NormalizeChar(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.NormalizeChar(input));
    }

    [Theory]
    [InlineData('ー', '-')]
    [InlineData('‐', '-')]
    [InlineData('−', '-')]
    [InlineData('a', 'a')]
    public void Normalizer_FoldDash(char input, char expected)
    {
        Assert.Equal(expected, TextNormalizer.FoldDash(input));
    }

    // ---------- HotkeyGesture ----------
    [Fact]
    public void Hotkey_ToString_IsCanonicalOrder()
    {
        Assert.True(HotkeyGesture.TryParse("alt+ctrl+M", out var g));
        Assert.Equal("Ctrl+Alt+M", g.ToString());
    }

    [Fact]
    public void Hotkey_Null_IsInvalid()
    {
        Assert.False(HotkeyGesture.TryParse(null, out _));
    }

    // ---------- PixelDownscaler ----------
    [Theory]
    [InlineData(10, 10, 0, 10, 10)]   // ブロック0は1として扱う
    [InlineData(5, 5, 14, 1, 1)]      // 小さい範囲でも最低1px
    [InlineData(140, 28, 14, 10, 2)]
    public void Downscaler_TargetSize(int w, int h, int block, int ew, int eh)
    {
        var (tw, th) = PixelDownscaler.TargetSize(w, h, block);
        Assert.Equal(ew, tw);
        Assert.Equal(eh, th);
    }

    [Fact]
    public void Downscaler_TargetLargerThanCrop_IsClamped()
    {
        var src = new byte[4 * 4 * 4];
        var img = PixelDownscaler.Downscale(src, 4, 4, 16, 0, 0, 2, 2, 10, 10);
        Assert.NotNull(img);
        Assert.Equal(2, img!.Width);
        Assert.Equal(2, img.Height);
    }

    [Fact]
    public void Downscaler_PaddedStride_ReadsCorrectRows()
    {
        // 幅2px・高さ2px、1行 12バイト(4バイトの余白つき)。下の行だけ白
        int stride = 12;
        var src = new byte[stride * 2];
        for (int x = 0; x < 2; x++) { int p = stride + x * 4; src[p] = src[p + 1] = src[p + 2] = 255; }
        var img = PixelDownscaler.Downscale(src, 2, 2, stride, 0, 1, 2, 1, 1, 1);
        Assert.Equal(255, img!.Pixels[0]);
    }

    [Fact]
    public void Downscaler_BufferTooSmall_ReturnsNull()
    {
        Assert.Null(PixelDownscaler.Downscale(new byte[8], 4, 4, 16, 0, 0, 4, 4, 2, 2));
    }

    // ---------- FrameHeuristics ----------
    private static byte[] Solid(int w, int h, int stride, byte v)
    {
        var px = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) { int p = y * stride + x * 4; px[p] = px[p + 1] = px[p + 2] = v; px[p + 3] = 255; }
        return px;
    }

    [Fact]
    public void BlackFrame_ThresholdBoundary()
    {
        Assert.True(FrameHeuristics.IsLikelyBlackFrame(Solid(100, 50, 400, 7), 100, 50, 400));
        Assert.False(FrameHeuristics.IsLikelyBlackFrame(Solid(100, 50, 400, 8), 100, 50, 400));
    }

    [Fact]
    public void BlackFrame_PaddedStride()
    {
        Assert.False(FrameHeuristics.IsLikelyBlackFrame(Solid(100, 50, 416, 200), 100, 50, 416));
    }

    [Fact]
    public void BlackFrame_ZeroSize_IsTreatedAsBlack()
    {
        Assert.True(FrameHeuristics.IsLikelyBlackFrame(Array.Empty<byte>(), 0, 0, 0));
    }

    // ---------- HoldBuffer ----------
    [Fact]
    public void HoldBuffer_HalfOverlap_IsKeptAsSeparateRegion()
    {
        var buf = new DetectionHoldBuffer<string>();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var hold = TimeSpan.FromSeconds(5);
        buf.Update(1, new[] { (new RectD(0, 0, 100, 20), DetectionCategory.Email, (string?)null) }, t0, hold);
        buf.Update(1, new[] { (new RectD(50, 0, 100, 20), DetectionCategory.Email, (string?)null) }, t0, hold);
        Assert.Equal(2, buf.GetActive(1, t0).Count);
    }

    [Fact]
    public void HoldBuffer_UnknownTargetAndClear()
    {
        var buf = new DetectionHoldBuffer<string>();
        var t0 = DateTime.UtcNow;
        Assert.Empty(buf.GetActive(99, t0));
        buf.Update(1, new[] { (new RectD(0, 0, 10, 10), DetectionCategory.Email, (string?)null) }, t0, TimeSpan.FromMinutes(1));
        buf.Clear();
        Assert.Empty(buf.GetActive(1, t0));
    }

    // ---------- ScanStatusTracker ----------
    [Fact]
    public void Scan_ExactlyMaxAge_IsStillSafe()
    {
        var t = new ScanStatusTracker();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        t.RecordSuccess(1, t0);
        Assert.True(t.IsSafe(1, t0.AddSeconds(3), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Scan_FailureAtSameTimeAsSuccess_IsNotSafe()
    {
        var t = new ScanStatusTracker();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        t.RecordSuccess(1, t0);
        t.RecordFailure(1, t0, "x");
        Assert.False(t.IsSafe(1, t0, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Scan_UnknownTarget_HasNoFailureReason()
    {
        Assert.Null(new ScanStatusTracker().GetFailureReason(123));
    }

    // ---------- WindowRuleMatcher ----------
    [Fact]
    public void WindowRule_WhitespaceTitle_IsTreatedAsNoCondition()
    {
        var rule = new WindowRule { ProcessName = " OUTLOOK.EXE ", TitleContains = "   " };
        Assert.True(WindowRuleMatcher.Matches(rule, "outlook", "何でも"));
    }

    [Fact]
    public void WindowRule_NormalizeProcessName_Null()
    {
        Assert.Equal("", WindowRuleMatcher.NormalizeProcessName(null));
    }

    // ---------- SettingsStore ----------
    [Fact]
    public void SettingsStore_MissingFile_ReturnsDefaultsWithoutWarning()
    {
        var path = Path.Combine(Path.GetTempPath(), "stagemask-none-" + Guid.NewGuid(), "settings.json");
        var store = new SettingsStore(path);
        var s = store.Load();
        Assert.NotNull(s);
        Assert.Null(store.Warning);
    }

    [Fact]
    public void SettingsStore_Save_CreatesDirectory_AndLeavesNoTempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stagemask-new-" + Guid.NewGuid(), "nested");
        var path = Path.Combine(dir, "settings.json");
        new SettingsStore(path).Save(new AppSettings());
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void SettingsStore_UnknownEnumValue_FallsBackToDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stagemask-enum-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ \"WindowRules\": [ { \"ProcessName\": \"x\", \"Action\": \"Explode\" } ] }");
        var store = new SettingsStore(path);
        var s = store.Load();
        Assert.Empty(s.WindowRules);
        Assert.NotNull(store.Warning);
    }

    // ---------- MaskContentResolver ----------
    [Fact]
    public void MaskContent_TextIsTrimmed()
    {
        var custom = new MaskContentOverride { UseCustom = true, Text = "  社外秘  " };
        Assert.Equal("社外秘", MaskContentResolver.Resolve(new AppearanceSettings(), custom, MaskStyle.Solid).Text);
    }
}
