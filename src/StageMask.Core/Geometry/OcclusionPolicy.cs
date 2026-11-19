namespace StageMask.Core.Geometry;

/// <summary>
/// 手前にあるウィンドウが、後ろのウィンドウを「確実に覆い隠している」とみなせるかを決める。
/// そうみなしたウィンドウと重なる部分には、後ろのウィンドウのマスクを描かない(手前のウィンドウの内容を隠さないため)。
/// 見えない・透けるウィンドウを覆っているとみなすと、後ろの機密情報がそのまま見えてしまうので、
/// 確実に不透明だと判断できる「ふつうのアプリのウィンドウ」だけを対象にし、迷うものはマスクを残す(安全側)。
/// </summary>
public static class OcclusionPolicy
{
    public const uint WS_CAPTION = 0x00C00000;     // タイトルバー
    public const uint WS_THICKFRAME = 0x00040000;  // サイズ変更できる枠
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_LAYERED = 0x00080000;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    public static bool CanOcclude(uint style, long exStyle, string? title)
    {
        // クリックが素通りする透明なウィンドウ
        if ((exStyle & WS_EX_TRANSPARENT) != 0) return false;
        // 透明度を持てるウィンドウ(半透明のオーバーレイ・画面キャプチャー用の全画面ウィンドウなど)
        if ((exStyle & WS_EX_LAYERED) != 0) return false;
        // ツールウィンドウ・アクティブにならないウィンドウ(フローティングの小窓、通知、オーバーレイ)
        if ((exStyle & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false;
        // ふつうのアプリのウィンドウ: タイトルを持ち、タイトルバーかサイズ変更できる枠がある
        if (string.IsNullOrWhiteSpace(title)) return false;
        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        bool hasFrame = (style & WS_THICKFRAME) != 0;
        return hasCaption || hasFrame;
    }
}
