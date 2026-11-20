using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using StageMask.App.Capture;
using StageMask.Core.Detection;
using StageMask.Core.Geometry;
using StageMask.Core.Imaging;
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
    /// <summary>OCR 用の大きな配列のプール。大きさの区分ごとに1つだけ残す(残す量の上限を決めるため)</summary>
    private readonly ArrayPool<byte> _buffers = ArrayPool<byte>.Create(maxArrayLength: 256 * 1024 * 1024, maxArraysPerBucket: 1);

    public OcrService(bool useEnglish)
    {
        TryAdd("ja", "ja");
        if (useEnglish) TryAdd("en", "en-US");
    }

    /// <summary>この面積(ピクセル数)以下の画像は2倍に拡大してから読む。</summary>
    internal const long UpscaleMaxPixels = 4_000_000;

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

        // 小さい画像(変化した行だけの帯・小さいウィンドウ)は2倍にしてから読む。
        // 小さい文字だと読み落としや「test@examp le.com」のような単語の分割が起きやすいため。
        // 拡大するとOCRの時間が約4倍になるが、等倍だと全体を読み直すときにGUIDなどを読み落とすことが
        // 実機で確認できたため、一般的なウィンドウの大きさ(400万ピクセル)までは拡大する
        double scale = 1;
        // 大きな配列は専用のプールから借りて使い回す(4K のウィンドウを2倍にすると1枚で数十MBになり、
        // 毎回作るとメモリ使用量が大きく増えるため)。共有のプール(ArrayPool.Shared)は CPU コアごとに
        // 大きな配列を溜め込み、v0.5.29〜0.5.33 でメモリが数GBまで増えたため使わない
        byte[]? upscaled = null;
        var tileBuffers = new List<byte[]>();
        try
        {
            if ((long)frame.Width * frame.Height <= UpscaleMaxPixels)
            {
                var packed = frame.Stride == frame.Width * 4 ? frame.Pixels : frame.Crop(0, 0, frame.Width, frame.Height);
                upscaled = _buffers.Rent(frame.Width * 2 * frame.Height * 2 * 4);
                PixelUpscaler.Upscale2x(packed, frame.Width, frame.Height, upscaled);
                frame = new CapturedFrame(upscaled, frame.Width * 2, frame.Height * 2, frame.Width * 8, frame.Origin);
                scale = 2;
            }

            // OCRエンジンの最大画像サイズを超える場合はタイルに分割する
            int max = (int)Math.Min(OcrEngine.MaxImageDimension, int.MaxValue);
            if (max <= 0) max = 4096;

            // 分割するときは境目を重ねる(境目で文字列が切れて、メールアドレスなどを読み落とさないように)。
            // 重なった部分で同じ文字列が2回見つかることがあるが、マスクが重なるだけで問題はない
            int overlap = Math.Min(256, max / 4);
            int stepSize = max - overlap;

            var perEngine = new List<OcrLineBox>[_engines.Count];
            for (int e = 0; e < perEngine.Length; e++) perEngine[e] = new List<OcrLineBox>();

            for (int ty = 0; ty < frame.Height; ty += stepSize)
            {
                for (int tx = 0; tx < frame.Width; tx += stepSize)
                {
                    ct.ThrowIfCancellationRequested();
                    int w = Math.Min(max, frame.Width - tx);
                    int h = Math.Min(max, frame.Height - ty);

                    // 画像全体がそのまま1枚に収まるときは切り出さない
                    byte[] pixels;
                    if (tx == 0 && ty == 0 && w == frame.Width && h == frame.Height && frame.Stride == w * 4)
                        pixels = frame.Pixels;
                    else
                    {
                        pixels = _buffers.Rent(w * h * 4);
                        tileBuffers.Add(pixels);
                        for (int row = 0; row < h; row++)
                            Buffer.BlockCopy(frame.Pixels, (ty + row) * frame.Stride + tx * 4, pixels, row * w * 4, w * 4);
                    }

                    // 1枚の画像を、すべてのエンジン(日本語・英語)で使い回す
                    using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                        pixels.AsBuffer(0, w * h * 4), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
                    for (int e = 0; e < _engines.Count; e++)
                    {
                        var result = await _engines[e].Engine.RecognizeAsync(bitmap);
                        foreach (var line in result.Lines)
                        {
                            var words = new List<OcrWordBox>(line.Words.Count);
                            foreach (var word in line.Words)
                            {
                                var r = word.BoundingRect;
                                words.Add(new OcrWordBox(word.Text, new RectD((r.X + tx) / scale, (r.Y + ty) / scale, r.Width / scale, r.Height / scale)));
                            }
                            perEngine[e].Add(new OcrLineBox(words));
                        }
                    }
                    if (tx + w >= frame.Width) break;
                }
                if (ty + max >= frame.Height) break;
            }
            results.AddRange(perEngine);
        }
        finally
        {
            if (upscaled is not null) _buffers.Return(upscaled);
            foreach (var b in tileBuffers) _buffers.Return(b);
        }
        return results;
    }
}
