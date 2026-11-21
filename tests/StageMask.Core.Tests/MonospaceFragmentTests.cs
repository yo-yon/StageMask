using System.Collections.Generic;
using StageMask.Core.Geometry;
using System.Linq;
using StageMask.Core.Detection;
using Xunit;
using static StageMask.Core.Tests.TestHelpers;

namespace StageMask.Core.Tests;

/// <summary>等幅フォントで細い文字の前後に OCR が空白を入れた場合でも、キー全体を隠す。</summary>
public class MonospaceFragmentTests
{
    private static void AssertWholeLineCovered(string text, DetectionCategory category)
    {
        var line = Line(text);
        var right = line.Words[^1].Box.Right;
        var hits = Detect(line).Where(r => r.Category == category).ToList();
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Box.X <= 0.5 && h.Box.Right >= right - 0.5);
    }

    [Theory]
    [InlineData("AK I AABCDEFGH I JKLMNOP")]
    [InlineData("ghp_AbCdEfGh I jK l MnOpQrStUvWxYz0123456789")]
    [InlineData("sk-proj-AbCdEfGh I jK l MnOpQrStUvWxYz0123456789")]
    public void SplitKey_IsCoveredEntirely(string text) => AssertWholeLineCovered(text, DetectionCategory.Secret);

    [Fact]
    public void SplitTenantName_IsDetected()
        => Assert.Contains(Detect(Line("contoso.onm i crosoft.com")), r => r.Category == DetectionCategory.Identifier);

    [Fact]
    public void OrdinarySentence_IsNotSecret()
        => Assert.DoesNotContain(Detect(Line("I am at the office today")), r => r.Category == DetectionCategory.Secret);
}

public class FullWidthFragmentTests
{
    [Fact]
    public void FullWidthTail_IsCoveredByExtension()
    {
        // 日本語 OCR が後半を全角で返した場合(「ＩｊＫｌ…」)でも、キーの後ろまで隠す
        var line = TestHelpers.Line("sk-proj-AbCdEfGh ＩｊＫｌＭｎＯｐＱｒＳｔＵｖＷｘＹｚ0123456789");
        var right = line.Words[^1].Box.Right;
        var hits = TestHelpers.Detect(line).Where(r => r.Category == DetectionCategory.Secret).ToList();
        Assert.Contains(hits, h => h.Box.Right >= right - 0.5);
    }
}

/// <summary>
/// 大きい文字(メモ帳 150%〜170%)では、細い文字の前後のすき間が広く読まれ、
/// 「Ij」「roj」のようにふつうの単語に見える断片に分かれることがある。また「_」が読み落とされることがある。
/// </summary>
public class LargeZoomFragmentTests
{
    // 等幅: 1文字 10px、高さ 20px。単語間のすき間を指定できる
    private static OcrLineBox Mono(double gap, params string[] words)
    {
        var list = new List<OcrWordBox>();
        double x = 0;
        foreach (var w in words)
        {
            list.Add(new OcrWordBox(w, new RectD(x, 0, w.Length * 10, 20)));
            x += w.Length * 10 + gap;
        }
        return new OcrLineBox(list);
    }

    private static void AssertCovered(OcrLineBox line, DetectionCategory category)
    {
        double left = line.Words[0].Box.X, right = line.Words[^1].Box.Right;
        var hits = TestHelpers.Detect(line).Where(r => r.Category == category).ToList();
        Assert.Contains(hits, h => h.Box.X <= left + 0.5 && h.Box.Right >= right - 0.5);
    }

    [Fact]
    public void OpenAiKey_SplitIntoPlainLookingPieces()
        => AssertCovered(Mono(7, "sk-p", "roj", "-AbCdEfGh", "Ij", "KlMnOpQrStUvWxYz0123456789"), DetectionCategory.Secret);

    [Fact]
    public void GitHubToken_UnderscoreLost()
        => AssertCovered(Mono(7, "ghp", "AbCdEfGh", "Ij", "KlMnOpQrStUvWxYz0123456789"), DetectionCategory.Secret);

