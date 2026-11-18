using System;
using System.IO;

namespace StageMask.Core.Settings;

public static class WindowRuleMatcher
{
    /// <summary>ウィンドウがルールに一致するか。プロセス名・タイトルとも大文字小文字を区別しない。</summary>
    public static bool Matches(WindowRule rule, string processName, string title)
    {
        if (!rule.Enabled) return false;
        var ruleProc = NormalizeProcessName(rule.ProcessName);
        if (ruleProc.Length == 0 && string.IsNullOrWhiteSpace(rule.TitleContains)) return false;

        if (ruleProc.Length > 0 &&
            !string.Equals(ruleProc, NormalizeProcessName(processName), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(rule.TitleContains) &&
            (title ?? "").IndexOf(rule.TitleContains.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        return true;
    }

    /// <summary>"outlook.exe" → "outlook"</summary>
    public static string NormalizeProcessName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = Path.GetFileNameWithoutExtension(n);
        return n;
    }
}
