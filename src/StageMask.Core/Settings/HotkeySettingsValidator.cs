using System;
using System.Collections.Generic;

namespace StageMask.Core.Settings;

/// <summary>
/// ホットキーの設定を適用する前に確かめる。
/// 解釈できない値(「M」など)や、他の操作と同じキーは保存せず、前回まで使えていた値に戻す。
/// 不正な値を保存すると、パニックなどのホットキーが効かないまま本番を迎えてしまうため。
/// </summary>
public static class HotkeySettingsValidator
{
    private static readonly (string Label, Func<HotkeySettings, string> Get, Action<HotkeySettings, string> Set)[] Fields =
    {
        ("マスク全体のON/OFF", h => h.ToggleMasks, (h, v) => h.ToggleMasks = v),
        ("パニック", h => h.Panic, (h, v) => h.Panic = v),
        ("手動マスクの編集モード", h => h.EditMode, (h, v) => h.EditMode = v),
        ("自動検出のON/OFF", h => h.ToggleDetection, (h, v) => h.ToggleDetection = v),
        ("操作パネルを最前面に出す", h => h.ShowPanel, (h, v) => h.ShowPanel = v),
    };

    /// <summary>
    /// current の不正な値を lastGood の値に戻し、戻した内容の説明を返す(問題がなければ空)。
    /// </summary>
    /// <param name="validate">1つのキーの組み合わせを確かめる。問題があれば理由、なければ null</param>
    public static List<string> RevertInvalid(HotkeySettings current, HotkeySettings lastGood, Func<string, string?> validate)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(lastGood);
        ArgumentNullException.ThrowIfNull(validate);

        var messages = new List<string>();
        var used = new Dictionary<HotkeyGesture, string>();
        foreach (var (label, get, set) in Fields)
        {
            var text = get(current) ?? "";
            var problem = validate(text);
            if (problem is null && HotkeyGesture.TryParse(text, out var gesture))
            {
                if (used.TryGetValue(gesture, out var other))
                    problem = $"「{text}」は「{other}」と同じキーです";
                else
                    used[gesture] = label;
            }
            if (problem is null) continue;

            var previous = get(lastGood);
            set(current, previous);
            messages.Add($"{label}: {problem}。以前の「{previous}」のままにしました");
            if (HotkeyGesture.TryParse(previous, out var prevGesture)) used.TryAdd(prevGesture, label);
        }
        return messages;
    }

    public static HotkeySettings Copy(HotkeySettings h) => new()
    {
        ToggleMasks = h.ToggleMasks,
        Panic = h.Panic,
        EditMode = h.EditMode,
        ToggleDetection = h.ToggleDetection,
        ShowPanel = h.ShowPanel,
    };
}
