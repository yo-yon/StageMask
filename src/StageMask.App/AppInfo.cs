using System.Reflection;

namespace StageMask.App;

/// <summary>アプリのバージョン情報(StageMask.App.csproj の Version から取得)。</summary>
internal static class AppInfo
{
    /// <summary>例: "0.3.1"</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var asm = typeof(AppInfo).Assembly;
        // InformationalVersion は "0.3.1+コミットID" の形になることがあるため、"+" 以降を除く
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            int plus = info.IndexOf('+');
            return plus >= 0 ? info.Substring(0, plus) : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "?";
    }
}
