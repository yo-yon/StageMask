using System.Globalization;
using System.Text;

namespace StageMask.Core.Detection;

/// <summary>
/// OCR結果とNGワードを同じ規則で正規化する。
/// - NFKC 正規化(全角英数→半角、半角カナ→全角カナ など)
/// - 小文字化
/// - 空白の除去
/// </summary>
public static class TextNormalizer
{
    /// <summary>1文字を正規化する。空白は空文字列を返す。</summary>
    public static string NormalizeChar(string ch)
    {
        if (string.IsNullOrEmpty(ch)) return string.Empty;
        var n = ch.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(n.Length);
        foreach (var c in n)
        {
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>文字列全体を正規化する(NGワード側の正規化に使う)。</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) sb.Append(NormalizeChar(e.GetTextElement()));
        return sb.ToString();
    }

    /// <summary>
    /// 正規表現用に、ダッシュ類をASCIIのハイフンにそろえる。
    /// (OCRがGUIDのハイフンを「‐」「−」「ー」などと読むことがあるため)
    /// 1文字→1文字の置換なので、文字位置の対応は崩れない。
    /// </summary>
    public static char FoldDash(char c) => c switch
    {
        '‐' or '‑' or '‒' or '–' or '—' or '―'
            or '−' or 'ー' or '－' or 'ｰ' => '-',
        _ => c,
    };
}
