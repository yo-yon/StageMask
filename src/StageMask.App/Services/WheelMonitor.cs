using System;
using System.Runtime.InteropServices;
using System.Threading;
using StageMask.App.Native;

namespace StageMask.App.Services;

/// <summary>
/// マウスホイールの回転だけを検知する(低レベルマウスフック WH_MOUSE_LL)。
/// スクロールした瞬間に、カーソルの下のウィンドウを隠すために使う。
/// - ホイール以外のマウス操作・キー入力は一切見ない・記録しない
/// - イベントはそのまま次に渡す(スクロール自体は妨げない)
/// - 公式ドキュメントの推奨に従い、フックは専用スレッドで動かし、処理はすぐに返す
///   (UIスレッドが忙しくて応答が遅れると、Windows にフックを黙って外されるため)
/// </summary>
internal sealed class WheelMonitor : IDisposable
{
    private readonly NativeMethods.LowLevelMouseProc _proc; // GC に回収されないよう保持する
    private readonly Action<int, int> _onWheel;             // 専用スレッドから呼ばれる(スレッドセーフに処理すること)
    private Thread? _thread;
    private uint _threadId;
    private volatile IntPtr _hook;

    public WheelMonitor(Action<int, int> onWheel)
    {
        _onWheel = onWheel;
        _proc = HookProc;
    }

    public bool IsRunning => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_thread is not null) return;
        using var started = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandleW(null), 0);
            started.Set();
            if (_hook == IntPtr.Zero) return;

            // フックのコールバックは、このスレッドのメッセージループ経由で呼ばれる
            while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "StageMask.WheelMonitor",
        };
        _thread.Start();
        started.Wait(TimeSpan.FromSeconds(2));
    }

    public void Stop()
    {
        var t = _thread;
        if (t is null) return;
        if (_threadId != 0) NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        t.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _threadId = 0;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == NativeMethods.WM_MOUSEWHEEL || msg == NativeMethods.WM_MOUSEHWHEEL)
            {
                try
                {
                    var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    _onWheel(data.pt.X, data.pt.Y);
                }
                catch
                {
                    // フックの中で例外を出さない
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
