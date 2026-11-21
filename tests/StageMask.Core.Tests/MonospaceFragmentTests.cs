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

    [Fact]
    public void JapaneseQuotes_AreKept()
    {
        // 日本語の文章のかぎかっこは英字に読み替えないので、何も検出しない
        Assert.Empty(TestHelpers.Detect(TestHelpers.CharLine("「設定」を開く")));
    }
}
