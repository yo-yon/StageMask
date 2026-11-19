using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using StageMask.App.Native;
using StageMask.Core.Settings;

namespace StageMask.App.Services;

/// <summary>グローバルホットキー(RegisterHotKey)を管理する。</summary>
internal sealed class HotkeyManager : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0x5100;

    public HotkeyManager(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);
    }

    /// <summary>キーの組み合わせとして正しいか(登録はしない)。問題があれば理由を返す。</summary>
    public static string? Validate(string gestureText)
        => TryResolve(gestureText, out _, out _, out var error) ? null : error;

    private static bool TryResolve(string gestureText, out HotkeyGesture gesture, out Key key, out string? error)
    {
        key = Key.None;
        error = null;
        if (!HotkeyGesture.TryParse(gestureText, out gesture))
        {
            error = $"「{gestureText}」は解釈できません(例: Ctrl+Alt+M)";
            return false;
        }
        var keyName = gesture.Key;
        if (keyName.Length == 1 && char.IsDigit(keyName[0])) keyName = "D" + keyName; // "1" → Key.D1
        if (!Enum.TryParse<Key>(keyName, ignoreCase: true, out key) || key == Key.None)
        {
            error = $"「{gesture.Key}」というキーはありません";
            return false;
        }
        return true;
    }

    /// <summary>ホットキーを登録する。失敗した場合はエラー内容を返す。</summary>
    public string? Register(string gestureText, Action action)
    {
        if (!TryResolve(gestureText, out var gesture, out var key, out var error)) return error;

        int vk = KeyInterop.VirtualKeyFromKey(key);
        int id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_hwnd, id, (uint)gesture.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)vk))
            return $"「{gesture}」は他のアプリが使用中のため登録できません";

        _actions[id] = action;
        return null;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) NativeMethods.UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }
}
