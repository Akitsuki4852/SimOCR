using SimOCR.Ocr;

namespace SimOCR.Export;

/// <summary>
/// **无框线**表格的列切分：不靠"某一行有没有低谷"，而靠**跨行持续存在的空白带**。
///
/// 为什么另起一个（而不是改 <see cref="TableColumnDetector"/>）：
/// TableColumnDetector 走的是"区间并集 + 覆盖低谷"，它的低谷判据要求覆盖峰值 &gt; 1；
/// 而**排版良好的表格每行每列只有一个文字块**（peak == 1），低谷法遇到 peak==1 直接放弃切分
/// —— 实测 5 列的表被并成 4 列（末两列粘连）。
///
/// 持续空白带没有这个前提，而且它正是主流实现的共同信号定义：
/// pdfplumber 的 `text` 策略（按 word 的 left/right/center 聚出"假想线"+ `min_words_vertical`）、
/// Camelot 的 `columns`/`edge_tol`、Tabula 的 `--columns`、img2table borderless 的
/// "跨多行持续存在的垂直空白带"，都要求**对齐/空白被多行反复复现**才认一条分隔线。
///
/// 本实现：逐像素统计"有多少行在该位置没有文字"（穿透率），取穿透率 ≥ 0.9 的极大连续区间，
/// 再按宽度门槛与列内容支撑度过滤 —— 因此对右对齐数字列、个别行空洞都稳。
/// </summary>
internal static class BlankColumnDetector
{
    /// <summary>某个 x 位置要被认为是"空白"，至少要有这么高比例的行在该处没有文字。</summary>
    private const double PenetrationRatio = 0.9;

    /// <summary>列内容支撑度低于这个值，说明这条分隔线把一列切成了空列 —— 该线不成立。</summary>
    private const double MinColumnSupport = 0.3;

    /// <summary>返回**内部**列分隔线（像素 x，升序）；调用方自己补左右边界。</summary>
    internal static List<double> Detect(IReadOnlyList<IReadOnlyList<OcrLine>> rows, double medianHeight)
    {
        if (rows.Count < 2)
        {
            return [];
        }

        var all = rows.SelectMany(r => r).ToList();
        if (all.Count < 2)
        {
            return [];
        }

        var left = all.Min(l => l.X0);
        var right = all.Max(l => l.X1);
        if (right - left < 4)
        {
            return [];
        }

        // 字宽**不能**只看字高：OCR 检测框比字形本身胖（实测中位框高 49px 对应 26px 字号），
        // 用 0.55×框高 会把"多窄的空白算真空白"估大一倍，真正的列间隙反而被判太窄而丢掉。
        // 改成按每个框的 (框宽 / 字符数) 取中位，最接近真实字宽。
        var charWidth = EstimateCharWidth(all, medianHeight);
        var snap = Math.Max(3.0, charWidth * 0.3);
        var minBandWidth = Math.Max(4.0, charWidth * 0.35);

        // **检测框比字形胖**：PP-OCR 的框在文字四周留了几像素（实测 "Unit Price"[337-485] 与
        // "Amount"[495-620] 的视觉间隙只有 9px，实际排版间隙约 17px）。不内收就会把真实列间隙
        // 判成"太窄"而丢掉 —— 这也是"区间并集"类方法容易把相邻列粘在一起的根因。
        var inset = Math.Min(charWidth * 0.3, medianHeight * 0.2);

        var from = (int)Math.Floor(left);
        var to = (int)Math.Ceiling(right);
        var width = to - from + 1;
        if (width <= 2)
        {
            return [];
        }

        var covered = Coverage(rows, from, to, width, inset);

        // 两轮：先严格（要求所有行都在该处没字），不行再放宽 1 行 ——
        // 排版良好的表格用严格档就够；有表头合并/个别行偏移时靠放宽档兜住。
        var allowances = new[] { (int)Math.Floor((1 - PenetrationRatio) * rows.Count), Math.Max(1, (int)Math.Floor(0.2 * rows.Count)) };
        foreach (var allowance in allowances.Distinct())
        {
            var candidates = Candidates(covered, width, from, left, right, allowance, minBandWidth, snap);
            var lines = Filter(candidates, rows, snap);
            if (lines.Count > 0)
            {
                return lines;
            }
        }

        return [];
    }

