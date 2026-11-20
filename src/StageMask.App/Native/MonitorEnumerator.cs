using System;
using System.Collections.Generic;
using StageMask.Core.Geometry;

namespace StageMask.App.Native;

internal sealed record MonitorInfo(IntPtr Handle, RectD Bounds, bool IsPrimary, string DeviceName = "")
{
    /// <summary>設定画面に出す名前(例: "DISPLAY2  1920×1080  (メイン)")</summary>
    public string DisplayName => $"{DeviceName.TrimStart('\\', '.')}  {Bounds.Width:0}×{Bounds.Height:0}  位置({Bounds.X:0},{Bounds.Y:0}){(IsPrimary ? "  (メイン)" : "")}";
}

internal static class MonitorEnumerator
{
    private const uint MONITORINFOF_PRIMARY = 1;

    /// <summary>接続中のモニターを列挙する(座標は物理ピクセル)。</summary>
    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        NativeMethods.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rc, IntPtr data) =>
        {
            var mi = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfoEx(hMonitor, ref mi))
            {
                var r = mi.rcMonitor;
                list.Add(new MonitorInfo(hMonitor, new RectD(r.Left, r.Top, r.Width, r.Height),
                                         (mi.dwFlags & MONITORINFOF_PRIMARY) != 0, mi.szDevice ?? ""));
            }
            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }

    /// <summary>デバイス名でモニターを探す。空ならメインのモニター。</summary>
    public static MonitorInfo? Find(IReadOnlyList<MonitorInfo> monitors, string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            foreach (var m in monitors) if (m.IsPrimary) return m;
            return monitors.Count > 0 ? monitors[0] : null;
        }
        foreach (var m in monitors)
            if (string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

    /// <summary>全モニターを含む仮想スクリーンの範囲。</summary>
    public static RectD VirtualScreen(IEnumerable<MonitorInfo> monitors)
    {
        var r = default(RectD);
        foreach (var m in monitors) r = r.Union(m.Bounds);
        return r;
    }
}
