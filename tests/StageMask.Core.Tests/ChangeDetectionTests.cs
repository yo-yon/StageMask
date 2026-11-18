using System;
using System.Linq;
using StageMask.Core.Detection;
using StageMask.Core.Geometry;
using StageMask.Core.Imaging;
using StageMask.Core.Settings;
using Xunit;

namespace StageMask.Core.Tests;

/// <summary>画面の変化検知(未確認部分を先に隠す・変化した行だけOCRする)のテスト。</summary>
public class ChangeDetectionTests
{
    private const int W = 192, H = 144; // 48px タイルで 4列 × 3行

    private static byte[] Blank(byte v = 255)
    {
        var px = new byte[W * H * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
        return px;
    }

    private static void FillRect(byte[] px, int x, int y, int w, int h, byte v)
    {
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
            {
                int p = (yy * W + xx) * 4;
                px[p] = px[p + 1] = px[p + 2] = v;
            }
    }

    private static FrameSignature Sig(byte[] px) => FrameSignature.Compute(px, W, H, W * 4);

    [Fact]
    public void Layout()
    {
        var s = Sig(Blank());
        Assert.Equal(4, s.Columns);
        Assert.Equal(3, s.Rows);
        Assert.Equal(new RectD(144, 96, 48, 48), s.TileRect(3, 2));
    }

    [Fact]
    public void NoBasis_AllTilesAreChanged()
    {
        Assert.Equal(12, Sig(Blank()).ChangedTiles(null).Count);
    }

    [Fact]
    public void SameImage_NoChange()
    {
        Assert.Empty(Sig(Blank()).ChangedTiles(Sig(Blank())));
    }

    [Fact]
    public void TextAppears_OnlyThatTileChanges()
    {
        var after = Blank();
        FillRect(after, 60, 60, 20, 16, 0); // タイル(1,1)の中に黒い文字の塊
        var changed = Sig(after).ChangedTiles(Sig(Blank()));
        Assert.Equal((1, 1), Assert.Single(changed));
    }

    [Fact]
    public void BlinkingCaret_IsIgnored()
    {
        var after = Blank();
        FillRect(after, 70, 52, 1, 20, 0); // 幅1px のカーソル
        Assert.Empty(Sig(after).ChangedTiles(Sig(Blank())));
    }

    [Fact]
    public void SmallTypedCharacter_IsDetected()
    {
        var after = Blank();
        FillRect(after, 52, 52, 8, 16, 0); // 1文字分(8x16)
        Assert.Single(Sig(after).ChangedTiles(Sig(Blank())));
    }

    [Fact]
    public void TwoPixelCaret_IsIgnored()
    {
        var after = Blank();
        FillRect(after, 70, 52, 2, 20, 0); // 幅2px のカーソル
        Assert.Empty(Sig(after).ChangedTiles(Sig(Blank())));
    }

    [Fact]
    public void PartialGlyphAtTileEdge_IsDetected()
    {
        // 行の境目: 文字の上端(高さ3px・細い線)だけがタイル(1,0)の下端に入っている。v0.5.2 では見逃していた
        var after = Blank();
        for (int x = 50; x < 90; x += 5) FillRect(after, x, 45, 1, 3, 0);
        Assert.Contains((1, 0), Sig(after).ChangedTiles(Sig(Blank())));
    }

    [Fact]
    public void ThinTextStrokes_AreDetected()
    {
        // 1px の細い線で書かれた文字(4px 間隔の間引きでは拾えなかった位置)
        var after = Blank();
        FillRect(after, 61, 61, 12, 1, 0);
        FillRect(after, 61, 65, 12, 1, 0);
        Assert.Single(Sig(after).ChangedTiles(Sig(Blank())));
    }

    [Fact]
    public void Dilate_AddsNeighbors()
    {
        var sig = Sig(Blank());
        Assert.Equal(9, sig.Dilate(new[] { (1, 1) }).Count);        // 中央 → 3x3
        Assert.Equal(4, sig.Dilate(new[] { (0, 0) }).Count);        // 角 → 2x2
        Assert.Empty(sig.Dilate(Array.Empty<(int, int)>()));
    }

    [Fact]
    public void SlightNoise_IsIgnored()
    {
        Assert.Empty(Sig(Blank(250)).ChangedTiles(Sig(Blank(255)))); // 明るさ5の違い(圧縮ノイズ程度)
    }

    [Fact]
    public void Scroll_ChangesManyTiles()
    {
        var before = Blank();
        for (int y = 0; y < H; y += 12) FillRect(before, 4, y, 180, 6, 0); // 文字の行が並んだ画面
        var after = Blank();
        for (int y = 6; y < H; y += 12) FillRect(after, 4, y, 180, 6, 0);  // 6px スクロール
        Assert.True(Sig(after).ChangedTiles(Sig(before)).Count >= 10);
    }

    [Fact]
    public void ResizedImage_IsAllChanged()
    {
        var small = FrameSignature.Compute(new byte[96 * 96 * 4], 96, 96, 96 * 4);
        Assert.Equal(12, Sig(Blank()).ChangedTiles(small).Count);
    }

    // ---------- OCR する横帯 ----------
    [Fact]
    public void Bands_ExpandByMarginAndMerge()
    {
        var s = Sig(Blank());
        var bands = s.Bands(new[] { (0, 0), (3, 2) }, margin: 1);
        Assert.Equal((0, 2), Assert.Single(bands)); // 0行目±1 と 2行目±1 がつながる
    }

    [Fact]
    public void Bands_NoMargin_StaySeparate()
    {
        var s = Sig(Blank());
        var bands = s.Bands(new[] { (0, 0), (0, 2) }, margin: 0);
        Assert.Equal(2, bands.Count);
        Assert.Equal(new RectD(0, 96, W, 48), s.BandRect(2, 2));
    }

    [Fact]
    public void Bands_Empty()
    {
        Assert.Empty(Sig(Blank()).Bands(Array.Empty<(int, int)>()));
    }

    // ---------- 確認済みの更新 ----------
    [Fact]
    public void VerifiedUpdate_OnlyCopiedRowsBecomeVerified()
    {
        var verified = Sig(Blank());
        var now = Blank();
        FillRect(now, 10, 10, 30, 30, 0);   // 0行目の変化
        FillRect(now, 10, 100, 30, 30, 0);  // 2行目の変化
        var current = Sig(now);

        // 0行目だけOCRした → 0行目は確認済み、2行目はまだ未確認
        var updated = FrameSignature.WithRowsFrom(verified, current, new[] { (0, 0) });
        var stillChanged = current.ChangedTiles(updated);
        Assert.All(stillChanged, t => Assert.Equal(2, t.Row));
        Assert.NotEmpty(stillChanged);
    }

    [Fact]
    public void VerifiedUpdate_WithoutBasis_CopiesAll()
    {
        var current = Sig(Blank());
        var updated = FrameSignature.WithRowsFrom(null, current, Array.Empty<(int, int)>());
        Assert.Empty(current.ChangedTiles(updated));
    }

    // ---------- 検出結果の保持(変化していない部分は延長) ----------
    [Fact]
    public void HoldBuffer_ExtendOutside_KeepsUnchangedDetections()
    {
        var buf = new DetectionHoldBuffer<string>();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var hold = TimeSpan.FromSeconds(3);
        buf.Update(1, new[]
        {
            (new RectD(0, 0, 50, 10), DetectionCategory.Email, (string?)"上"),
            (new RectD(0, 100, 50, 10), DetectionCategory.Email, (string?)"下"),
        }, t0, hold);

        // 2秒後: 下半分だけ変化してOCRした → 上の検出は延長、下は延長しない
        buf.ExtendOutside(1, new[] { new RectD(0, 90, 200, 50) }, t0.AddSeconds(2), hold);

        var at4 = buf.GetActive(1, t0.AddSeconds(4));
        Assert.Equal("上", Assert.Single(at4).Payload);
    }

    // ---------- 設定(v0.5.0で追加) ----------
    [Fact]
    public void ScrollSettings_DefaultsAreSafe()
    {
        var d = new DetectionSettings();
        Assert.True(d.MaskUnverifiedChanges);
        Assert.True(d.MaskOnWheel);
        Assert.Equal(100, d.ChangeCheckMs);
        Assert.True(d.WheelMaskMs > 0);
    }

    [Fact]
    public void ScrollSettings_RoundTrip_AndOldFileGetsDefaults()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stagemask-test-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var path = System.IO.Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path);
            var s = store.Load();
            s.Detection.MaskOnWheel = false;
            s.Detection.ChangeCheckMs = 250;
            store.Save(s);
            var loaded = new SettingsStore(path).Load();
            Assert.False(loaded.Detection.MaskOnWheel);
            Assert.Equal(250, loaded.Detection.ChangeCheckMs);

