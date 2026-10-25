using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SimOCR.Diagnostics;
using SimOCR.Export;

namespace SimOCR.Ocr;

/// <summary>
/// 竖带补充识别（"按竖条裁剪放大后重认、只补空缺"做法的一般化）。
///
/// 为什么需要：PP-OCRv5 的检测会把整页等比缩到长边 ~2000px。
/// 实测大图等比缩放到长边上限后缩放比会到 0.5 上下 —— 小字号直接糊掉，
/// 识别成 `32 36*88`、`42utt.` 这类噪声；而**裁剪后放大**能让同一块文字重新可读。
///
/// 做法：把页面按竖向切成若干条 → 每条放大 → 分别识别 → 把**原结果里没有的**行补进来。
/// 只补空缺、不覆盖已有结果，因此不会把已经认对的内容改坏。
/// </summary>
internal static class StripEnhancer
{
    /// <summary>页面长边超过这个值才做（否则本来就没被明显缩放，白花时间）。</summary>
    private const int MinSideForStrips = 2400;

    private const int StripCount = 3;

    private const double StripScale = 2.0;

    /// <summary>重叠判定用的 IoU 阈值：与原行高度重合就认为是同一行。</summary>
    private const double DuplicateIou = 0.4;

    internal static async Task<OcrDocument> EnhanceAsync(
        IRecognitionService recognition,
        BitmapSource image,
        OcrDocument document,
        LogService? log = null,
        CancellationToken cancellationToken = default)
    {
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        if (Math.Max(width, height) < MinSideForStrips || document.Lines.Count == 0)
        {
            return document;
        }

        var stripWidth = width / StripCount;
        if (stripWidth < 200)
        {
            return document;
        }

        var merged = new List<OcrLine>(document.Lines);
        var added = 0;
        for (var i = 0; i < StripCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rect = new Int32Rect(i * stripWidth, 0, stripWidth, height);
            BitmapSource strip;
            BitmapSource scaled;
            try
            {
                strip = new CroppedBitmap(image, rect);
                scaled = new TransformedBitmap(strip, new ScaleTransform(StripScale, StripScale));
            }
            catch (Exception)
            {
                continue; // 裁剪失败就当这条不做，不影响主流程
            }

            OcrDocument part;
            try
            {
                part = await recognition.RecognizeAsync(scaled, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var line in part.Lines)
            {
                if (!IsUseful(line.Text))
                {
                    continue;
                }

                var mapped = Map(line, StripScale, rect.X, rect.Y);
                if (merged.Any(existing => OverlapRatio(existing, mapped) >= DuplicateIou))
                {
                    continue;
                }

                merged.Add(mapped);
                added++;
            }
        }

        if (added == 0)
        {
            return document;
        }

        log?.Info($"竖带补充识别：切 {StripCount} 条放大 {StripScale:0.#}× 重认，补进 {added} 行（只补空缺）");
        return new OcrDocument(document.ImageWidth, document.ImageHeight, merged, document.ElapsedMs);
    }

    /// <summary>
    /// 只补"有意义"的行：含数字的行一律算；英寸记号这类**尺寸写法**只在配置档声明了尺寸记号时才算
    /// （见 <see cref="TableProfile.HasMeaningfulValue"/>）。纯文字噪声不进。
    /// </summary>
    private static bool IsUseful(string? text) => TableProfiles.Current.HasMeaningfulValue(text ?? string.Empty);

    private static OcrLine Map(OcrLine line, double scale, double dx, double dy)
        => new(
            line.Text,
            line.Score,
            line.Box.Select(p => ((p.X / scale) + dx, (p.Y / scale) + dy)).ToArray(),
            line.OnStamp);

    /// <summary>两行的纵向重合度（按行高归一），用于去重。</summary>
    private static double OverlapRatio(OcrLine a, OcrLine b)
    {
        var top = Math.Max(a.Y0, b.Y0);
        var bottom = Math.Min(a.Y1, b.Y1);
        if (bottom <= top)
        {
            return 0;
        }

        var overlap = bottom - top;
        var height = Math.Min(a.Y1 - a.Y0, b.Y1 - b.Y0);
        if (height <= 0)
        {
            return 0;
        }

        // 纵向重合高 + 横向有交叠 => 认为是同一处内容
        var horizontal = Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0);
        if (horizontal <= 0)
        {
            return 0;
        }

        return overlap / height;
    }
}
