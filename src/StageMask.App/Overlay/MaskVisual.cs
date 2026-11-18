using System;
using System.Collections.Generic;
using System.Windows.Media;
using StageMask.Core.Geometry;
using StageMask.Core.Settings;

namespace StageMask.App.Overlay;

internal enum MaskKind
{
    /// <summary>手動で配置したマスク(編集モードで移動・削除できる)</summary>
    Manual,
    /// <summary>ウィンドウ全体のマスク</summary>
    Window,
    /// <summary>OCRで検出した領域のマスク</summary>
    Detection,
    /// <summary>位置合わせ確認用の枠線(マスクではない)</summary>
    DebugBox,
}

/// <summary>
/// オーバーレイに描く1つのマスク。座標は仮想スクリーン上の物理ピクセル。
/// Image はモザイク/ぼかし用の縮小画像、Text・ContentImage はマスクの上に重ねる文字・画像。
/// </summary>
internal sealed record MaskVisual(
    RectD ScreenRect,
    MaskKind Kind,
    MaskStyle Style,
    ImageSource? Image,
    Guid? ManualId = null,
    string? Text = null,
    ImageSource? ContentImage = null);

/// <summary>オーバーレイ全体の描画状態。</summary>
internal sealed record OverlayState(
    IReadOnlyList<MaskVisual> Visuals,
    bool Panic,
    bool EditMode,
    Brush SolidBrush,
    string PanicMessage,
    string EditHint);
