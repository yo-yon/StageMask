using System.Collections.Generic;
using System.Linq;
using StageMask.Core.Detection;
using StageMask.Core.Geometry;

namespace StageMask.Core.Tests;

internal static class TestHelpers
{
    /// <summary>
    /// 空白区切りの文字列から、単語ごとに横並びの矩形を持つOCR行を作る。
    /// 各単語は幅 = 文字数 × 10px、単語間は 5px。
    /// </summary>
    public static OcrLineBox Line(string text, double y = 0)
    {
        var words = new List<OcrWordBox>();
        double x = 0;
        foreach (var w in text.Split(' ').Where(s => s.Length > 0))
        {
            words.Add(new OcrWordBox(w, new RectD(x, y, w.Length * 10, 20)));
            x += w.Length * 10 + 5;
        }
        return new OcrLineBox(words);
    }

    /// <summary>日本語OCRのように1文字ずつ単語に分かれた行を作る。</summary>
    public static OcrLineBox CharLine(string text, double y = 0)
    {
        var words = new List<OcrWordBox>();
        double x = 0;
        foreach (var c in text)
        {
            if (c == ' ') { x += 5; continue; }
            words.Add(new OcrWordBox(c.ToString(), new RectD(x, y, 10, 20)));
            x += 10;
        }
        return new OcrLineBox(words);
    }

    public static IReadOnlyList<DetectionResult> Detect(DetectorOptions options, params OcrLineBox[] lines)
        => new SensitiveTextDetector(options).Detect(lines);

    public static IReadOnlyList<DetectionResult> Detect(params OcrLineBox[] lines)
        => Detect(new DetectorOptions(), lines);
}