    [Fact]
    public void GitHubToken_SplitAfterPrefix()
        => AssertCovered(Mono(7, "ghp_AbCdEfGh", "Ij", "Kl", "MnOpQrStUvWxYz0123456789"), DetectionCategory.Secret);

    [Fact]
    public void TenantEmail_SplitInDomain()
        => AssertCovered(Mono(7, "yoh@contoso.onmic", "rosoft.com"), DetectionCategory.Email);

    [Fact]
    public void Email_SplitInLocalPart()
        => AssertCovered(Mono(7, "tar", "o@example.com"), DetectionCategory.Email);

    [Fact]
    public void Email_DoesNotSwallowFollowingWord()
    {
        var line = Mono(5, "taro@example.com", "for", "details");
        var hits = TestHelpers.Detect(line).Where(r => r.Category == DetectionCategory.Email).ToList();
        Assert.NotEmpty(hits);
        Assert.DoesNotContain(hits, h => h.Box.Right > line.Words[0].Box.Right + 0.5);
    }

    [Fact]
    public void OrdinaryWords_AreNotSecret()
        => Assert.DoesNotContain(TestHelpers.Detect(Mono(7, "skip", "project", "github", "pages")), r => r.Category == DetectionCategory.Secret);
}

/// <summary>実機の OCR ダンプ(v0.5.36、メモ帳 150% / 170%)をそのまま再現したテスト。</summary>
public class RealOcrDumpTests
{
    private static OcrLineBox L(params (string T, double X, double Y, double W, double H)[] w)
        => new(w.Select(x => new OcrWordBox(x.T, new RectD(x.X, x.Y, x.W, x.H))).ToList());

    private static void AssertRightCovered(OcrLineBox line, double left, double right)
    {
        var hits = TestHelpers.Detect(line);
        Assert.True(hits.Any(h => h.Box.X <= left + 0.5) && hits.Any(h => h.Box.Right >= right - 0.5),
            string.Join(" | ", hits));
        // 左端から右端まで、すき間なく覆われていること
        double x = left;
        foreach (var h in hits.OrderBy(h => h.Box.X)) if (h.Box.X <= x + 12) x = System.Math.Max(x, h.Box.Right);
        Assert.True(x >= right - 0.5, $"covered to {x}: " + string.Join(" | ", hits));
    }

    [Fact]
    public void Zoom150_Email() => AssertRightCovered(
        L(("ta", 25, 95, 18, 15), ("「", 48, 99, 6, 11), ("o@example", 57, 94, 97, 18), (".", 159, 107, 3, 3), ("com", 167, 99, 32, 11)), 25, 199);

    [Fact]
    public void Zoom150_OpenAiKey() => AssertRightCovered(
        L(("sk-p", 24, 116, 42, 17), ("「", 70, 121, 6, 11), ("0j", 79, 116, 17, 18), ("-AbCdEfGhIjKIMnOpO", 102, 116, 196, 18),
          ("「", 301, 121, 6, 11), ("StUVWXYZ田23456789", 310, 116, 196, 16)), 24, 506);

    [Fact]
    public void Zoom150_GitHubToken() => AssertRightCovered(
        L(("ghp_AbCdEfGh", 24, 138, 130, 20), ("I", 159, 138, 3, 16), ("jK", 168, 138, 19, 18), ("IMnOpO", 193, 138, 61, 18),
          ("「", 257, 143, 6, 11), ("StUVWXYZ田23456789", 266, 138, 196, 16)), 24, 462);

    [Fact]
    public void Zoom150_TenantEmail() => AssertRightCovered(
        L(("yoh@contoso", 24, 204, 119, 19), (".", 148, 217, 3, 3), ("onmic", 156, 204, 53, 16), ("「", 213, 209, 6, 11),
          ("OSOft", 222, 204, 52, 16), (".", 280, 217, 3, 3), ("com", 288, 209, 32, 11)), 24, 320);

    [Fact]
    public void Zoom170_OpenAiKeyTail() => AssertRightCovered(
        L(("sk—pro", 23, 118, 76, 23), ("」", 102, 123, 8, 18), ("一", 113, 127, 13, 3)), 23, 126);