            // v0.4.0 以前の設定ファイル(項目なし)を読んだときは、安全側の既定値になる
            System.IO.File.WriteAllText(path, "{\"Detection\":{\"Enabled\":true}}");
            var old = new SettingsStore(path).Load();
            Assert.True(old.Detection.MaskOnWheel);
            Assert.True(old.Detection.MaskUnverifiedChanges);
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }

    // ---------- v0.5.4: スクロール後のずれたマスク ----------
    [Fact]
    public void ReplaceIn_RemovesStaleDetectionsInChangedArea_KeepsOthers()
    {
        var buf = new DetectionHoldBuffer<string>();
        var now = DateTime.UtcNow;
        var hold = TimeSpan.FromSeconds(3);
        buf.Update(1, new (RectD, DetectionCategory, string?)[]
        {
            (new RectD(10, 10, 50, 10), DetectionCategory.Identifier, "old-moved"),   // 内容が変わった場所
            (new RectD(10, 200, 50, 10), DetectionCategory.Identifier, "unchanged"),  // 変わっていない場所
        }, now, hold);

        buf.ReplaceIn(1, new[] { new RectD(0, 0, 96, 48) },
            new (RectD, DetectionCategory, string?)[] { (new RectD(10, 30, 50, 10), DetectionCategory.Identifier, "new") },
            now, hold);

        var payloads = buf.GetActive(1, now).Select(i => i.Payload).OrderBy(p => p).ToList();
        Assert.Equal("new,unchanged", string.Join(",", payloads));
    }

    [Fact]
    public void OldSettings_LargeChangeDefaultIsMigrated()
    {
        var json = "{\"Version\":1,\"Detection\":{\"LargeChangePercent\":25}}";
        var s = SettingsStore.Deserialize(json)!;
        Assert.Equal(10, s.Detection.LargeChangePercent);
        Assert.Equal(2, s.Version);

        var custom = SettingsStore.Deserialize("{\"Version\":1,\"Detection\":{\"LargeChangePercent\":40}}")!;
        Assert.Equal(40, custom.Detection.LargeChangePercent); // 自分で変えた値はそのまま
    }

    // ---------- v0.5.8: 前回の画像を使った指紋の計算(高速化) ----------
    [Fact]
    public void IncrementalCompute_EqualsFullCompute()
    {
        var before = Blank();
        FillRect(before, 10, 10, 30, 8, 0);
        var after = (byte[])before.Clone();
        FillRect(after, 100, 60, 20, 12, 0);       // 1か所だけ変える
        var prev = Sig(before);
        var inc = FrameSignature.Compute(after, W, H, W * 4, prev, before, W * 4);
        var full = Sig(after);
        Assert.Empty(inc.ChangedTiles(full));
        Assert.Empty(full.ChangedTiles(inc));
        Assert.Equal(full.ChangedTiles(prev).Count, inc.ChangedTiles(prev).Count);
    }

    [Fact]
    public void IncrementalCompute_DifferentSize_FallsBackToFull()
    {
        var small = new byte[96 * 96 * 4];
        var prev = FrameSignature.Compute(small, 96, 96, 96 * 4);
        var inc = FrameSignature.Compute(Blank(), W, H, W * 4, prev, small, 96 * 4);
        Assert.Empty(inc.ChangedTiles(Sig(Blank())));
    }
}
