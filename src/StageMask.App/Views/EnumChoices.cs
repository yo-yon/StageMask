using System.Collections.Generic;
using StageMask.Core.Settings;

namespace StageMask.App.Views;

/// <summary>ComboBox 用の「値と日本語の表示名」。</summary>
public sealed record EnumChoice(object Value, string Label);

public static class EnumChoices
{
    public static IReadOnlyList<EnumChoice> RuleActions { get; } = new[]
    {
        new EnumChoice(RuleAction.HideWindow, "ウィンドウ全体を隠す"),
        new EnumChoice(RuleAction.AutoDetect, "機密情報だけ隠す(OCR)"),
    };

    public static IReadOnlyList<EnumChoice> MaskStyles { get; } = new[]
    {
        new EnumChoice(MaskStyle.Solid, "塗りつぶし"),
        new EnumChoice(MaskStyle.Mosaic, "モザイク"),
        new EnumChoice(MaskStyle.Blur, "ぼかし"),
    };

    public static IReadOnlyList<EnumChoice> CaptureMethods { get; } = new[]
    {
        new EnumChoice(CaptureMethod.PrintWindow, "PrintWindow(枠なし・非公式フラグ使用)"),
        new EnumChoice(CaptureMethod.GraphicsCapture, "Windows.Graphics.Capture(公式・黄色枠が出る)"),
    };
}