    /// <summary>逐像素统计该位置被多少行覆盖（同一行内先把区间并起来，避免重复计数）。</summary>
    private static int[] Coverage(IReadOnlyList<IReadOnlyList<OcrLine>> rows, int from, int to, int width, double inset)
    {
        var covered = new int[width];
        foreach (var row in rows)
        {
            var spans = row.Select(l =>
                {
                    var start = l.X0 + inset;
                    var end = l.X1 - inset;
                    // 内收后如果框太窄（单字/短符号），退化成框中心的一个点
                    if (end <= start)
                    {
                        var center = (l.X0 + l.X1) / 2.0;
                        return (Start: (int)Math.Floor(center), End: (int)Math.Ceiling(center));
                    }

                    return (Start: (int)Math.Floor(start), End: (int)Math.Ceiling(end));
                })
                .OrderBy(s => s.Start)
                .ToList();
            // 同一行内的区间并集只有一份实现：相邻即并，避免重复计数
            var merged = IntervalMerge.UnionSpans(spans);

            foreach (var (start, end) in merged)
            {
                var s = Math.Max(start, from);
                var e = Math.Min(end, to);
                for (var x = s; x <= e; x++)
                {
                    covered[x - from]++;
                }
            }
        }

        return covered;
    }

    /// <summary>取"空白带"的中点作为候选分隔线。</summary>
    private static List<double> Candidates(
        int[] covered,
        int width,
        int from,
        double left,
        double right,
        int allowance,
        double minBandWidth,
        double snap)
    {
        var candidates = new List<double>();
        var runStart = -1;
        for (var i = 0; i <= width; i++)
        {
            var blank = i < width && covered[i] <= allowance;
            if (blank && runStart < 0)
            {
                runStart = i;
                continue;
            }

            if (blank || runStart < 0)
            {
                continue;
            }

            var start = runStart + from;
            var end = i - 1 + from;
            runStart = -1;

            if (end - start + 1 < minBandWidth)
            {
                continue; // 抗锯齿缝隙 / 字间空隙
            }

            if (start <= left + snap || end >= right - snap)
            {
                continue; // 贴边的是表格外边距，不是列分隔
            }

            candidates.Add((start + end) / 2.0);
        }

        return candidates;
    }

    /// <summary>按 (框宽 / 字符数) 的中位数估字宽（比"字高 × 系数"稳）。</summary>
    private static double EstimateCharWidth(IReadOnlyList<OcrLine> lines, double medianHeight)
    {
        var widths = new List<double>();
        foreach (var line in lines)
        {
            var chars = line.Text?.Trim().Length ?? 0;
            if (chars <= 0)
            {
                continue;
            }

            widths.Add((line.X1 - line.X0) / chars);
        }

        if (widths.Count == 0)
        {
            return Math.Max(2.0, medianHeight * 0.55);
        }

        widths.Sort();
        return Math.Clamp(widths[widths.Count / 2], 2.0, Math.Max(2.0, medianHeight));
    }

    /// <summary>吸附过近的候选线，并丢掉"切出空列"的线。</summary>
    private static List<double> Filter(List<double> candidates, IReadOnlyList<IReadOnlyList<OcrLine>> rows, double snap)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var snapped = new List<double>();
        foreach (var value in candidates.OrderBy(v => v))
        {
            if (snapped.Count > 0 && value - snapped[^1] <= snap)
            {
                snapped[^1] = (snapped[^1] + value) / 2.0;
                continue;
            }

            snapped.Add(value);
        }

        // 逐条检查：这条线不能把某一侧切成"几乎没内容的列"
        var result = new List<double>();
        foreach (var line in snapped)
        {
            var leftEdge = result.Count > 0 ? result[^1] : double.NegativeInfinity;
            var leftSupport = Support(rows, leftEdge, line);
            var rightSupport = Support(rows, line, NextLine(snapped, line));
            if (leftSupport >= MinColumnSupport && rightSupport >= MinColumnSupport)
            {
                result.Add(line);
            }
        }

        return result;
    }

    private static double NextLine(List<double> lines, double current)
    {
        var index = lines.IndexOf(current);
        return index >= 0 && index + 1 < lines.Count ? lines[index + 1] : double.PositiveInfinity;
    }

    /// <summary>区间内"有内容的行"占比。</summary>
    private static double Support(IReadOnlyList<IReadOnlyList<OcrLine>> rows, double left, double right)
    {
        if (double.IsInfinity(left) || double.IsInfinity(right) || right <= left)
        {
            return 1.0; // 边界外的区间不做判断
        }

        var hit = 0;
        foreach (var row in rows)
        {
            foreach (var line in row)
            {
                var center = (line.X0 + line.X1) / 2.0;
                if (center >= left && center < right)
                {
                    hit++;
                    break;
                }
            }
        }

        return rows.Count == 0 ? 1.0 : (double)hit / rows.Count;
    }
}
