using StageMask.Core.Settings;
using Xunit;

namespace StageMask.Core.Tests;

public class MaskContentTests
{
    private static AppearanceSettings Common(bool showLabel = true, string text = "MASKED", string image = "")
        => new() { ShowLabel = showLabel, LabelText = text, LabelImagePath = image };

    [Fact]
    public void UsesCommonSettings_ByDefault()
    {
        var r = MaskContentResolver.Resolve(Common(image: @"C:\img\logo.png"), new MaskContentOverride(), MaskStyle.Solid);
        Assert.Equal("MASKED", r.Text);
        Assert.Equal(@"C:\img\logo.png", r.ImagePath);
    }

    [Fact]
    public void CommonLabelOff_HidesText()
    {
        var r = MaskContentResolver.Resolve(Common(showLabel: false), null, MaskStyle.Solid);
        Assert.Null(r.Text);
        Assert.Equal(ResolvedMaskContent.None, r);
    }

    [Fact]
    public void Override_ReplacesCommon()
    {
        var custom = new MaskContentOverride { UseCustom = true, Text = "顧客情報のため非表示", ImagePath = "" };
        var r = MaskContentResolver.Resolve(Common(image: @"C:\img\logo.png"), custom, MaskStyle.Solid);
        Assert.Equal("顧客情報のため非表示", r.Text);
        Assert.Null(r.ImagePath); // 個別設定で画像なし
    }

    [Fact]
    public void Override_EmptyText_MeansNoText_EvenIfCommonHasText()
    {
        var custom = new MaskContentOverride { UseCustom = true, Text = "  ", ImagePath = @"C:\img\a.png" };
        var r = MaskContentResolver.Resolve(Common(), custom, MaskStyle.Solid);
        Assert.Null(r.Text);
        Assert.Equal(@"C:\img\a.png", r.ImagePath);
    }

    [Theory]
    [InlineData(MaskStyle.Mosaic)]
    [InlineData(MaskStyle.Blur)]
    public void MosaicAndBlur_ShowTextButNoImage(MaskStyle style)
    {
        var r = MaskContentResolver.Resolve(Common(image: @"C:\img\logo.png"), null, style);
        Assert.Equal("MASKED", r.Text);
        Assert.Null(r.ImagePath);
    }

    [Fact]
    public void QuotedPath_IsUnquoted()
    {
        // エクスプローラーの「パスのコピー」は "..." で囲まれる
        var r = MaskContentResolver.Resolve(Common(image: "\"C:\\img\\logo.png\""), null, MaskStyle.Solid);
        Assert.Equal(@"C:\img\logo.png", r.ImagePath);
    }

    [Fact]
    public void OldSettingsFile_WithoutNewFields_LoadsDefaults()
    {
        var s = SettingsStore.Deserialize("{ \"ManualMasks\": [ { \"X\": 1, \"Y\": 2, \"Width\": 3, \"Height\": 4 } ], \"WindowRules\": [ { \"ProcessName\": \"OUTLOOK\" } ] }")!;
        Assert.Equal(MaskStyle.Solid, s.ManualMasks[0].Style);
        Assert.NotNull(s.ManualMasks[0].Content);
        Assert.False(s.ManualMasks[0].Content.UseCustom);
        Assert.NotNull(s.WindowRules[0].Content);
        Assert.Equal("", s.Appearance.LabelImagePath);
    }
}
