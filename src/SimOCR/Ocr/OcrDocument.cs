using System.Collections.Generic;
using System.Linq;
using System.Text;
using SimOCR.Export;

namespace SimOCR.Ocr;

/// <summary>识别出的一行文字（坐标一律是**选区图的物理像素**，与覆盖层/贴图窗口同一套几何约定）。</summary>
internal sealed class OcrLine
{
    internal OcrLine(string text, float score, IReadOnlyList<(double X, double Y)> box, bool onStamp = false)
    {
        Text = text;
        Score = score;
        Box = box;
        OnStamp = onStamp;

        X0 = box.Min(p => p.X);
        Y0 = box.Min(p => p.Y);
        X1 = box.Max(p => p.X);
        Y1 = box.Max(p => p.Y);
    }

    internal string Text { get; }

    internal float Score { get; }

    /// <summary>四角点（顺时针，来自检测阶段的多边形）。</summary>
    internal IReadOnlyList<(double X, double Y)> Box { get; }

    internal double X0 { get; }

    internal double Y0 { get; }

    internal double X1 { get; }

    internal double Y1 { get; }

    internal double CenterY => (Y0 + Y1) / 2;

    internal double Height => Y1 - Y0;

    /// <summary>这一行是否压在红色印章上（去印章用：印章文字会污染列结构）。</summary>
    internal bool OnStamp { get; }

    /// <summary>标记为"压在印章上"（返回新实例；本类是不可变的）。</summary>
    internal OcrLine MarkOnStamp() => new(Text, Score, Box, onStamp: true);

    /// <summary>整体平移（编辑模式里"补识别"子区域的结果要平移回原图坐标）。</summary>
    internal OcrLine Offset(double dx, double dy)
        => new(Text, Score, Box.Select(p => (p.X + dx, p.Y + dy)).ToArray());
}

/// <summary>一次识别的完整结果：图片尺寸 + 文本行 + 耗时。</summary>
internal sealed class OcrDocument
{
    internal OcrDocument(int imageWidth, int imageHeight, IReadOnlyList<OcrLine> lines, long elapsedMs)
    {
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        Lines = SortReadingOrder(lines);
        ElapsedMs = elapsedMs;
    }

    internal int ImageWidth { get; }

    internal int ImageHeight { get; }

    /// <summary>已按阅读顺序（自顶向下、同行自左向右）排好。</summary>
    internal IReadOnlyList<OcrLine> Lines { get; }

    internal long ElapsedMs { get; }

    /// <summary>
    /// 阅读顺序：先按 y 中心聚成"行"（容差取中位行高的 0.6），行内按 x0 排序；
    /// 行内相邻两段若水平间隙较大，拼接时补一个空格（中文之间不补）。
    /// 行分组的算法只有一份：<see cref="RowClustering.Bands"/>。
    /// </summary>
    internal static IReadOnlyList<OcrLine> SortReadingOrder(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count <= 1)
        {
            return lines;
        }

        var bands = RowClustering.Bands(lines, RowClustering.MedianHeight(lines));

        var result = new List<OcrLine>(lines.Count);
        foreach (var band in bands)
        {
            result.AddRange(band.OrderBy(l => l.X0));
        }

        return result;
    }

    /// <summary>纯文本（导出 TXT / DOCX 用）：每一视觉行一段，行内按阅读顺序拼接。</summary>
    internal string ToPlainText() => ToPlainTextWithSpans().Text;

    /// <summary>
    /// 纯文本 + 每一行在文本里的 (起始下标, 长度)。
    /// 识别结果窗口用它做"点框定位文本、选中文本高亮框"的联动。
    /// </summary>
    internal (string Text, IReadOnlyList<(int Start, int Length)> Spans) ToPlainTextWithSpans()
    {
        var spans = new List<(int Start, int Length)>(Lines.Count);
        if (Lines.Count == 0)
        {
            return (string.Empty, spans);
        }

        var medianHeight = RowClustering.MedianHeight(Lines);
        var rowTolerance = RowClustering.Tolerance(medianHeight);

        var builder = new StringBuilder();
        OcrLine? previous = null;
        foreach (var line in Lines.Where(l => !l.OnStamp))
        {
            if (previous is not null)
            {
                if (Math.Abs(line.CenterY - previous.CenterY) <= rowTolerance)
                {
                    // 同一视觉行：按水平间隙分档决定分隔符 ——
                    //   很小 → 直接相连（中文之间、被切开的单词）
                    //   中等 → 补一个空格（拉丁词间）
                    //   很大 → 制表符（这是表格的相邻列，粘到 Excel/Word 里能对齐）
                    var gap = line.X0 - previous.X1;
                    if (gap > medianHeight * 1.5)
                    {
                        builder.Append('\t');
                    }
                    else if (gap > medianHeight * 0.35)
                    {
                        builder.Append(' ');
                    }
                }
                else
                {
                    builder.Append('\n');
                }
            }

            spans.Add((builder.Length, line.Text.Length));
            builder.Append(line.Text);
            previous = line;
        }

        return (builder.ToString(), spans);
    }
}
