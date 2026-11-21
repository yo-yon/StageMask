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
