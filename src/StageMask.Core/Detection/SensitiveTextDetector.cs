using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using StageMask.Core.Geometry;

namespace StageMask.Core.Detection;

/// <summary>
/// OCR結果(行・単語・座標)から、メールアドレス・識別子・秘密情報・NGワードを検出する。
///
/// 方針: 「見せてはいけないものを見せない」ことを優先し、取りこぼしより過剰検出を許容する。
/// - 行内の単語を空白なしで連結してから照合する(OCRが記号の前後で単語を分割することがあるため)。
/// - 一致した文字が1文字でも含まれる単語は、単語の矩形全体をマスク対象にする。
/// </summary>
public sealed class SensitiveTextDetector
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>
    /// 1回の照合の上限時間。想定外の文字列で照合が長引くと検出全体が止まるため、
    /// 上限を超えたら「その行全体を機密情報とみなす」(フェイルクローズ)。
    /// ※ 下の正規表現より前に宣言すること(静的フィールドは記述順に初期化される)
    /// </summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    // ---- メールアドレス / UPN ----
    private static readonly Regex EmailRx = new(
        @"[a-z0-9._%+\-]+@[a-z0-9\-]+(?:\.[a-z0-9\-]+)*\.[a-z]{2,}", Opt, RegexTimeout);

    // ---- 識別子(GUID・テナント固有のホスト名など) ----
    /// <summary>
    /// 入力途中のメールアドレス(「yoh@」「taro@exa」など)。完成するまで「メールアドレス」と判定されず、
    /// 入力中に見えてしまうため、「@ の前に文字があるもの」はドメインが未完成でも隠す。
    /// 「@メンション」(前に文字がない)は対象外。
    /// 小さい文字ではOCRが「test@examp le.com」のようにドメインの途中で単語を分けることがあるため、
    /// 直後の単語がドメインの続き(「.com」などで終わる)に見える場合はそこまで含める。
    /// </summary>
    private static readonly Regex PartialEmailRx = new(
        @"[a-z0-9._%+\-]+@[a-z0-9.\-]*(?: [a-z0-9\-]*(?:\.[a-z0-9\-]+)*\.[a-z]{2,}\b)?", Opt, RegexTimeout);

    private static readonly Regex[] IdentifierRx =
    {
        // GUID。OCRが 0/o・1/l を取り違えることがあるため英数字を広く許容する。
        new(@"[0-9a-z]{8}-[0-9a-z]{4}-[0-9a-z]{4}-[0-9a-z]{4}-[0-9a-z]{12}", Opt, RegexTimeout),
        // ハイフンなしの32桁16進数(GUIDのN形式など)
        new(@"(?<![0-9a-f])[0-9a-f]{32}(?![0-9a-f])", Opt, RegexTimeout),
        // Microsoft 365 / Power Platform / Azure のテナント固有ホスト名
        // 小さい文字ではOCRが「sharepo int.com」のように単語の途中で分けることがあるため、
        // ドメイン名の文字の間に区切り(空白)が1つずつ入っていても一致させる
        new(@"[a-z0-9\-]+\." + Spaced("onmicrosoft.com"), Opt, RegexTimeout),
        new(@"[a-z0-9\-]+\." + Spaced("sharepoint.com"), Opt, RegexTimeout),
        new(@"[a-z0-9\-]+\.crm\d*\.dynamics\.com", Opt, RegexTimeout),
        new(@"[a-z0-9\-]+\.(?:blob|queue|table|file|dfs)\.core\.windows\.net", Opt, RegexTimeout),
        new(@"[a-z0-9\-]+\.vault\.azure\.net", Opt, RegexTimeout),
        new(@"[a-z0-9\-]+\.openai\.azure\.com", Opt, RegexTimeout),
        new(@"[a-z0-9\-]+\.azurewebsites\.net", Opt, RegexTimeout),
    };

    /// <summary>
    /// 固定の文字列を、OCRの読み違えに強い正規表現にする。
    /// 文字の間に空白が1つ入っていても一致させ、小さい文字で取り違えやすい文字(f と t、o と 0、
    /// l・i・1・| など)はどちらでも一致させる(例: 小さい「onmicrosoft」は「onmicrosott」と読まれることがある)。
    /// </summary>
    private static string Spaced(string literal)
        => string.Join(" ?", literal.Select(c => c switch
        {
            'f' or 't' => "[ft]",
            'o' => "[o0]",
            'l' or 'i' => "[li1|!]",
            'n' => "(?:n|ri)",
            'm' => "(?:m|rn)",
            _ => Regex.Escape(c.ToString()),
        }));

    // ---- 秘密情報(既知の形式) ----
    private static readonly Regex[] SecretRx =
    {
        // OpenAI / Anthropic 形式のキー。小さい文字では「proj」の「j」を「|」「]」などと読み違えることがあるため1文字は許容する
        new(@"sk-(?:pro.-?|ant-[a-z0-9]+-)?[a-z0-9_\-]{20,}", Opt, RegexTimeout),
        new(@"gh[pousr]_[a-z0-9]{30,}", Opt, RegexTimeout),                               // GitHub トークン
        new(@"github_pat_[a-z0-9_]{30,}", Opt, RegexTimeout),                             // GitHub fine-grained PAT
        new(@"akia[0-9a-z]{16}", Opt, RegexTimeout),                                      // AWS アクセスキーID
        new(@"aiza[0-9a-z_\-]{30,}", Opt, RegexTimeout),                                  // Google API キー
        new(@"xox[abprs]-[a-z0-9\-]{10,}", Opt, RegexTimeout),                            // Slack トークン
        new(@"ey[a-z0-9_\-]{8,}\.ey[a-z0-9_\-]{8,}\.[a-z0-9_\-]{8,}", Opt, RegexTimeout), // JWT
        new(@"bearer ?[a-z0-9\-._~+/]{20,}=*", Opt, RegexTimeout),                         // Authorization: Bearer ...
        new(@"-----begin[a-z ]*private ?key-----", Opt, RegexTimeout),                    // PEM 秘密鍵
        // キー=値 形式(接続文字列・設定ファイル)。値の部分まで含めてマスクする。
        new(@"(?:accountkey|sharedaccesskey|sharedaccesssignature|clientsecret|client_secret|client-secret|" +
            @"apikey|api_key|api-key|subscription-key|password|passwd|pwd|secret|token|sig)" +
            // 区切りは「=」「:」。小さい文字ではOCRが「=」を「-」と読むことがあるため、値に数字を含む場合に限り「-」も許容する
            @"\s?(?:[=:""']+|-(?=[^;,&""'\s]*\d))\s?[^;,&""'\s]{6,}", Opt, RegexTimeout),
        // Entra ID のクライアントシークレットでよく見られる形式(xxx8Q~...)。
        // ※公式に形式が規定されたものではなく、観察に基づくパターン。
        new(@"[a-z0-9_\-.]{3}\dq~[a-z0-9_.\-~]{31,}", Opt, RegexTimeout),
    };

    /// <summary>
    /// キーの書き出し(「sk-」「ghp_」など)だけでも秘密情報とみなす。
    /// 小さい文字ではOCRがキーを途中で分けたり読み違えたりして本体が形式に合わなくなることがあるため、
    /// 書き出しを見つけたら、そこから続く断片(空白をはさんでいても)をまとめて隠す。
    /// </summary>
    private static readonly Regex SecretPrefixRx = new(
        @"\b(?:sk-[a-z0-9]|gh[pousr]_|github_pat_|xox[abprs]-|akia[0-9a-z]{4}|aiza[0-9a-z_\-]{4})", Opt, RegexTimeout);

    // 汎用の「長くてランダムな文字列」(単語単位で判定する)
    private static readonly Regex GenericTokenRx = new(@"^[A-Za-z0-9+/_\-~=.]{28,}$", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);

    private readonly DetectorOptions _options;
    private readonly List<string> _ngWords;

    public SensitiveTextDetector(DetectorOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ngWords = options.NgWords
            .Select(TextNormalizer.Normalize)
            .Where(w => w.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<DetectionResult> Detect(IEnumerable<OcrLineBox> lines)
    {
        var results = new List<DetectionResult>();
        foreach (var line in MergeSameRowLines(lines))
        {
            if (line.Words.Count == 0) continue;
            DetectInLine(line, results);
        }
        return Deduplicate(results);
    }

    /// <summary>
    /// OCRが1行を途中で2つの「行」に分けて返すことがある(小さい文字の「test@examp」と「le.com」など)。
    /// 同じ高さに並び、すぐ右隣から始まる行はつなげて1行として扱う(行をまたいだメールアドレスも見つけるため)。
    /// </summary>
    internal static List<OcrLineBox> MergeSameRowLines(IEnumerable<OcrLineBox> lines)
    {
        var list = lines.Where(l => l.Words.Count > 0).ToList();
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < list.Count && !merged; i++)
            {
                var a = list[i].Words;
                var last = a[^1].Box;
                for (int j = 0; j < list.Count; j++)
                {
                    if (i == j) continue;
                    var first = list[j].Words[0].Box;
                    double h = Math.Max(1, Math.Max(last.Height, first.Height));
                    double gap = first.X - last.Right;
                    double overlap = Math.Min(last.Bottom, first.Bottom) - Math.Max(last.Y, first.Y);
                    if (gap < -h * 0.3 || gap > h * 1.0) continue;
                    if (overlap < Math.Min(last.Height, first.Height) * 0.5) continue;
                    var words = new List<OcrWordBox>(a.Count + list[j].Words.Count);
                    words.AddRange(a);
                    words.AddRange(list[j].Words);
                    list[i] = new OcrLineBox(words);
                    list.RemoveAt(j);
                    merged = true;
                    break;
                }
            }
        }
        return list;
    }

    private void DetectInLine(OcrLineBox line, List<DetectionResult> results)
    {
        // 正規化した行文字列と、各文字がどの単語に由来するかの対応表を作る。
        // - ngText: 単語を空白なしで連結(日本語OCRは1文字ずつ単語になるため)
        // - rxText: 英数字どうしが隣り合い、かつ単語の間に実際の空白がある場合だけ区切り(空白)を入れる。
        //           「yoh @ contoso . com」のように記号で分割された単語や、
        //           日本語OCRが英数字を1文字ずつ単語に分けた「y o h @ …」は連結し、
        //           「Hello world yoh@contoso.com」の Hello/world は巻き込まないようにする。
        var ngText = new StringBuilder();
        var ngMap = new List<int>();
        var rxText = new StringBuilder();
        var rxMap = new List<int>();
        RectD? prevBox = null;
        var words = new List<(int Index, string Text, RectD Box)>(line.Words.Count);
        for (int wi = 0; wi < line.Words.Count; wi++)
        {
            var normalized = new StringBuilder();
            var text = line.Words[wi].Text ?? string.Empty;
            var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext()) normalized.Append(TextNormalizer.NormalizeChar(e.GetTextElement()));
            if (normalized.Length == 0) continue;
            words.Add((wi, normalized.ToString(), line.Words[wi].Box));

            var box = line.Words[wi].Box;
            if (rxText.Length > 0 && IsAsciiAlnum(rxText[^1]) && IsAsciiAlnum(normalized[0]) &&
                prevBox is RectD pb && HasSpaceBetween(pb, box))
            {
                rxText.Append(' ');
                rxMap.Add(-1);
            }
            prevBox = box;
            foreach (var c in normalized.ToString())
            {
                ngText.Append(c);
                ngMap.Add(wi);
                rxText.Append(TextNormalizer.FoldDash(c));
                rxMap.Add(wi);
            }
        }
        if (ngMap.Count == 0) return;
        var rx = rxText.ToString();
        var ng = ngText.ToString();

        if (_options.DetectEmail)
            AddRegexMatches(EmailRx, rx, rxMap, line, DetectionCategory.Email, results, extendToAdjacent: true, keyFragments: false);
        if (_options.DetectEmail)
            AddPartialEmailMatches(rx, rxMap, line, results);

        if (_options.DetectIdentifier)
            foreach (var r in IdentifierRx)
                AddRegexMatches(r, rx, rxMap, line, DetectionCategory.Identifier, results, extendToAdjacent: true);

        if (_options.DetectSecret)
        {
            foreach (var r in SecretRx)
                AddRegexMatches(r, rx, rxMap, line, DetectionCategory.Secret, results, extendToAdjacent: true, secret: true);
            AddRegexMatches(SecretPrefixRx, rx, rxMap, line, DetectionCategory.Secret, results, extendToAdjacent: true, secret: true);
        }

        // 等幅フォントでは「I」「l」「i」のような細い文字の前後が広く空き、OCR がそこで単語を分けることがある
        // (例: 「AK I AABCDEFGH I JKLMNOP」「ghp_AbCd I jK l Mn…」)。キー・テナント名はそれで取りこぼすため、
        // 1〜2文字の英数字の断片は前後とつないだ文字列でも調べる(見つかった分を足すだけなので、隠す範囲が広がる方向にだけ働く)
        if (_options.DetectSecret || _options.DetectIdentifier)
        {
            var (tight, tightMap) = BuildTightText(words);
            if (tight != rx)
            {
                if (_options.DetectIdentifier)
                    foreach (var r in IdentifierRx)
                        AddRegexMatches(r, tight, tightMap, line, DetectionCategory.Identifier, results, extendToAdjacent: true);
                if (_options.DetectSecret)
                {
                    foreach (var r in SecretRx)
                        AddRegexMatches(r, tight, tightMap, line, DetectionCategory.Secret, results, extendToAdjacent: true, secret: true);
                    AddRegexMatches(SecretPrefixRx, tight, tightMap, line, DetectionCategory.Secret, results, extendToAdjacent: true, secret: true);
                }
            }
        }

        if (_options.DetectSecret && _options.DetectGenericSecret)
            for (int wi = 0; wi < line.Words.Count; wi++)
            {
                var w = line.Words[wi];
                if (LooksLikeRandomToken(w.Text))
                    results.Add(new DetectionResult(DetectionCategory.Secret, w.Text, ExtendWordRange(w.Box, wi, wi, line, secret: true)));
            }

        if (_options.DetectNgWords && _ngWords.Count > 0)
            AddNgWordMatches(ng, ngMap, line, results);
    }

    private static bool IsAsciiAlnum(char c) => c < 128 && char.IsLetterOrDigit(c);

    /// <summary>
    /// 1〜2文字の英数字の断片を、前後の単語と空白なしでつないだ行文字列を作る。
    /// 断片の前後でも、文字1つ分より広く空いていれば本当の空白とみなして区切る。
    /// </summary>
    private static (string Text, List<int> Map) BuildTightText(List<(int Index, string Text, RectD Box)> words)
    {
        // 1文字あたりの幅(3文字以上の英数字の単語から求める)
        double widthSum = 0; int charSum = 0; double heightMax = 1;
        foreach (var (_, t, b) in words)
        {
            heightMax = Math.Max(heightMax, b.Height);
            if (t.Length >= 3 && t.All(c => c < 128)) { widthSum += b.Width; charSum += t.Length; }
        }
        double charWidth = charSum > 0 ? widthSum / charSum : heightMax * 0.5;

        var text = new StringBuilder();
        var map = new List<int>();
        for (int k = 0; k < words.Count; k++)
        {
            var (wi, t, box) = words[k];
            if (k > 0 && text.Length > 0 && IsAsciiAlnum(text[^1]) && IsAsciiAlnum(t[0]))
            {
                var (_, pt, pbox) = words[k - 1];
                bool fragment = IsShortAsciiFragment(pt) || IsShortAsciiFragment(t);
                double gap = box.X - pbox.Right;
                // 細い文字の前後のすき間(文字幅の半分程度まで)は、断片でなくてもつなぐ
                bool space = HasSpaceBetween(pbox, box) && gap > charWidth * 0.5 && (!fragment || gap > charWidth);
                if (space) { text.Append(' '); map.Add(-1); }
            }
            foreach (var c in t) { text.Append(TextNormalizer.FoldDash(c)); map.Add(wi); }
        }
        return (text.ToString(), map);
    }

    private static bool IsShortAsciiFragment(string t) => t.Length is >= 1 and <= 2 && t.All(IsAsciiAlnum);

    /// <summary>
    /// 2つの単語の間に空白(スペース)があるとみなせるか。
    /// 文字間隔より広い隙間(文字の高さの20%超)を空白とみなす。判定が微妙な場合は連結する側
    /// (=周りの単語も含めて隠す、安全側)に倒れる。
    /// </summary>
    private static bool HasSpaceBetween(RectD prev, RectD next)
    {
        double gap = next.X - prev.Right;
        double height = Math.Max(1, Math.Max(prev.Height, next.Height));
        return gap > height * 0.2;
    }

    private static void AddRegexMatches(Regex regex, string text, List<int> map, OcrLineBox line,
                                        DetectionCategory category, List<DetectionResult> results,
                                        bool extendToAdjacent = false, bool keyFragments = true, bool secret = false)
    {
        try
        {
            foreach (Match m in regex.Matches(text))
            {
                if (m.Length == 0) continue;
                var box = BoxFor(m.Index, m.Length, map, line);
                if (extendToAdjacent) box = ExtendToAdjacentWords(box, m.Index, m.Length, map, line, keyFragments, secret);
                results.Add(new DetectionResult(category, m.Value, box));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // 照合が終わらない行は、行全体を隠す
            results.Add(new DetectionResult(category, "(timeout)", BoxFor(0, map.Count, map, line)));
        }
    }

    /// <summary>
    /// 入力途中・読み取り途中のメールアドレス。ドメインが「.com」などで終わっていない場合は、
    /// OCRが単語を途中で分けて残りを読み違えた(「l」を「|」と読むなど)可能性があるため、
    /// すぐ右隣(文字1つ分以内)にある単語を4つまで含めて隠す(「le」「.」「com」のように細かく分かれることがある)。
    /// </summary>
    private static void AddPartialEmailMatches(string text, List<int> map, OcrLineBox line, List<DetectionResult> results)
    {
        try
        {
            foreach (Match m in PartialEmailRx.Matches(text))
            {
                if (m.Length == 0) continue;
                // 前側(ローカル部)がOCRで別の単語に分かれた場合(「mai」「l5@example.com」)も含める
                var box = ExtendToAdjacentWords(BoxFor(m.Index, m.Length, map, line), m.Index, m.Length, map, line, keyFragments: false);
                bool complete = EmailRx.Match(m.Value) is { Success: true } full && full.Index + full.Length == m.Value.Length;
                if (!complete)
                {
                    int lastWord = -1;
                    for (int i = m.Index + m.Length - 1; i >= m.Index && lastWord < 0; i--) lastWord = map[i];
                    // 「.」だけの単語は高さがとても小さいので、行の中で一番高い単語の高さを基準にする
                    double h = Math.Max(1, line.Words.Max(w => w.Box.Height));
                    for (int k = 1; k <= 4 && lastWord >= 0 && lastWord + 1 < line.Words.Count; k++)
                    {
                        var prev = line.Words[lastWord].Box;
                        var next = line.Words[lastWord + 1].Box;
                        if (next.X - prev.Right > h * 0.8) break;
                        box = box.Union(next);
                        lastWord++;
                    }
                }
                results.Add(new DetectionResult(DetectionCategory.Email, m.Value, box));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            results.Add(new DetectionResult(DetectionCategory.Email, "(timeout)", BoxFor(0, map.Count, map, line)));
        }
    }

    private void AddNgWordMatches(string text, List<int> map, OcrLineBox line, List<DetectionResult> results)
    {
        foreach (var word in _ngWords)
        {
            // 完全一致
            var taken = new List<(int Start, int End)>();
            int idx = 0;
            while ((idx = text.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
            {
                results.Add(new DetectionResult(DetectionCategory.NgWord, word, BoxFor(idx, word.Length, map, line)));
                taken.Add((idx, idx + word.Length));
                idx += 1;
            }

            // あいまい一致: OCRの読み違えを許容する。8文字以上の語は2文字まで、それ未満は1文字まで。
            // 置き換え(啇/商)のほか、1文字の読み落とし・余分な1文字にも対応する(編集距離)。
            // 長い語で2文字まで許すのは、読み違いが重なって語の一部(「株式会社」など)が見えてしまうのを防ぐため。
            // 似た別の語(同じ「株式会社」で終わる別の社名など)まで隠すことがあるが、隠しすぎる側に倒す。
            if (_options.FuzzyNgWords && word.Length >= _options.FuzzyMinLength)
            {
                int allowed = word.Length >= 8 ? 2 : 1;
                var candidates = new List<(int Start, int Len, int Dist)>();
                for (int start = 0; start < text.Length; start++)
                    for (int len = word.Length - 1; len <= word.Length + 1; len++)
                    {
                        if (len <= 0 || start + len > text.Length) continue;
                        int d = BoundedEditDistance(text, start, len, word, allowed);
                        if (d >= 1 && d <= allowed) candidates.Add((start, len, d));
                    }
                // 近いもの(距離が小さく、語の長さに近いもの)から順に、重ならないものだけ採る
                foreach (var c in candidates.OrderBy(c => c.Dist).ThenBy(c => Math.Abs(c.Len - word.Length)).ThenBy(c => c.Start))
                {
                    int end = c.Start + c.Len;
                    if (taken.Any(t => c.Start < t.End && t.Start < end)) continue;
                    taken.Add((c.Start, end));
                    results.Add(new DetectionResult(DetectionCategory.NgWord, word, BoxFor(c.Start, c.Len, map, line)));
                }
            }
        }
    }

    /// <summary>
    /// text[start..start+len) と word の編集距離(置き換え・挿入・削除の回数)。max を超えたら max+1 を返す。
    /// </summary>
    private static int BoundedEditDistance(string text, int start, int len, string word, int max)
    {
        if (Math.Abs(len - word.Length) > max) return max + 1;
        var prev = new int[word.Length + 1];
        var cur = new int[word.Length + 1];
        for (int j = 0; j <= word.Length; j++) prev[j] = j;
        for (int i = 1; i <= len; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= word.Length; j++)
            {
                int cost = text[start + i - 1] == word[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin > max) return max + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[word.Length];
    }

    /// <summary>
    /// 1つのOCR単語が「ランダムな長い文字列(キー・トークン)」に見えるかを判定する。
    /// 大文字・小文字・数字が混在する、または32文字以上の16進数のものを対象にする。
    /// </summary>
    public static bool LooksLikeRandomToken(string? word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        var w = word.Normalize(NormalizationForm.FormKC);
        try
        {
            if (!GenericTokenRx.IsMatch(w)) return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return true; // 判定できない単語は隠す側に倒す
        }

        int upper = 0, lower = 0, digit = 0;
        bool allHex = true;
        var distinct = new HashSet<char>();
        foreach (var c in w)
        {
            if (char.IsUpper(c)) upper++;
            else if (char.IsLower(c)) lower++;
            else if (char.IsDigit(c)) digit++;
            if (!Uri.IsHexDigit(c)) allHex = false;
            distinct.Add(c);
        }
        if (distinct.Count < 10) return false;
        if (allHex && w.Length >= 32) return true;
        return upper >= 2 && lower >= 2 && digit >= 2;
    }

    /// <summary>
    /// キーやパスワードは、OCRが記号を読み違えると(「#」「|」など)一致する範囲が途中で切れ、残りが見えてしまう。
    /// 一致した単語の左右に、空白をはさまずくっついている単語(とキーの断片に見える単語)を4つまで含める。
    /// </summary>
    private static RectD ExtendToAdjacentWords(RectD box, int start, int length, List<int> map, OcrLineBox line, bool keyFragments = true, bool secret = false)
    {
        int first = -1, last = -1;
        for (int i = start; i < start + length && i < map.Count; i++)
        {
            if (map[i] < 0) continue;
            if (first < 0) first = map[i];
            last = map[i];
        }
        if (first < 0) return box;
        return ExtendWordRange(box, first, last, line, keyFragments, secret);
    }

    /// <param name="keyFragments">
    /// true: 空白をはさんでいても、キーの一部に見える断片(数字・記号入りなど)は文字の高さ1.2倍までの隙間なら含める。
    /// false: 空白をはさまずくっついている単語だけ含める(メールアドレス用)。
    /// </param>
    /// <param name="secret">
    /// true(キー・パスワード): 後ろ側は、ふつうの単語(英小文字だけ・先頭だけ大文字)が出てくるまで、
    /// 文字の高さ3倍までの隙間なら断片を6つまで含める(OCRがキーを細かく分けて隙間を大きく読んでも、残りが見えないように)。
    /// </param>
    private static RectD ExtendWordRange(RectD box, int first, int last, OcrLineBox line, bool keyFragments = true, bool secret = false)
    {
        double h = Math.Max(1, line.Words.Max(w => w.Box.Height));
        if (secret)
        {
            for (int k = 0; k < 6 && last + 1 < line.Words.Count; k++)
            {
                var a = line.Words[last].Box; var b = line.Words[last + 1];
                double gap = b.Box.X - a.Right;
                bool touching = gap <= h * 0.2;
                if (!IsAsciiWord(b.Text) || gap > h * 3.0 || (!touching && LooksPlainWord(b.Text))) break;
                box = box.Union(b.Box); last++;
            }
        }
        // 「空白なし」の基準は HasSpaceBetween と同じ(文字の高さの20%以下の隙間)。メールアドレスはより厳しくする
        double touch = keyFragments ? 0.2 : 0.12;
        // 日本語OCRは1文字ずつ単語に分けるため、日本語の文字(「メールは」「です」)は含めない
        bool Joinable(double gap, string? word) =>
            IsAsciiWord(word) &&
            (gap <= h * touch || (keyFragments && gap <= h * 1.2 && LooksKeyFragment(word)));
        for (int k = 0; k < 4 && last + 1 < line.Words.Count; k++)
        {
            var a = line.Words[last].Box; var b = line.Words[last + 1];
            if (!Joinable(b.Box.X - a.Right, b.Text)) break;
            box = box.Union(b.Box); last++;
        }
        for (int k = 0; k < 4 && first - 1 >= 0; k++)
        {
            var a = line.Words[first - 1]; var b = line.Words[first].Box;
            if (!Joinable(b.X - a.Box.Right, a.Text)) break;
            box = box.Union(a.Box); first--;
        }
        // メールアドレスは、前側(ローカル部)が OCR で空白ほどの隙間をあけて別の単語に分かれることがある
        // (小さい文字の「mail5@…」が「mai」「l5@…」になる)。直前の単語が短い英数字の断片なら含める
        if (!keyFragments && first - 1 >= 0 && LooksLocalPartFragment(line.Words[first - 1], line.Words[first]))
            box = box.Union(line.Words[first - 1].Box);
        return box;
    }

    /// <summary>
    /// メールアドレスの直前の単語が、ローカル部の切れ端に見えるか。
    /// 小文字・数字だけの4文字以下で、文字1つ分ほどの隙間で並んでいるもの。
    /// 「Contact:」「宛先:」のようなラベルは含めない。「to」「cc」などの短い単語まで隠すことがあるが、隠しすぎる側に倒す。
    /// </summary>
    private static bool LooksLocalPartFragment(OcrWordBox prev, OcrWordBox addressWord)
    {
        var t = prev.Text;
        if (string.IsNullOrEmpty(t) || t.Length > 4) return false;
        // 小文字と数字だけ(「Taro」「CC」のような大文字を含む名前・略語は、ふつうの単語とみなして含めない)
        foreach (var c in t) if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9')) return false;
        var text = addressWord.Text ?? "";
        if (text.Length == 0) return false;
        double charWidth = addressWord.Box.Width / text.Length;
        double gap = addressWord.Box.X - prev.Box.Right;
        return gap >= -charWidth && gap <= charWidth * 1.0;
    }

    /// <summary>
    /// キーの一部に見える断片か(OCRが「-」を読み落として「sk-proj」と残りを別の単語にした場合など)。
    /// 数字を含む・大文字と小文字が混在する・「-」「_」「#」などの記号を含むもの。
    /// 「key:」「Password=」のようなラベル(末尾が「:」「=」)や、ふつうの英単語は含めない。
    /// </summary>
    private static bool LooksKeyFragment(string? word)
    {
        if (string.IsNullOrEmpty(word) || word.Length < 2) return false;
        char lastChar = word[^1];
        if (lastChar is ':' or '=' or '：' or '＝') return false;
        bool digit = false, upper = false, lower = false, symbol = false;
        foreach (var c in word)
        {
            if (char.IsDigit(c)) digit = true;
            else if (char.IsUpper(c)) upper = true;
            else if (char.IsLower(c)) lower = true;
            else if (c is '-' or '_' or '#' or '+' or '/' or '~' or '|' or '=') symbol = true;
        }
        return digit || (upper && lower && word.Length >= 4 && !IsCapitalizedWord(word)) || symbol;
    }

    /// <summary>ふつうの英単語に見えるか(英小文字だけ、または先頭だけ大文字)。キーの断片とは見なさない。</summary>
    private static bool LooksPlainWord(string? w)
    {
        if (string.IsNullOrEmpty(w)) return true;
        if (IsCapitalizedWord(w)) return true;
        foreach (var c in w) if (c is < 'a' or > 'z') return false;
        return true;
    }

    private static bool IsAsciiWord(string? w)
    {
        if (string.IsNullOrEmpty(w)) return false;
        // 日本語の OCR は英数字を全角(「Ｉ」「ｌ」など)で返すことがあるため、互換文字を半角にしてから調べる
        w = w.Normalize(NormalizationForm.FormKC);
        foreach (var c in w) if (c > 0x7E || c < 0x21) return false;
        return true;
    }

    /// <summary>「Hello」のように先頭だけ大文字のふつうの単語。</summary>
    private static bool IsCapitalizedWord(string w)
    {
        if (!char.IsUpper(w[0])) return false;
        for (int i = 1; i < w.Length; i++) if (!char.IsLower(w[i])) return false;
        return true;
    }

    private static RectD BoxFor(int start, int length, List<int> map, OcrLineBox line)
    {
        var box = default(RectD);
        int last = -1;
        for (int i = start; i < start + length && i < map.Count; i++)
        {
            int wi = map[i];
            if (wi < 0 || wi == last) continue; // -1 は単語間の区切り
            box = box.Union(line.Words[wi].Box);
            last = wi;
        }
        return box;
    }

    private static List<DetectionResult> Deduplicate(List<DetectionResult> items)
    {
        var seen = new HashSet<(DetectionCategory, RectD)>();
        var list = new List<DetectionResult>();
        foreach (var d in items)
            if (seen.Add((d.Category, d.Box))) list.Add(d);
        return list;
    }
}
