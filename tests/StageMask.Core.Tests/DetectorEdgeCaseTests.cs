using System.Linq;
using StageMask.Core.Detection;
using StageMask.Core.Geometry;
using Xunit;
using static StageMask.Core.Tests.TestHelpers;

// 注意: テスト用の偽のキー(APIキー・トークン・秘密鍵の書き出しなど)は、"gh" + "p_..." のように
// 文字列を分けて書いている。GitHub のプッシュ保護などの検査ツールに本物のキーと誤認されないようにするため。
// 分けても結果は同じ文字列になるので、テストの内容は変わらない。

namespace StageMask.Core.Tests;

/// <summary>検出ロジックの境界値・誤検出・取りこぼしのテスト。</summary>
public class DetectorEdgeCaseTests
{
    // ---------- メールアドレス ----------
    [Fact]
    public void Email_InsideJapaneseSentence_CoversOnlyAddress()
    {
        var line = CharLine("メールはyoh@contoso.comです");
        var hit = Assert.Single(Detect(line), x => x.Category == DetectionCategory.Email);
        // 「メールは」(0〜3文字目)と「です」は含めない
        Assert.Equal(line.Words[4].Box.Union(line.Words[18].Box), hit.Box);
    }

    /// <summary>
    /// 回帰テスト: 日本語OCRは英数字も1文字ずつの単語に分けることがある。
    /// その場合でもメール・GUID・キーを検出できること(v0.2.0までは取りこぼしていた)。
    /// </summary>
    [Theory]
    [InlineData("連絡先:yoh@contoso.com", DetectionCategory.Email)]
    [InlineData("テナントID:72f988bf-86f1-41af-91ab-2d7cd011db47", DetectionCategory.Identifier)]
    [InlineData("キー:AKIA" + "IOSFODNN7EXAMPLE", DetectionCategory.Secret)]
    public void SingleCharacterOcrWords_AreJoined(string text, DetectionCategory expected)
    {
        Assert.Contains(Detect(CharLine(text)), x => x.Category == expected);
    }

    [Fact]
    public void WordsSeparatedBySpace_AreNotSwallowedIntoEmail()
    {
        // 単語の間に空白(高さの20%超の隙間)があれば、前の単語は巻き込まない
        var line = Line("From Taro yoh@contoso.com");
        var hit = Assert.Single(Detect(line), x => x.Category == DetectionCategory.Email);
        Assert.Equal(line.Words[2].Box, hit.Box);
    }

    [Theory]
    [InlineData("@yoh さんへ")]            // メンション
    [InlineData("価格は@100円")]
    public void Email_NotAnAddress_IsNotDetected(string text)
    {
        Assert.DoesNotContain(Detect(Line(text)), x => x.Category == DetectionCategory.Email);
    }

    // v0.5.7: 入力途中のメールアドレスも隠す(完成するまで見えてしまうため)
    [Theory]
    [InlineData("yoh@")]
    [InlineData("taro@exa")]
    [InlineData("taro@example")]
    [InlineData("user@localhost")]
    public void Email_PartiallyTyped_IsDetected(string text)
    {
        Assert.Contains(Detect(Line(text)), x => x.Category == DetectionCategory.Email);
    }

    // v0.5.13: 小さい文字でOCRがドメインの途中で単語を分けた場合も、後ろまで隠す
    [Fact]
    public void Email_SplitInsideDomainByOcr_MasksBothParts()
    {
        var line = Line("Contact: test@examp le.com");
        var d = Detect(line).Where(x => x.Category == DetectionCategory.Email).ToList();
        Assert.NotEmpty(d);
        double right = d.Max(x => x.Box.Right);
        Assert.True(right >= line.Words[^1].Box.Right, "ドメインの後半(le.com)まで隠すこと");
        Assert.True(d.Min(x => x.Box.X) >= line.Words[1].Box.X, "前の単語(Contact:)は巻き込まないこと");
    }

    [Fact]
    public void Email_SplitIntoTwoOcrLinesOnSameRow_MasksBothParts()
    {
        var a = new OcrLineBox(new[] { new OcrWordBox("Contact:", new RectD(0, 0, 80, 20)), new OcrWordBox("test@examp", new RectD(90, 0, 100, 20)) });
        var b = new OcrLineBox(new[] { new OcrWordBox("le.com", new RectD(192, 1, 60, 20)) });
        var d = Detect(b, a).Where(x => x.Category == DetectionCategory.Email).ToList();
        Assert.NotEmpty(d);
        Assert.True(d.Max(x => x.Box.Right) >= 252, "隣の行に分かれた後半(le.com)まで隠すこと");
    }

