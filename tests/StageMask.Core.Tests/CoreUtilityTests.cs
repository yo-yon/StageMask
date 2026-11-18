using System;
using System.Linq;
using StageMask.Core.Detection;
using StageMask.Core.Geometry;
using StageMask.Core.Imaging;
using StageMask.Core.Settings;
using Xunit;

namespace StageMask.Core.Tests;

public class CoreUtilityTests
{
    // ---------- RectD ----------
    [Fact]
    public void RectD_MergeOverlapping_MergesChains()
    {
        var merged = RectD.MergeOverlapping(new[]
        {
            new RectD(0, 0, 10, 10),
            new RectD(5, 5, 10, 10),
            new RectD(100, 100, 5, 5),
        });
        Assert.Equal(2, merged.Count);
        Assert.Contains(new RectD(0, 0, 15, 15), merged);
    }

    [Fact]
    public void RectD_Intersect()
    {
        var r = new RectD(0, 0, 10, 10).Intersect(new RectD(5, 5, 10, 10));
        Assert.Equal(new RectD(5, 5, 5, 5), r);
        Assert.True(new RectD(0, 0, 10, 10).Intersect(new RectD(20, 20, 5, 5)).IsEmpty);
    }

    // ---------- HoldBuffer ----------
    [Fact]
    public void HoldBuffer_KeepsItemsUntilExpiry()
    {
        var buf = new DetectionHoldBuffer<string>();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        buf.Update(1, new[] { (new RectD(0, 0, 10, 10), DetectionCategory.Email, (string?)"a") }, t0, TimeSpan.FromSeconds(3));

        Assert.Single(buf.GetActive(1, t0.AddSeconds(2)));
        Assert.Empty(buf.GetActive(1, t0.AddSeconds(3.1)));
    }

    [Fact]
    public void HoldBuffer_SameRegion_ExtendsInsteadOfDuplicating()
    {
        var buf = new DetectionHoldBuffer<string>();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var hold = TimeSpan.FromSeconds(3);
        buf.Update(1, new[] { (new RectD(0, 0, 100, 20), DetectionCategory.Email, (string?)null) }, t0, hold);
        buf.Update(1, new[] { (new RectD(1, 0, 100, 20), DetectionCategory.Email, (string?)null) }, t0.AddSeconds(2), hold);

        var active = buf.GetActive(1, t0.AddSeconds(4));
        Assert.Single(active);
    }

    [Fact]
    public void HoldBuffer_EmptyUpdate_DoesNotClearUnexpired()
    {
        // OCRが一瞬読めなかっても、保持時間内はマスクを続ける
        var buf = new DetectionHoldBuffer<string>();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var hold = TimeSpan.FromSeconds(3);
        buf.Update(1, new[] { (new RectD(0, 0, 10, 10), DetectionCategory.Email, (string?)null) }, t0, hold);
        buf.Update(1, Array.Empty<(RectD, DetectionCategory, string?)>(), t0.AddSeconds(1), hold);
        Assert.Single(buf.GetActive(1, t0.AddSeconds(2)));
    }

    [Fact]
    public void HoldBuffer_RetainOnly_RemovesOtherTargets()
    {
        var buf = new DetectionHoldBuffer<string>();
        var t0 = DateTime.UtcNow;
        var item = new[] { (new RectD(0, 0, 10, 10), DetectionCategory.Email, (string?)null) };
        buf.Update(1, item, t0, TimeSpan.FromMinutes(1));
        buf.Update(2, item, t0, TimeSpan.FromMinutes(1));
        buf.RetainOnly(new long[] { 2 });
        Assert.Empty(buf.GetActive(1, t0));
        Assert.Single(buf.GetActive(2, t0));
    }

    // ---------- PixelDownscaler ----------
    [Fact]
    public void Downscale_AveragesBlocks()
    {
        // 4x2 画像: 左半分 黒(0)、右半分 白(255) → 2x1 に縮小
        int w = 4, h = 2, stride = w * 4;
        var src = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = x < 2 ? (byte)0 : (byte)255;
                int p = y * stride + x * 4;
                src[p] = src[p + 1] = src[p + 2] = v;
                src[p + 3] = 255;
            }

