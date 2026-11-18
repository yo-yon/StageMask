using System.Collections.Generic;
using StageMask.Core.Geometry;

namespace StageMask.Core.Detection;

/// <summary>OCRで認識された1単語(画像座標)。Windows.Media.Ocr.OcrWord から変換して渡す。</summary>
public sealed record OcrWordBox(string Text, RectD Box);

/// <summary>OCRで認識された1行。</summary>
public sealed record OcrLineBox(IReadOnlyList<OcrWordBox> Words);

public enum DetectionCategory
{
    /// <summary>メールアドレス・UPN</summary>
    Email,
    /// <summary>GUID・テナントドメインなどの識別子</summary>
    Identifier,
    /// <summary>APIキー・トークン・接続文字列などの秘密情報</summary>
    Secret,
    /// <summary>ユーザーが登録したNGワード</summary>
    NgWord,
}

/// <summary>検出結果。Box は検出に該当した単語の外接矩形(画像座標)。</summary>
public sealed record DetectionResult(DetectionCategory Category, string MatchedText, RectD Box);