    [Fact]
    public void Email_SplitAndRestMisread_MasksAdjacentWord()
    {
        // 後半を「|e.mmm」のように読み違えた場合も、すぐ隣の単語まで隠す
        var line = Line("Contact: test@examp |e.mmm");
        var d = Detect(line).Where(x => x.Category == DetectionCategory.Email).ToList();
        Assert.True(d.Max(x => x.Box.Right) >= line.Words[^1].Box.Right);
    }

    [Fact]
    public void Email_SplitIntoManyFragments_MasksAll()
    {
        // 「test@examp」「le」「.」「com」(「.」の矩形は小さい)
        var line = new OcrLineBox(new[]
        {
            new OcrWordBox("test@examp", new RectD(0, 0, 100, 20)),
            new OcrWordBox("le", new RectD(102, 0, 20, 20)),
            new OcrWordBox(".", new RectD(126, 16, 4, 4)),
            new OcrWordBox("c|m", new RectD(138, 6, 30, 14)), // 読み違え
        });
        var d = Detect(line).Where(x => x.Category == DetectionCategory.Email).ToList();
        Assert.True(d.Max(x => x.Box.Right) >= 168);
    }

    // v0.5.19: 小さい文字でのOCRの読み違えへの対策
    [Theory]
    [InlineData("db: Password-Dummy#Pass2026")]   // 「=」を「-」と読んだ
    [InlineData("key: sk-pro|-AbCdEf1234567890GhIjKlMn")] // 「j」を「|」と読んだ
    public void Secret_OcrMisreads_AreStillDetected(string text)
    {
        Assert.Contains(Detect(Line(text)), x => x.Category == DetectionCategory.Secret);
    }

    [Fact]
    public void Secret_PasswordWordWithoutDigits_IsNotDetected()
    {
        Assert.DoesNotContain(Detect(Line("This is password-protected content")), x => x.Category == DetectionCategory.Secret);
    }

    [Fact]
    public void Secret_ExtendsOverWordsTouchingWithoutSpace()
    {
        // 「sk-proj-…」の後ろの「#x」部分がOCRで別の単語になった
        var line = new OcrLineBox(new[]
        {
            new OcrWordBox("key:", new RectD(0, 0, 40, 20)),
            new OcrWordBox("sk-proj-AbCdEf1234567890GhIj", new RectD(50, 0, 280, 20)),
            new OcrWordBox("#x", new RectD(331, 0, 20, 20)),
        });
        var d = Detect(line).Where(x => x.Category == DetectionCategory.Secret).ToList();
        Assert.True(d.Max(x => x.Box.Right) >= 351);
        Assert.True(d.Min(x => x.Box.X) >= 50, "空白で離れた「key:」は巻き込まない");
    }

    [Fact]
    public void Secret_GenericToken_ExtendsOverKeyFragmentsAcrossGaps()
    {
        // OCRが「-」を読み落とし、「sk-proj」「本体」「StUv#x」の3つの単語に分かれた
        var line = new OcrLineBox(new[]
        {
            new OcrWordBox("key:", new RectD(0, 0, 40, 20)),
            new OcrWordBox("sk-proj", new RectD(52, 0, 70, 20)),
            new OcrWordBox("AbCdEf1234567890GhIjKlMnOpQr", new RectD(134, 0, 280, 20)),
            new OcrWordBox("StUv#x", new RectD(426, 0, 60, 20)),
            new OcrWordBox("is", new RectD(498, 0, 20, 20)),
        });
        var d = Detect(line).Where(x => x.Category == DetectionCategory.Secret).ToList();
        Assert.True(d.Min(x => x.Box.X) <= 52, "前の「sk-proj」も隠す");
        Assert.True(d.Max(x => x.Box.Right) >= 486, "後ろの「StUv#x」も隠す");
        Assert.True(d.Min(x => x.Box.X) > 40, "ラベルの「key:」は巻き込まない");
        Assert.True(d.Max(x => x.Box.Right) < 498, "ふつうの単語「is」は巻き込まない");
    }

    [Fact]
    public void Identifier_TenantHostSplitByOcr_MasksWholeHost()
    {
        var line = Line("host: contoso.sharepo int.com");
        var d = Detect(line).Where(x => x.Category == DetectionCategory.Identifier).ToList();
        Assert.NotEmpty(d);
        Assert.True(d.Max(x => x.Box.Right) >= line.Words[^1].Box.Right, "後半の「int.com」まで隠す");
    }

