using System.Linq;
using StageMask.Core.Detection;
using Xunit;
using static StageMask.Core.Tests.TestHelpers;

// 注意: テスト用の偽のキー(APIキー・トークン・秘密鍵の書き出しなど)は、"gh" + "p_..." のように
// 文字列を分けて書いている。GitHub のプッシュ保護などの検査ツールに本物のキーと誤認されないようにするため。
// 分けても結果は同じ文字列になるので、テストの内容は変わらない。

namespace StageMask.Core.Tests;

public class SensitiveTextDetectorTests
{
    [Theory]
    [InlineData("To: yoh@contoso.com")]
    [InlineData("admin@contoso.onmicrosoft.com")]
    [InlineData("連絡先 taro.yamada+test@example.co.jp まで")]
    [InlineData("ＭＡＩＬ：ｙｏｈ＠ｅｘａｍｐｌｅ．ｃｏｍ")] // 全角
    public void Email_IsDetected(string text)
    {
        var d = Detect(Line(text));
        Assert.Contains(d, x => x.Category == DetectionCategory.Email);
    }

    [Fact]
    public void Email_SplitAcrossOcrWords_IsDetected()
    {
        // OCRが記号の前後で単語を分割したケース
        var d = Detect(Line("yoh @ contoso . com"));
        var email = Assert.Single(d, x => x.Category == DetectionCategory.Email);
        // 4単語すべてを覆う(先頭単語の左端〜最後の単語の右端)
        Assert.Equal(0, email.Box.X);
        Assert.Equal(Line("yoh @ contoso . com").Words.Last().Box.Right, email.Box.Right);
    }

    [Fact]
    public void Email_BoxCoversOnlyMatchedWords()
    {
        var line = Line("Hello world yoh@contoso.com");
        var email = Assert.Single(Detect(line), x => x.Category == DetectionCategory.Email);
        Assert.Equal(line.Words[2].Box, email.Box);
    }

