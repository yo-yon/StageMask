using System;
using System.IO;

namespace StageMask.App.Services;

/// <summary>エラーを %APPDATA%\StageMask\error.log に追記する。</summary>
internal static class Logger
{
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StageMask", "error.log");

    public static void Write(string source, Exception ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                // ログが肥大化しないよう 1MB を超えたら作り直す
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}{Environment.NewLine}");
            }
        }
        catch
        {
            // ログ出力の失敗でアプリを止めない
        }
    }
}
