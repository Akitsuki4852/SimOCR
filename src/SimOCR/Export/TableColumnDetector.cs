using SimOCR.Ocr;

namespace SimOCR.Export;

/// <summary>
/// 表格数据列的检测。四步算法本身与业务无关（阈值来自配置档的 <c>thresholds</c>）：
///   1. <see cref="DataColumns"/>：所有识别框的 x 区间取并集，间隙 ≥ minGap 处断开；
///   2. <see cref="SplitByValley"/>：在列内部"覆盖低谷"处切开。有的行会把两个字段认成一个框（"架桥"），
///      它把两列粘在一起；但桥只出现在少数行，逐像素统计覆盖数时桥处明显低于两侧真列 → 在低谷切开；
///   3. <see cref="TightenColumns"/>：把列收紧到"主体（≥60%）落在该列内"的框的实际范围。
///      右对齐的数字列常被低谷从中间切开，收紧后两截之间的缝很小；
///   4. <see cref="MergeColumns"/>：把窄缝并回一列（否则同一列会被贴上相邻列的标签）。
///
/// 关键参数：<c>minGap = max(minGapPixels, gapFactor × 中位字高)</c>（默认 10px / 0.22）。
/// **不要**用"表格宽度的百分比"当间隙阈值 —— 按宽度百分比会把同一行的相邻字段全部粘成一列。
/// </summary>
internal static class TableColumnDetector
{
    /// <summary>间隙阈值：与字高挂钩，系数与像素下限来自配置档。</summary>
    internal static double MinGap(double medianHeight) => MinGap(medianHeight, 0.22, 10.0);

    internal static double MinGap(double medianHeight, double gapFactor, double minGapPixels)
    {
        var factor = gapFactor > 0 ? gapFactor : 0.22;
        var floor = minGapPixels > 0 ? minGapPixels : 10.0;
        return Math.Max(floor, factor * medianHeight);
    }

    internal static List<(double Min, double Max)> Detect(IReadOnlyList<OcrLine> lines, double minGap)
    {
        // 只把"普通宽度"的行喂给列检测：页眉标题、跨列说明这类**超宽行**会把列间隙糊掉
        //（投影直方图上把低谷填平），实测无表头的页面会因此塌成 1 列。
        // 超宽行仅不参与"切列"，仍然会正常落到某个列里参与成格。
        // 这里得到的是"用于统计覆盖的窄行集合"，与配置档（profile）无关，故不叫 profile。
        var narrow = NarrowLines(lines);

        var columns = DataColumns(narrow, minGap);
        var split = new List<(double Min, double Max)>();
        foreach (var column in columns)
        {
            split.AddRange(SplitByValley(column, narrow, minGap, 0));
        }

        return MergeColumns(TightenColumns(split, narrow), minGap);
    }

    /// <summary>剔除"横跨大半张表"的行（宽度 &gt; 总宽 60%）；剩余的太少时退回全部。</summary>
    private static List<OcrLine> NarrowLines(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count < 8)
        {
            return [.. lines];
        }

        var span = lines.Max(l => l.X1) - lines.Min(l => l.X0);
        if (span <= 0)
        {
            return [.. lines];
        }

