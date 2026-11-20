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