    [Theory]
    [InlineData("TenantId 72f988bf-86f1-41af-91ab-2d7cd011db47")]
    [InlineData("72F988BF-86F1-41AF-91AB-2D7CD011DB47")]
    [InlineData("72f988bf‐86f1‐41af‐91ab‐2d7cd011db47")]      // U+2010 ハイフン
    [InlineData("72f988bf－86f1－41af－91ab－2d7cd011db47")]  // 全角ハイフン
    [InlineData("72f988bf86f141af91ab2d7cd011db47")]          // ハイフンなし32桁
    [InlineData("72f988bf-86f1-41af-91ab-2d7cdO11db47")]      // 0 を O と誤読
    public void Guid_IsDetected(string text)
    {
        Assert.Contains(Detect(Line(text)), x => x.Category == DetectionCategory.Identifier);
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/demo")]
    [InlineData("contoso-my.sharepoint.com")]
    [InlineData("https://org1234.crm7.dynamics.com/")]
    [InlineData("mystorage.blob.core.windows.net")]
    [InlineData("kv-prod.vault.azure.net")]
    [InlineData("my-aoai.openai.azure.com")]
    public void TenantHost_IsDetected(string text)
    {
        Assert.Contains(Detect(Line(text)), x => x.Category == DetectionCategory.Identifier);
    }

    [Theory]
    [InlineData("OPENAI_API_KEY=sk" + "-proj-AbCdEf1234567890GhIjKlMnOp")]
    [InlineData("gh" + "p_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [InlineData("AKIA" + "IOSFODNN7EXAMPLE")]
    [InlineData("AI" + "zaSyA-1234567890abcdefghijklmnopqrstu")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U")]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=demo;AccountKey=Zm9vYmFyYmF6cXV4==;EndpointSuffix=core.windows.net")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("Password=P@ssw0rd123")]
    [InlineData("password: P@ssw0rd123")]
    [InlineData("-----BEGIN RSA " + "PRIVATE KEY-----")]
    [InlineData("abc8Q~AbCdEfGhIjKlMnOpQrStUvWxYz0123456789")]
    public void Secret_IsDetected(string text)
    {
        Assert.Contains(Detect(Line(text)), x => x.Category == DetectionCategory.Secret);
    }

    [Theory]
    [InlineData("Xk9pQ2mZ7vB4nR8tL1wY6cH3jF5sD0gA")] // 大小英字+数字のランダム文字列
    [InlineData("a3f1c9e07b2d4f68a1c3e5f7092b4d6f8a1c")]  // 長い16進数
    public void GenericToken_IsDetected(string token)
    {
        Assert.True(SensitiveTextDetector.LooksLikeRandomToken(token));
    }

    [Theory]
    [InlineData("Configuration")]
    [InlineData("https://make.powerapps.com/environments")]
    [InlineData("InternationalizationAndLocalization")] // 長いが英字のみ
    [InlineData("2026-11-17")]
    public void GenericToken_NormalWords_AreNotDetected(string word)
    {
        Assert.False(SensitiveTextDetector.LooksLikeRandomToken(word));
    }

    [Fact]
    public void GenericSecret_CanBeDisabled()
    {
        var options = new DetectorOptions { DetectGenericSecret = false };
        Assert.Empty(Detect(options, Line("value Xk9pQ2mZ7vB4nR8tL1wY6cH3jF5sD0gA")));
    }

    [Fact]
    public void PlainSentence_HasNoDetection()
    {
        var d = Detect(
            Line("Power Automate でフローを作成します"),
            Line("今日の登壇資料は 2026年11月17日 版です"),
            CharLine("会議室の予約を確認してください"));
        Assert.Empty(d);
    }

    [Fact]
    public void NgWord_Japanese_SplitIntoChars_IsDetected()
    {
        var options = new DetectorOptions { NgWords = new[] { "朝日商事" } };
        var line = CharLine("顧客: 朝日商事 様");
        var hit = Assert.Single(Detect(options, line), x => x.Category == DetectionCategory.NgWord);
        // 「朝日商事」の4文字分だけを覆う
        var expected = line.Words[3].Box.Union(line.Words[6].Box);
        Assert.Equal(expected, hit.Box);
    }

    [Fact]
    public void NgWord_IgnoresSpacesWidthAndCase()
    {
        var options = new DetectorOptions { NgWords = new[] { "Contoso Ltd" } };
        Assert.Contains(Detect(options, Line("ＣＯＮＴＯＳＯ ＬＴＤ 御中")), x => x.Category == DetectionCategory.NgWord);
    }

    [Fact]
    public void NgWord_Fuzzy_ToleratesOneMisread()
    {
        var options = new DetectorOptions { NgWords = new[] { "朝日商事株式会社" } };
        // 「商」を「啇」と誤読したケース
        Assert.Contains(Detect(options, CharLine("朝日啇事株式会社")), x => x.Category == DetectionCategory.NgWord);
    }

    [Fact]
    public void NgWord_Fuzzy_CanBeDisabled()
    {
        var options = new DetectorOptions { NgWords = new[] { "朝日商事株式会社" }, FuzzyNgWords = false };
        Assert.Empty(Detect(options, CharLine("朝日啇事株式会社")));
    }

    [Fact]
    public void NgWord_ShortWord_IsNotFuzzyMatched()
    {
        // 3文字以下はあいまい一致しない(誤検出が多くなるため)
        var options = new DetectorOptions { NgWords = new[] { "山田" } };
        Assert.Empty(Detect(options, CharLine("山口さん")));
    }

    [Fact]
    public void Categories_CanBeDisabled()
    {
        var options = new DetectorOptions
        {
            DetectEmail = false, DetectIdentifier = false, DetectSecret = false, DetectNgWords = false,
        };
        Assert.Empty(Detect(options, Line("yoh@contoso.com 72f988bf-86f1-41af-91ab-2d7cd011db47 AKIA" + "IOSFODNN7EXAMPLE")));
    }

    [Fact]
    public void MultipleLines_ReturnSeparateBoxes()
    {
        var d = Detect(Line("a@example.com", y: 0), Line("b@example.com", y: 100));
        var emails = d.Where(x => x.Category == DetectionCategory.Email).ToList();
        Assert.Equal(2, emails.Count);
        Assert.Contains(emails, e => e.Box.Y == 0);
        Assert.Contains(emails, e => e.Box.Y == 100);
    }
}
