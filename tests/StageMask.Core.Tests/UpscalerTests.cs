using StageMask.Core.Imaging;
using StageMask.Core.Settings;
using Xunit;

namespace StageMask.Core.Tests;

public class UpscalerTests
{
    [Fact]
    public void Upscale2x_DoublesSizeAndKeepsFlatColor()
    {
        var src = new byte[3 * 2 * 4];
        for (int i = 0; i < src.Length; i += 4) { src[i] = 10; src[i + 1] = 20; src[i + 2] = 30; src[i + 3] = 255; }
        var dst = PixelUpscaler.Upscale2x(src, 3, 2);
        Assert.Equal(6 * 4 * 4, dst.Length);
        for (int i = 0; i < dst.Length; i += 4)
        {
            Assert.Equal(10, dst[i]); Assert.Equal(20, dst[i + 1]); Assert.Equal(30, dst[i + 2]); Assert.Equal(255, dst[i + 3]);
        }
    }

    [Fact]
    public void Upscale2x_KeepsEdgePixelsAndInterpolatesBetween()
    {
        // 横2画素: 黒 → 白
        var src = new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 };
        var dst = PixelUpscaler.Upscale2x(src, 2, 1);
        Assert.Equal(4 * 2 * 4, dst.Length);
        Assert.Equal(0, dst[0]);              // 左端は黒のまま
        Assert.Equal(255, dst[3 * 4]);        // 右端は白のまま
        Assert.InRange(dst[1 * 4], 1, 254);   // 間は中間色
    }
}

public class HotkeyValidatorTests
{
    private static string? Validate(string text) => HotkeyGesture.TryParse(text, out _) ? null : $"「{text}」は解釈できません";

    [Fact]
    public void InvalidHotkey_IsRevertedToLastGood()
    {
        var current = new HotkeySettings { Panic = "M" };
        var lastGood = new HotkeySettings { Panic = "Ctrl+Alt+P" };
        var messages = HotkeySettingsValidator.RevertInvalid(current, lastGood, Validate);
        Assert.Equal("Ctrl+Alt+P", current.Panic);
        Assert.Single(messages);
    }

    [Fact]
    public void DuplicateHotkey_IsRevertedToLastGood()
    {
        var current = new HotkeySettings { Panic = "Ctrl+Alt+M" }; // マスクON/OFFと同じ
        var lastGood = new HotkeySettings();
        var messages = HotkeySettingsValidator.RevertInvalid(current, lastGood, Validate);
        Assert.Equal("Ctrl+Alt+P", current.Panic);
        Assert.Single(messages);
    }

    [Fact]
    public void ValidHotkeys_AreKept()
    {
        var current = new HotkeySettings { Panic = "Ctrl+Alt+F12" };
        var messages = HotkeySettingsValidator.RevertInvalid(current, new HotkeySettings(), Validate);
        Assert.Equal("Ctrl+Alt+F12", current.Panic);
        Assert.Empty(messages);
    }
}

public class OcclusionPolicyTests
{
    private const uint Overlapped = 0x00C00000 | 0x00040000; // タイトルバー + サイズ変更枠

    [Fact]
    public void NormalAppWindow_Occludes()
        => Assert.True(StageMask.Core.Geometry.OcclusionPolicy.CanOcclude(Overlapped, 0, "メモ帳"));

    [Fact]
    public void FramelessResizableAppWindow_Occludes() // Electron のタイトルバーなしウィンドウなど
        => Assert.True(StageMask.Core.Geometry.OcclusionPolicy.CanOcclude(0x80000000 | 0x00040000, 0, "Claude"));

    [Theory]
    [InlineData(0x80000000u, 0L, "Overlay")]                 // 枠のないポップアップ(全画面オーバーレイなど)
    [InlineData(Overlapped, 0x00080000L, "App")]             // 透明度を持てるウィンドウ
    [InlineData(Overlapped, 0x00000020L, "App")]             // クリックが素通りする
    [InlineData(Overlapped, 0x00000080L, "App")]             // ツールウィンドウ
    [InlineData(Overlapped, 0x08000000L, "App")]             // アクティブにならない
    [InlineData(Overlapped, 0L, "")]                         // タイトルなし
    public void UncertainWindows_DoNotOcclude(uint style, long ex, string title)
        => Assert.False(StageMask.Core.Geometry.OcclusionPolicy.CanOcclude(style, ex, title));
}