    [Fact]
    public void Email_LocalPartSplitByOcr_MasksTheFrontToo()
    {
        var line = new OcrLineBox(new[]
        {
            new OcrWordBox("line", new RectD(0, 0, 40, 20)),
            new OcrWordBox("05", new RectD(52, 0, 20, 20)),
            new OcrWordBox("mai", new RectD(84, 0, 30, 20)),
            new OcrWordBox("l5@example.com", new RectD(116, 0, 140, 20)),
        });
        var d = Detect(line).Where(x => x.Category == DetectionCategory.Email).ToList();
        Assert.True(d.Min(x => x.Box.X) <= 84, "前の「mai」も隠す");
        Assert.True(d.Min(x => x.Box.X) > 72, "空白で離れた「05」は巻き込まない");
    }

    [Theory]
    [InlineData("host: contoso.onmicrosott.com")]   // 小さい「f」を「t」と読んだ
    [InlineData("host: contoso.onmicros0ft.com")]
    [InlineData("host: contoso.sharepoirit.com")]   // 「n」を「ri」と読んだ
    public void Identifier_TenantHostWithOcrMisreads_IsDetected(string text)
    {
        Assert.Contains(Detect(Line(text)), x => x.Category == DetectionCategory.Identifier);
    }

    // v0.5.24: 前側が空白ほどの隙間で分かれた場合(案A)
    [Fact]
    public void Email_LocalPartSplitWithSpaceLikeGap_MasksShortFragment()
    {
        // 「mai」と「l5@example.com」の間に文字1つ弱の隙間(文字幅10px、隙間8px)
        var line = new OcrLineBox(new[]
        {
            new OcrWordBox("line", new RectD(0, 0, 40, 20)),
            new OcrWordBox("05", new RectD(60, 0, 20, 20)),
            new OcrWordBox("mai", new RectD(100, 0, 30, 20)),
            new OcrWordBox("l5@example.com", new RectD(138, 0, 140, 20)),
        });
        var d = Detect(line).Where(x => x.Category == DetectionCategory.Email).ToList();
        Assert.True(d.Min(x => x.Box.X) <= 100, "前の「mai」も隠す");
        Assert.True(d.Min(x => x.Box.X) > 80, "その前の「05」までは隠さない");
    }

    [Fact]
    public void Email_LabelBeforeAddress_IsNotMasked()
    {
        var line = new OcrLineBox(new[]
        {
            new OcrWordBox("Mail:", new RectD(0, 0, 50, 20)),
            new OcrWordBox("yoh@example.com", new RectD(58, 0, 150, 20)),
        });
        var d = Detect(line).Where(x => x.Category == DetectionCategory.Email).ToList();
        Assert.True(d.Min(x => x.Box.X) >= 58, "記号で終わるラベルは巻き込まない");
    }

    [Fact]
    public void Email_FarPrecedingWord_IsNotMasked()
    {
        var line = new OcrLineBox(new[]
        {
            new OcrWordBox("to", new RectD(0, 0, 20, 20)),
            new OcrWordBox("yoh@example.com", new RectD(40, 0, 150, 20)), // 文字幅10px、隙間20px(2文字分)
        });
        var d = Detect(line).Where(x => x.Category == DetectionCategory.Email).ToList();
        Assert.True(d.Min(x => x.Box.X) >= 40);
    }

    // v0.5.26: キーが細かく分かれ、隙間も大きく読まれた場合
    [Fact]
    public void Secret_KeySplitWithWideGaps_MasksPrefixAndTail()
    {
        var line = new OcrLineBox(new[]
        {
            new OcrWordBox("key:", new RectD(0, 0, 40, 20)),
            new OcrWordBox("sk-p", new RectD(52, 0, 40, 20)),
            new OcrWordBox("roj-AbCdEf12", new RectD(118, 0, 120, 20)),   // 隙間 26px(1.3文字の高さ)
            new OcrWordBox("34567890GhIjKlMnOpQr", new RectD(262, 0, 200, 20)),
            new OcrWordBox("-StUv#x", new RectD(510, 0, 70, 20)),         // 隙間 48px(2.4文字の高さ)
            new OcrWordBox("is", new RectD(600, 0, 20, 20)),
            new OcrWordBox("valid", new RectD(632, 0, 50, 20)),
        });
        var d = Detect(line).Where(x => x.Category == DetectionCategory.Secret).ToList();
        Assert.NotEmpty(d);
        Assert.True(d.Min(x => x.Box.X) <= 52, "書き出しの「sk-p」も隠す");
        Assert.True(d.Max(x => x.Box.Right) >= 580, "末尾の「-StUv#x」も隠す");
        Assert.True(d.Min(x => x.Box.X) > 40, "ラベルの「key:」は巻き込まない");
        Assert.True(d.Max(x => x.Box.Right) < 600, "後ろのふつうの単語は巻き込まない");
    }

