using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using StageMask.App.Capture;
using StageMask.Core.Detection;
using StageMask.Core.Geometry;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace StageMask.App.Ocr;

/// <summary>
/// Windows 標準の OCR(Windows.Media.Ocr)で文字を読み取る。処理はすべて端末内で行われる。
/// 日本語エンジンに加えて、英数字の精度を上げるため英語エンジンも併用できる。
/// 使うには、Windows の設定で該当言語(日本語・英語)の言語パックが入っている必要がある。
/// </summary>
internal sealed class OcrService
{
    private readonly List<(string Name, OcrEngine Engine)> _engines = new();

    public OcrService(bool useEnglish)
    {
        TryAdd("ja", "ja");
        if (useEnglish) TryAdd("en", "en-US");
    }

    public bool IsAvailable => _engines.Count > 0;

    /// <summary>利用中のエンジンの説明(例: "ja, en")</summary>
    public string Status => _engines.Count == 0
        ? "OCRエンジンが見つかりません(日本語/英語の言語パックを確認してください)"
        : string.Join(", ", _engines.ConvertAll(e => e.Name));

    private void TryAdd(string name, string tag)
    {
        try
        {
            var lang = new Language(tag);
            if (!OcrEngine.IsLanguageSupported(lang)) return;
            var engine = OcrEngine.TryCreateFromLanguage(lang);
            if (engine is not null) _engines.Add((name, engine));
        }
        catch
        {
            // 言語パックがない場合など。エンジンなしで続行する
        }
    }

    /// <summary>
    /// 画像をOCRし、エンジンごとの結果(行の一覧)を返す。座標はキャプチャ画像の座標。
    /// </summary>
    public async Task<List<IReadOnlyList<OcrLineBox>>> RecognizeAsync(CapturedFrame frame, CancellationToken ct)
    {
        var results = new List<IReadOnlyList<OcrLineBox>>();
        if (_engines.Count == 0 || frame.Width <= 0 || frame.Height <= 0) return results;

        // OCRエンジンの最大画像サイズを超える場合はタイルに分割する
        int max = (int)Math.Min(OcrEngine.MaxImageDimension, int.MaxValue);
        if (max <= 0) max = 4096;

        // 分割するときは境目を重ねる(境目で文字列が切れて、メールアドレスなどを読み落とさないように)。
        // 重なった部分で同じ文字列が2回見つかることがあるが、マスクが重なるだけで問題はない
        int overlap = Math.Min(256, max / 4);
        int stepSize = max - overlap;

        foreach (var (_, engine) in _engines)
        {
            var lines = new List<OcrLineBox>();
            for (int ty = 0; ty < frame.Height; ty += stepSize)
            {
                for (int tx = 0; tx < frame.Width; tx += stepSize)
                {
                    ct.ThrowIfCancellationRequested();
                    int w = Math.Min(max, frame.Width - tx);
                    int h = Math.Min(max, frame.Height - ty);
                    var pixels = frame.Crop(tx, ty, w, h);

                    using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                        pixels.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
                    var result = await engine.RecognizeAsync(bitmap);

                    foreach (var line in result.Lines)
                    {
                        var words = new List<OcrWordBox>(line.Words.Count);
                        foreach (var word in line.Words)
                        {
                            var r = word.BoundingRect;
                            words.Add(new OcrWordBox(word.Text, new RectD(r.X + tx, r.Y + ty, r.Width, r.Height)));
                        }
                        lines.Add(new OcrLineBox(words));
                    }
                    if (tx + w >= frame.Width) break;
                }
                if (ty + max >= frame.Height) break;
            }
            results.Add(lines);
        }
        return results;
    }
}