    // ---- v0.5.37 のダンプ(100%前後・120%・130%・180%)----
    [Fact]
    public void GitHubToken_UnderscoreReadAsHyphen_Split() => AssertRightCovered(
        L(("ghp-AbCdEfGhI", 22, 124, 102, 16), ("j", 127, 125, 4, 14), ("K", 134, 124, 8, 13), ("lMnOpQrStUvWxYz0123456789", 145, 124, 196, 15)), 22, 341);

    [Fact]
    public void ApiKey_SplitAndGhReadAsKanji() => AssertRightCovered(
        L(("ap", 22, 161, 16, 10), ("i", 41, 157, 2, 12), ("key:Abc123Def456", 54, 156, 127, 15), ("田", 182, 156, 16, 14), ("i", 201, 157, 2, 12), ("789", 206, 156, 23, 14)), 22, 229);

    [Fact]
    public void ApiKey_ShortFirstValuePiece() => AssertRightCovered(
        L(("ap", 22, 169, 17, 12), ("i", 43, 164, 2, 14), ("key=Abcl", 58, 164, 68, 17), ("23Def456Gh", 130, 164, 89, 15), ("i", 223, 164, 2, 14), ("789", 229, 164, 26, 15)), 22, 255);

    [Fact]
    public void Akia_WithUmlaut() => AssertRightCovered(
        L(("AK", 22, 146, 17, 14), ("IÄABCDEFGHIJKLMNOP", 42, 144, 159, 17)), 22, 201);

    [Fact]
    public void TenantEmail_PerCharacterWithKatakanaDot() => AssertRightCovered(
        L(("0", 31, 172, 6, 8), ("h@C", 39, 168, 22, 12), ("0", 63, 172, 6, 8), ("n", 71, 172, 6, 8), ("t", 79, 169, 4, 11), ("0", 87, 172, 6, 8),
          ("S", 95, 172, 6, 8), ("0", 103, 172, 6, 8), ("・", 113, 176, 3, 3), ("0", 119, 172, 6, 8), ("n", 127, 172, 6, 8), ("m", 135, 172, 6, 8),
          ("i", 145, 169, 2, 10), ("C", 151, 172, 6, 8), ("r", 160, 172, 5, 8), ("0", 167, 172, 6, 8), ("S", 175, 172, 6, 8), ("0", 183, 172, 6, 8),
          ("f", 191, 168, 4, 12), ("t", 199, 169, 4, 11), (".", 209, 176, 3, 3), ("C", 215, 172, 6, 8), ("0", 223, 172, 6, 8), ("m", 231, 172, 6, 8)), 23, 237);

    [Fact]
    public void TenantEmail_AtReadAsUmlaut() => AssertRightCovered(
        L(("yohöcontoso", 22, 155, 76, 14), ("・", 101, 164, 3, 3), ("onmicrosoft", 106, 157, 75, 11), (".com", 185, 160, 25, 8)), 22, 210);

    [Fact]
    public void TenantEmail_ComMissing_FirstLetterMissing() => AssertRightCovered(
        L(("oh@contoso", 33, 188, 98, 16), (".", 136, 200, 3, 3), ("onmic", 143, 188, 48, 15), ("「", 194, 193, 6, 10), ("OSOft", 203, 188, 47, 15), (".", 256, 200, 3, 3)), 23, 259);

    [Fact]
    public void TenantHost_UserPartNotRead() => AssertRightCovered(
        L(("contoso.", 54, 173, 61, 13), ("onm", 118, 177, 24, 9), ("i", 145, 173, 2, 12), ("crosof", 150, 172, 46, 14), ("t", 198, 173, 5, 13), (".", 208, 183, 3, 3), ("com", 214, 177, 24, 9)), 22, 238);

    [Fact]
    public void WrappedKey_SplitIntoTwoShortWords() => AssertRightCovered(
        L(("AbCdEfGhI", 22, 145, 114, 21), ("jKlMn0pQrStUvWxYz0123456789", 141, 145, 348, 24)), 22, 489);