    [Theory]
    [InlineData("desk-top layout")]
    [InlineData("task-1 done")]
    public void SecretPrefix_InsideOrdinaryWords_IsNotDetected(string text)
    {
        Assert.DoesNotContain(Detect(Line(text)), x => x.Category == DetectionCategory.Secret);
    }

    [Fact]
    public void Lines_OnDifferentRows_AreNotMerged()
    {
        var a = new OcrLineBox(new[] { new OcrWordBox("test@examp", new RectD(0, 0, 100, 20)) });
        var b = new OcrLineBox(new[] { new OcrWordBox("le.com", new RectD(102, 40, 60, 20)) });
        var d = Detect(a, b).Where(x => x.Category == DetectionCategory.Email).ToList();
        Assert.True(d.Max(x => x.Box.Bottom) <= 20.5, "下の行は巻き込まないこと");
    }

    [Fact]
    public void Email_FollowedByOrdinaryWord_DoesNotSwallowIt()
    {
        var line = Line("taro@example.com is mine");
        var d = Detect(line).Where(x => x.Category == DetectionCategory.Email).ToList();
        Assert.True(d.Max(x => x.Box.Right) <= line.Words[0].Box.Right, "後ろの普通の単語は巻き込まないこと");
    }

    [Fact]
    public void Email_TwoAddressesInOneLine_AreBothDetected()
    {
        var d = Detect(Line("CC: a@example.com b@example.com"));
        Assert.Equal(2, d.Count(x => x.Category == DetectionCategory.Email));
    }

    [Fact]
    public void Email_SubdomainAndPlus_IsDetected()
    {
        Assert.Contains(Detect(Line("first.last+tag@mail.sub.example.co.jp")), x => x.Category == DetectionCategory.Email);
    }

    // ---------- 識別子 ----------
    [Fact]
    public void Guid_InsideUrl_IsDetected()
    {
        var url = "https://make.powerapps.com/environments/Default-72f988bf-86f1-41af-91ab-2d7cd011db47/apps";
        Assert.Contains(Detect(Line(url)), x => x.Category == DetectionCategory.Identifier);
    }

    [Theory]
    [InlineData("72f988bf-86f1-41af-91ab")]              // 途中で切れたGUID
    [InlineData("72f988bf86f141af91ab2d7cd011db4")]      // 31桁
    [InlineData("72f988bf86f141af91ab2d7cd011db47ab")]   // 34桁(32桁ちょうどではない)
    public void Guid_Malformed_IsNotDetectedAsIdentifier(string text)
    {
        var options = new DetectorOptions { DetectGenericSecret = false };
        Assert.DoesNotContain(Detect(options, Line(text)), x => x.Category == DetectionCategory.Identifier);
    }

    // ---------- 誤検出しないこと ----------
    [Theory]
    [InlineData("Version 1.2.3.4567")]
    [InlineData("TEL 03-1234-5678")]
    [InlineData("2026/11/17 19:30")]
    [InlineData("https://learn.microsoft.com/ja-jp/power-automate/")]
    [InlineData("Power Platform 管理センター")]
    [InlineData("C:\\Users\\Public\\Documents")]
    public void OrdinaryText_HasNoDetection(string text)
    {
        Assert.Empty(Detect(Line(text)));
    }