        var dst = PixelDownscaler.Downscale(src, w, h, stride, 0, 0, w, h, 2, 1);
        Assert.NotNull(dst);
        Assert.Equal(2, dst!.Width);
        Assert.Equal(1, dst.Height);
        Assert.Equal(0, dst.Pixels[0]);
        Assert.Equal(255, dst.Pixels[4]);
    }

    [Fact]
    public void Downscale_ClampsCropToImage()
    {
        var src = new byte[4 * 4 * 4];
        var dst = PixelDownscaler.Downscale(src, 4, 4, 16, -10, -10, 100, 100, 2, 2);
        Assert.NotNull(dst);
        Assert.Equal(2 * 2 * 4, dst!.Pixels.Length);
        Assert.Null(PixelDownscaler.Downscale(src, 4, 4, 16, 10, 10, 5, 5, 2, 2));
    }

    // ---------- Settings ----------
    [Fact]
    public void Settings_RoundTrip()
    {
        var s = new AppSettings();
        s.ManualMasks.Add(new ManualMask { X = 1, Y = 2, Width = 3, Height = 4, Label = "通知" });
        s.WindowRules.Add(new WindowRule { ProcessName = "OUTLOOK", Action = RuleAction.AutoDetect, Style = MaskStyle.Mosaic });
        s.Detection.NgWords.Add("朝日商事");

        var json = SettingsStore.Serialize(s);
        Assert.Contains("\"AutoDetect\"", json); // enum は文字列で保存
        Assert.DoesNotContain("朝日商事", json);       // NGワードは平文で書かない
        var back = SettingsStore.Deserialize(json)!;

        Assert.Equal("通知", back.ManualMasks.Single().Label);
        Assert.Equal(RuleAction.AutoDetect, back.WindowRules.Single().Action);
        Assert.Equal(MaskStyle.Mosaic, back.WindowRules.Single().Style);
    }

    [Fact]
    public void Settings_MissingSections_AreFilledWithDefaults()
    {
        var s = SettingsStore.Deserialize("{ \"Version\": 1, \"Detection\": null }")!;
        Assert.NotNull(s.Detection);
        Assert.NotNull(s.Detection.NgWords);
        Assert.NotNull(s.Hotkeys);
    }

    [Fact]
    public void SettingsStore_BrokenFile_ReturnsDefaults()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stagemask-test-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "settings.json");
        System.IO.File.WriteAllText(path, "{ broken json");
        var store = new SettingsStore(path);
        var s = store.Load();
        Assert.NotNull(s);
        Assert.True(System.IO.File.Exists(path + ".bak"));

        store.Save(s);
        Assert.NotNull(new SettingsStore(path).Load());
        System.IO.Directory.Delete(dir, true);
    }

    // ---------- Hotkey ----------
    [Theory]
    [InlineData("Ctrl+Alt+M", HotkeyModifiers.Control | HotkeyModifiers.Alt, "M")]
    [InlineData("ctrl + shift + F12", HotkeyModifiers.Control | HotkeyModifiers.Shift, "F12")]
    [InlineData("Win+Alt+P", HotkeyModifiers.Win | HotkeyModifiers.Alt, "P")]
    public void Hotkey_Parse(string text, HotkeyModifiers mods, string key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal(mods, g.Modifiers);
        Assert.Equal(key, g.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("M")]            // 修飾キーなし
    [InlineData("Ctrl+Alt")]     // キーなし
    [InlineData("Ctrl+A+B")]     // キーが2つ
    public void Hotkey_Parse_Invalid(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }

    // ---------- WindowRuleMatcher ----------
    [Theory]
    [InlineData("OUTLOOK", "", "outlook", "受信トレイ - yoh@contoso.com", true)]
    [InlineData("outlook.exe", "", "OUTLOOK", "x", true)]
    [InlineData("msedge", "Azure", "msedge", "Home - Microsoft Azure", true)]
    [InlineData("msedge", "Azure", "msedge", "Power Apps", false)]
    [InlineData("", "Teams", "ms-teams", "チャット | Microsoft Teams", true)]
    [InlineData("chrome", "", "msedge", "x", false)]
    [InlineData("", "", "msedge", "x", false)] // 条件なしのルールは何にも一致しない
    public void WindowRule_Matches(string ruleProc, string ruleTitle, string proc, string title, bool expected)
    {
        var rule = new WindowRule { ProcessName = ruleProc, TitleContains = ruleTitle };
        Assert.Equal(expected, WindowRuleMatcher.Matches(rule, proc, title));
    }

    [Fact]
    public void WindowRule_Disabled_NeverMatches()
    {
        var rule = new WindowRule { ProcessName = "outlook", Enabled = false };
        Assert.False(WindowRuleMatcher.Matches(rule, "outlook", ""));
    }
}
