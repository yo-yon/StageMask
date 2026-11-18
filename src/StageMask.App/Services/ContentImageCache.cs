using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StageMask.App.Services;

/// <summary>
/// マスク上に表示する画像を読み込んでキャッシュする(UIスレッド専用)。
/// 読み込めない画像は null を返し、理由を LastError に残す。
/// </summary>
internal sealed class ContentImageCache
{
    private const long MaxFileBytes = 20 * 1024 * 1024; // 20MB

    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string? LastError { get; private set; }

    public ImageSource? Get(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_cache.TryGetValue(path, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) LastError = $"画像が見つかりません: {path}";
            else if (fi.Length > MaxFileBytes) LastError = $"画像が大きすぎます(20MBまで): {path}";
            else
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(fi.FullName, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // ファイルをロックしない
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
            }
        }
        catch (Exception ex)
        {
            LastError = $"画像を読み込めません: {path}({ex.Message})";
        }
        _cache[path] = image;
        return image;
    }

    /// <summary>設定変更時に呼び、次回アクセスで読み込み直す。</summary>
    public void Clear()
    {
        _cache.Clear();
        LastError = null;
    }
}
