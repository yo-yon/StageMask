using System;
using System.Collections.Generic;
using StageMask.Core.Geometry;

namespace StageMask.App.Native;

internal sealed record MonitorInfo(IntPtr Handle, RectD Bounds, bool IsPrimary);

internal static class MonitorEnumerator
{
    private const uint MONITORINFOF_PRIMARY = 1;

    /// <summary>接続中のモニターを列挙する(座標は物理ピクセル)。</summary>
    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        NativeMethods.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rc, IntPtr data) =>
        {
            var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfoW(hMonitor, ref mi))
            {
                var r = mi.rcMonitor;
                list.Add(new MonitorInfo(hMonitor, new RectD(r.Left, r.Top, r.Width, r.Height),
                                         (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }

    /// <summary>全モニターを含む仮想スクリーンの範囲。</summary>
    public static RectD VirtualScreen(IEnumerable<MonitorInfo> monitors)
    {
        var r = default(RectD);
        foreach (var m in monitors) r = r.Union(m.Bounds);
        return r;
    }
}