    // ---------- 汎用トークンの境界 ----------
    [Fact]
    public void GenericToken_LengthBoundary()
    {
        Assert.False(SensitiveTextDetector.LooksLikeRandomToken("Xk9pQ2mZ7vB4nR8tL1wY6cH3jF5"));  // 27文字
        Assert.True(SensitiveTextDetector.LooksLikeRandomToken("Xk9pQ2mZ7vB4nR8tL1wY6cH3jF5s")); // 28文字
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Aa1Aa1Aa1Aa1Aa1Aa1Aa1Aa1Aa1Aa1")] // 長いが文字の種類が少ない(繰り返し)
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")] // 大文字なし
    public void GenericToken_NotRandom(string? word)
    {
        Assert.False(SensitiveTextDetector.LooksLikeRandomToken(word));
    }

    [Fact]
    public void GenericToken_IgnoredWhenSecretCategoryDisabled()
    {
        var options = new DetectorOptions { DetectSecret = false, DetectGenericSecret = true };
        Assert.Empty(Detect(options, Line("Xk9pQ2mZ7vB4nR8tL1wY6cH3jF5sD0gA")));
    }

    // ---------- 秘密情報(追加の形式) ----------
    [Theory]
    [InlineData("github" + "_pat_11ABCDEFG0123456789_abcdefghijklmnop")]
    [InlineData("xo" + "xb-1234567890-abcdefghij")]
    [InlineData("SharedAccessKey=abc123DEF456ghi789")]
    [InlineData("client_secret: s3cr3tValue!!")]
    [InlineData("Ocp-Apim-Subscription-Key: 0123456789abcdef")]
    public void Secret_MoreFormats_AreDetected(string text)
    {
        Assert.Contains(Detect(Line(text)), x => x.Category == DetectionCategory.Secret);
    }

    // ---------- NGワード ----------
    [Fact]
    public void NgWord_RepeatedInLine_AllOccurrencesDetected()
    {
        var options = new DetectorOptions { NgWords = new[] { "朝日" }, FuzzyNgWords = false };
        var d = Detect(options, CharLine("朝日と朝日"));
        Assert.Equal(2, d.Count(x => x.Category == DetectionCategory.NgWord));
    }

    [Fact]
    public void NgWord_MultipleWords_AreAllDetected()
    {
        var options = new DetectorOptions { NgWords = new[] { "山田太郎", "Contoso" } };
        var d = Detect(options, Line("担当 山田太郎 / CONTOSO 様"));
        Assert.Equal(2, d.Count(x => x.Category == DetectionCategory.NgWord));
    }

    [Fact]
    public void NgWord_HalfWidthKatakana_MatchesFullWidth()
    {
        var options = new DetectorOptions { NgWords = new[] { "アサヒ" } };
        Assert.Contains(Detect(options, Line("ｱｻﾋ 御中")), x => x.Category == DetectionCategory.NgWord);
    }

    // v0.5.23: 8文字以上の語は2文字違いまで隠す(隠しすぎる側。読み違いが重なって語の一部が見えるのを防ぐ)
    [Fact]
    public void NgWord_Fuzzy_LongWord_ToleratesTwoDifferences()
    {
        var options = new DetectorOptions { NgWords = new[] { "朝日商事株式会社" } };
        var line = CharLine("朝目啇事株式会社");
        var d = Detect(options, line).Where(x => x.Category == DetectionCategory.NgWord).ToList();
        Assert.NotEmpty(d);
        Assert.True(d.Max(x => x.Box.Right) >= line.Words[^1].Box.Right, "「株式会社」まで隠す");
    }

    [Fact]
    public void NgWord_Fuzzy_LongWord_DoesNotMatchThreeDifferences()
    {
        var options = new DetectorOptions { NgWords = new[] { "朝日商事株式会社" } };
        Assert.Empty(Detect(options, CharLine("朝目啇亊株式会社"))); // 3文字違い
    }

    [Fact]
    public void NgWord_Fuzzy_ShortWord_DoesNotMatchTwoDifferences()
    {
        var options = new DetectorOptions { NgWords = new[] { "朝日商事" } };
        Assert.Empty(Detect(options, CharLine("朝目啇事"))); // 4文字の語で2文字違い
    }

    [Fact]
    public void NgWord_Fuzzy_ToleratesOneMissingCharacter()
    {
        var options = new DetectorOptions { NgWords = new[] { "朝日商事株式会社" } };
        Assert.Contains(Detect(options, CharLine("朝日商事株式社")), x => x.Category == DetectionCategory.NgWord); // 「会」の読み落とし
    }

    [Fact]
    public void NgWord_BlankAndDuplicateEntries_AreIgnored()
    {
        var options = new DetectorOptions { NgWords = new[] { "", "   ", "朝日", "朝日" }, FuzzyNgWords = false };
        var d = Detect(options, CharLine("朝日"));
        Assert.Single(d, x => x.Category == DetectionCategory.NgWord);
    }

    [Fact]
    public void NgWord_SplitAcrossLines_IsNotDetected_KnownLimitation()
    {
        // 既知の制限: 改行をまたぐNGワードは検出できない(READMEに記載)
        var options = new DetectorOptions { NgWords = new[] { "朝日商事" }, FuzzyNgWords = false };
        Assert.Empty(Detect(options, CharLine("朝日", 0), CharLine("商事", 30)));
    }

    // ---------- 入力の異常 ----------
    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(Detect());
        Assert.Empty(Detect(new Core.Detection.OcrLineBox(System.Array.Empty<OcrWordBox>())));
        Assert.Empty(Detect(Line("   ")));
    }
}