    // ---- v0.5.38 のダンプ(110%・120%)----
    [Fact]
    public void TenantEmail_AtCReadAsKanji() => AssertRightCovered(
        L(("yoh", 22, 172, 24, 15), ("配", 46, 170, 15, 16), ("ontoso.", 62, 173, 53, 13), ("onm", 118, 177, 24, 9), ("i", 145, 173, 2, 12),
          ("crosof", 150, 172, 46, 14), ("t", 198, 173, 5, 13), (".", 208, 183, 3, 3), ("com", 214, 177, 24, 9)), 22, 238);

    [Fact]
    public void TenantEmail_HeavilyGarbled() => AssertRightCovered(
        L(("YOh", 22, 182, 26, 17), ("00nt0S0.", 58, 183, 69, 14), ("C", 166, 187, 8, 10), ("「", 176, 187, 6, 10), ("OSOft", 184, 182, 43, 15), (".", 232, 194, 3, 3)), 22, 227); // 末尾の「.」だけは対象外

    [Fact]
    public void MicrosoftLearnUrl_IsNotMasked()
        => Assert.Empty(TestHelpers.Detect(TestHelpers.Line("see learn.microsoft.com for details")));

    [Fact]
    public void TenantLabel_IsNotMasked()
    {
        var line = TestHelpers.Line("Tenant: contoso.onmicrosoft.com");
        Assert.DoesNotContain(TestHelpers.Detect(line), h => h.Box.X < line.Words[1].Box.X - 0.5);
    }

    // ---- v0.5.40 のダンプ(170%前後)----
    [Fact]
    public void ApiKey_KeywordMisread() => AssertRightCovered(
        L(("ao", 23, 240, 26, 16), ("i", 56, 233, 3, 23), ("kev=Abc123Def456Gh", 80, 233, 250, 23), ("i", 336, 233, 3, 23), ("789", 345, 233, 40, 23)), 80, 385);

    [Fact]
    public void OrdinaryAssignment_IsNotSecret()
        => Assert.DoesNotContain(TestHelpers.Detect(L(("timeout=30", 0, 0, 100, 20), ("mode=Default", 110, 0, 120, 20), ("name=SampleProject", 240, 0, 180, 20))), r => r.Category == DetectionCategory.Secret);

    [Fact]
    public void JapaneseQuotes_AreKept()
    {
        // 日本語の文章のかぎかっこは英字に読み替えないので、何も検出しない
        Assert.Empty(TestHelpers.Detect(TestHelpers.CharLine("「設定」を開く")));
    }
}

public class HoldBufferSameBatchTests
{
    [Xunit.Fact]
    public void LargerAndContainedDetections_InSameBatch_AreBothKept()
    {
        // 実機 190%: 「ghp AbCd…」(行頭から)と「AbCd…」(途中から)が同時に見つかった
        var buf = new DetectionHoldBuffer<object>();
        var now = System.DateTime.UtcNow;
        buf.Update(1, new (RectD, DetectionCategory, object?)[]
        {
            (new RectD(23, 149, 558, 27), DetectionCategory.Secret, null),
            (new RectD(78, 149, 503, 27), DetectionCategory.Secret, null),
        }, now, System.TimeSpan.FromSeconds(2));
        var active = buf.GetActive(1, now);
        Assert.Contains(active, i => i.Rect.X <= 23.5);
    }

    [Xunit.Fact]
    public void SameRegion_FromEarlierUpdate_IsReplaced()
    {
        var buf = new DetectionHoldBuffer<object>();
        var now = System.DateTime.UtcNow;
        buf.Update(1, new (RectD, DetectionCategory, object?)[] { (new RectD(0, 0, 100, 20), DetectionCategory.Secret, null) }, now, System.TimeSpan.FromSeconds(2));
        buf.Update(1, new (RectD, DetectionCategory, object?)[] { (new RectD(1, 0, 100, 20), DetectionCategory.Secret, null) }, now.AddMilliseconds(100), System.TimeSpan.FromSeconds(2));
        Assert.Single(buf.GetActive(1, now.AddMilliseconds(100)));
    }
}
