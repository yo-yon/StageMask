using System.Collections.Generic;

namespace StageMask.Core.Detection;

public sealed class DetectorOptions
{
    public bool DetectEmail { get; init; } = true;
    public bool DetectIdentifier { get; init; } = true;
    public bool DetectSecret { get; init; } = true;

    /// <summary>
    /// 既知の形式に当てはまらない「長くてランダムな文字列」も秘密情報とみなす。
    /// 取りこぼしは減るが、URLの一部などを過剰にマスクすることがある。
    /// </summary>
    public bool DetectGenericSecret { get; init; } = true;

    public bool DetectNgWords { get; init; } = true;
    public IReadOnlyList<string> NgWords { get; init; } = new List<string>();

    /// <summary>
    /// NGワードのあいまい一致。4文字以上のNGワードについて、1文字までの誤認識を許容する。
    /// OCRの誤読(例: 「榎」→「框」)で取りこぼすのを防ぐ。
    /// </summary>
    public bool FuzzyNgWords { get; init; } = true;

    /// <summary>あいまい一致を適用するNGワードの最小文字数。</summary>
    public int FuzzyMinLength { get; init; } = 4;
}