        var narrow = lines.Where(l => l.X1 - l.X0 <= span * 0.6).ToList();
        return narrow.Count >= 8 ? narrow : [.. lines];
    }

    /// <summary>第 1 步：区间并集切列（并集算法只有一份：<see cref="IntervalMerge.Union"/>）。</summary>
    private static List<(double Min, double Max)> DataColumns(IReadOnlyList<OcrLine> lines, double minGap)
        => IntervalMerge.Union(
            lines.Select(l => (Min: l.X0, Max: l.X1)),
            minGap);

    private static int[] CoverageProfile((double Min, double Max) column, IReadOnlyList<OcrLine> lines)
    {
        var x0 = (int)column.Min;
        var x1 = (int)column.Max;
        var counts = new int[Math.Max(1, x1 - x0 + 1)];
        foreach (var line in lines)
        {
            var from = Math.Max(x0, (int)line.X0) - x0;
            var to = Math.Min(x1, (int)line.X1) - x0;
            for (var x = Math.Max(0, from); x <= to && x < counts.Length; x++)
            {
                counts[x]++;
            }
        }

        return counts;
    }

    /// <summary>稳健峰值：取 90 分位而不是最大值 —— 表格边框竖线会让个别位置飙到上百，用最大值会把阈值抬得过高、切不开真正的低谷。</summary>
    private static int RobustPeak(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var ordered = values.OrderBy(v => v).ToArray();
        return ordered[(int)(0.9 * (ordered.Length - 1))];
    }

    /// <summary>第 2 步：在覆盖低谷处递归切开。</summary>
    private static List<(double Min, double Max)> SplitByValley(
        (double Min, double Max) column,
        IReadOnlyList<OcrLine> lines,
        double minGap,
        int depth)
    {
        if (depth > 6)
        {
            return [column];
        }

        var x0 = (int)column.Min;
        var x1 = (int)column.Max;
        if (x1 - x0 < 3 * minGap)
        {
            return [column];
        }

        var counts = CoverageProfile(column, lines);
        var peak = RobustPeak(counts);
        if (peak <= 1)
        {
            return [column];
        }

        var low = Math.Max(1, (int)(0.2 * peak));

        // 找 [x0+minGap, x1-minGap] 内最长的覆盖低谷
        var loBound = (int)minGap;
        var hiBound = counts.Length - (int)minGap;
        var bestLength = 0;
        var bestStart = -1;
        var runStart = -1;
        for (var i = Math.Max(0, loBound); i < Math.Max(0, hiBound); i++)
        {
            if (counts[i] <= low)
            {
                if (runStart < 0)
                {
                    runStart = i;
                }
            }
            else if (runStart >= 0)
            {
                if (i - runStart > bestLength)
                {
                    bestLength = i - runStart;
                    bestStart = runStart;
                }

                runStart = -1;
            }
        }

        if (runStart >= 0 && hiBound - runStart > bestLength)
        {
            bestLength = hiBound - runStart;
            bestStart = runStart;
        }

        if (bestStart < 0 || bestLength < minGap)
        {
            return [column];
        }

        var cut = x0 + bestStart + (bestLength / 2.0);

        // 两侧都必须有"真实内容"才切：否则一个横跨多列的长框会把一列反复切成碎片
        var splitAt = bestStart + (bestLength / 2);
        var leftPeak = RobustPeak(counts.Take(splitAt).ToArray());
        var rightPeak = RobustPeak(counts.Skip(splitAt).ToArray());
        if (leftPeak < 0.4 * peak || rightPeak < 0.4 * peak)
        {
            return [column];
        }

        var result = SplitByValley((column.Min, cut), lines, minGap, depth + 1);
        result.AddRange(SplitByValley((cut, column.Max), lines, minGap, depth + 1));
        return result;
    }

    /// <summary>第 3 步：收紧到主体落在列内的框的实际范围（用 60% 而不是中心点，避免粘框把刚切开的列又粘回去）。</summary>
    private static List<(double Min, double Max)> TightenColumns(
        List<(double Min, double Max)> columns,
        IReadOnlyList<OcrLine> lines)
    {
        var result = new List<(double Min, double Max)>();
        foreach (var column in columns)
        {
            var inside = lines
                .Where(l => Overlap(l.X0, l.X1, column.Min, column.Max) >= 0.6 * (l.X1 - l.X0))
                .ToList();
            if (inside.Count == 0)
            {
                inside = lines
                    .Where(l => column.Min <= (l.X0 + l.X1) / 2 && (l.X0 + l.X1) / 2 <= column.Max)
                    .ToList();
            }

            if (inside.Count == 0)
            {
                continue;
            }

            result.Add((inside.Min(l => l.X0), inside.Max(l => l.X1)));
        }

        return result;
    }

    /// <summary>第 4 步：合并窄缝（与第 1 步同一份并集实现）。</summary>
    private static List<(double Min, double Max)> MergeColumns(
        List<(double Min, double Max)> columns,
        double minGap)
        => IntervalMerge.Union(columns, minGap);

    internal static double Overlap(double a0, double a1, double b0, double b1)
        => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));
}
