using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using StageMask.Core.Geometry;

namespace StageMask.App.Native;

/// <summary>トップレベルウィンドウの情報。座標は物理ピクセル。</summary>
internal sealed record WindowInfo(
    IntPtr Handle,
    string Title,
    string ClassName,
    string ProcessName,
    int ProcessId,
    bool IsToolWindow,
    RectD WindowRect,   // GetWindowRect(不可視のリサイズ枠を含む)
    RectD FrameRect,    // DWMWA_EXTENDED_FRAME_BOUNDS(見た目どおりの範囲)
    bool IsMinimized,
    bool IsPopupLike = false, // メニュー・入力候補・ツールチップなど、本体に付随する小さなウィンドウ
    bool CanOcclude = true)   // 後ろのウィンドウを覆い隠す(不透明な)ウィンドウとみなしてよいか
{
    /// <summary>ウィンドウ選択リスト用の表示名</summary>
    public string Display => $"{ProcessName} — {Title}";
}

internal static class WindowEnumerator
{
    // デスクトップ・タスクバーなど、マスク対象にしてはいけないシェルのウィンドウ
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    private static readonly object Gate = new(); // UIスレッドと検出ワーカーの両方から呼ばれるため
    private static readonly Dictionary<int, string> ProcessNameCache = new();
    private static DateTime _cacheClearedUtc = DateTime.UtcNow;
    private static readonly int OwnProcessId = Environment.ProcessId;

    /// <summary>
    /// 表示中のトップレベルウィンドウを前面から順に列挙する。自プロセスのウィンドウは含めない。
    /// </summary>
    public static List<WindowInfo> GetWindows()
    {
        lock (Gate)
        {
            if ((DateTime.UtcNow - _cacheClearedUtc).TotalSeconds > 30)
            {
                ProcessNameCache.Clear(); // PID の再利用に備えて定期的に破棄
                _cacheClearedUtc = DateTime.UtcNow;
            }
        }

        var handles = new List<IntPtr>();
        NativeMethods.EnumWindowsProc callback = (h, _) => { handles.Add(h); return true; };
        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        var result = new List<WindowInfo>();
        foreach (var h in handles)
        {
            var info = TryGetInfo(h);
            if (info is not null) result.Add(info);
        }
        return result;
    }

    /// <summary>ウィンドウ情報を取得する。非表示・クローク中・自プロセス・シェルの場合は null。</summary>
    public static WindowInfo? TryGetInfo(IntPtr h)
    {
        if (!NativeMethods.IsWindow(h) || !NativeMethods.IsWindowVisible(h)) return null;
        if (IsCloaked(h)) return null;

        NativeMethods.GetWindowThreadProcessId(h, out var pid);
        if (pid == 0 || pid == OwnProcessId) return null;

        var cls = GetClassName(h);
        if (ExcludedClasses.Contains(cls)) return null;

        if (!NativeMethods.GetWindowRect(h, out var wr) || wr.Width <= 0 || wr.Height <= 0) return null;
        var windowRect = new RectD(wr.Left, wr.Top, wr.Width, wr.Height);
        var frameRect = GetFrameRect(h) ?? windowRect;

        var ex = NativeMethods.GetWindowLong(h, NativeMethods.GWL_EXSTYLE);
        uint style = (uint)(NativeMethods.GetWindowLong(h, NativeMethods.GWL_STYLE) & 0xFFFFFFFF);
        var title = GetTitle(h);
        bool isTool = (ex & NativeMethods.WS_EX_TOOLWINDOW) != 0;
        // 本体に付随するウィンドウの見分け方(経験則):
        // 持ち主(オーナー)がいる / ツールウィンドウ / タイトルバーのないポップアップでタイトルが空
        bool popupLike = NativeMethods.GetWindow(h, NativeMethods.GW_OWNER) != IntPtr.Zero
                         || isTool
                         || ((style & NativeMethods.WS_POPUP) != 0 && (style & NativeMethods.WS_CAPTION) != NativeMethods.WS_CAPTION && title.Length == 0);
        return new WindowInfo(
            h,
            title,
            cls,
            GetProcessName((int)pid),
            (int)pid,
            isTool,
            windowRect,
            frameRect,
            NativeMethods.IsIconic(h),
            popupLike,
            OcclusionPolicy.CanOcclude(style, ex, title));
    }

    /// <summary>位置・最小化状態だけを再取得する(毎フレーム呼ぶ軽量版)。閉じられていれば null。</summary>
    public static WindowInfo? RefreshGeometry(WindowInfo w)
    {
        var h = w.Handle;
        if (!NativeMethods.IsWindow(h) || !NativeMethods.IsWindowVisible(h) || IsCloaked(h)) return null;
        if (!NativeMethods.GetWindowRect(h, out var wr) || wr.Width <= 0 || wr.Height <= 0) return null;
        var windowRect = new RectD(wr.Left, wr.Top, wr.Width, wr.Height);
        return w with
        {
            WindowRect = windowRect,
            FrameRect = GetFrameRect(h) ?? windowRect,
            IsMinimized = NativeMethods.IsIconic(h),
        };
    }

    private static RectD? GetFrameRect(IntPtr h)
    {
        int hr = NativeMethods.DwmGetWindowAttribute(h, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                                                     out NativeMethods.RECT r, Marshal.SizeOf<NativeMethods.RECT>());
        if (hr != 0 || r.Width <= 0 || r.Height <= 0) return null;
        return new RectD(r.Left, r.Top, r.Width, r.Height);
    }

    private static bool IsCloaked(IntPtr h)
    {
        // 別の仮想デスクトップ上のウィンドウや、中断中のストアアプリは「クローク」されていて画面に出ない
        int hr = NativeMethods.DwmGetWindowAttribute(h, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    private static string GetTitle(IntPtr h)
    {
        int len = NativeMethods.GetWindowTextLengthW(h);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetProcessName(int pid)
    {
        lock (Gate)
        {
            if (ProcessNameCache.TryGetValue(pid, out var cached)) return cached;
        }
        string name;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
        }
        catch (Exception)
        {
            name = "(unknown)";
        }
        lock (Gate) ProcessNameCache[pid] = name;
        return name;
    }
}
