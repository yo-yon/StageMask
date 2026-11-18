using System;

namespace StageMask.Core.Settings;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    // RegisterHotKey の MOD_* と同じ値
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>"Ctrl+Alt+M" のような文字列を、修飾キーとキー名に分解する。</summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = HotkeyModifiers.None;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= HotkeyModifiers.Control; break;
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "win": case "windows": mods |= HotkeyModifiers.Win; break;
                default:
                    if (key is not null) return false; // キーが2つ以上
                    key = raw;
                    break;
            }
        }
        // 修飾キーなしのホットキーは誤爆しやすいので認めない
        if (key is null || mods == HotkeyModifiers.None) return false;
        gesture = new HotkeyGesture(mods, key);
        return true;
    }

    public override string ToString()
    {
        var s = "";
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) s += "Ctrl+";
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) s += "Alt+";
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) s += "Shift+";
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) s += "Win+";
        return s + Key;
    }
}
