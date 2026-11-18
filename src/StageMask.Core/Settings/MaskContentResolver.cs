namespace StageMask.Core.Settings;

/// <summary>マスク上に実際に表示する文字・画像。どちらも null なら何も表示しない。</summary>
public sealed record ResolvedMaskContent(string? Text, string? ImagePath)
{
    public static readonly ResolvedMaskContent None = new(null, null);
}

public static class MaskContentResolver
{
    /// <summary>
    /// 共通設定とマスクごとの上書き設定から、表示する文字・画像を決める。
    /// - モザイク/ぼかしのマスクには画像を出さない(下の画面の雰囲気を見せるための見た目なので)
    /// - 空白だけの文字・パスは「なし」とみなす
    /// </summary>
    public static ResolvedMaskContent Resolve(AppearanceSettings common, MaskContentOverride? custom, MaskStyle style)
    {
        string? text, image;
        if (custom is { UseCustom: true })
        {
            text = custom.Text;
            image = custom.ImagePath;
        }
        else
        {
            text = common.ShowLabel ? common.LabelText : null;
            image = common.LabelImagePath;
        }

        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        image = string.IsNullOrWhiteSpace(image) || style != MaskStyle.Solid ? null : image.Trim().Trim('"');
        return text is null && image is null ? ResolvedMaskContent.None : new ResolvedMaskContent(text, image);
    }
}
