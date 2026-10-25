using SimOCR.Ocr;

namespace SimOCR.Export;

/// <summary>
/// **通用快速识别**：不预设文档类型，**也不要求有框线**。
///
///   1) 线格路径：像素里能找到表格线 → 用它的行列线（<see cref="LineGridDetector"/>，毫秒级）；
///   2) 空白对齐路径：找不到线 → 用 OCR 文本框的 x 区间切列（区间并集 + 覆盖低谷切分，同
///      <see cref="TableColumnDetector"/>），再按行聚类切行。
///
/// 两条路都只产出「行列分割线」，内容落格交给 <see cref="TableGrid.Refill"/>；
/// **不做语义推断**（列名就是 1..N），所以对"排列整齐的文字或表格"普遍成立。
/// </summary>
internal static class FastTableBuilder
{
    /// <summary>按文本框位置推出一张表；推不出（例如整块是段落文字）返回 null。</summary>
    internal static TableGrid? FromLines(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count < 4)
        {
            return null;
        }

        var median = MedianHeight(lines);
        if (median <= 1)
        {
            return null;
        }

        var bands = RowBands(lines, median);
        if (bands.Count < 2)
        {
            return null;
        }

        // 列切分：**优先用"跨行持续空白带"**（排版良好的表格每行每列只有一个文字块，
        // 低谷法在 peak==1 时会放弃切分，实测会把 5 列并成 4 列）；
        // 空白带切不出 2 列时再退回"区间并集 + 覆盖低谷"（对参差不齐的行更宽容）。
        var columnLines = ColumnLinesFromBlankBands(lines, bands, median);
        if (columnLines.Count < 3)
        {
            columnLines = ColumnLinesFromValleys(lines, median);
        }

        if (columnLines.Count < 3)
        {
            return null;
        }

        var rowLines = RowLinesFromBands(bands);
        if (rowLines.Count < 3)
        {
            return null;
        }

        var grid = new TableGrid(rowLines, columnLines, "空白对齐");
        return grid.IsUsable && IsSane(grid) ? grid : null;
    }

    /// <summary>持续空白带 → 列分割线（含左右边界）。</summary>
    private static List<double> ColumnLinesFromBlankBands(
        IReadOnlyList<OcrLine> lines,
        IReadOnlyList<IReadOnlyList<OcrLine>> bands,
        double median)
    {
        var inner = BlankColumnDetector.Detect(bands, median);
        if (inner.Count == 0)
        {
            return [];
        }

        var result = new List<double> { lines.Min(l => l.X0) };
        result.AddRange(inner);
        result.Add(lines.Max(l => l.X1));
        return result;
    }

    /// <summary>低谷切列 → 列分割线（含左右边界），分割线放在间隙中点。</summary>
    private static List<double> ColumnLinesFromValleys(IReadOnlyList<OcrLine> lines, double median)
    {
        var columns = TableColumnDetector.Detect(lines, TableColumnDetector.MinGap(median));
        if (columns.Count < 2)
        {
            return [];
        }

        var result = new List<double> { columns[0].Min };
        for (var i = 1; i < columns.Count; i++)
        {
            result.Add((columns[i - 1].Max + columns[i].Min) / 2.0);
        }

        result.Add(columns[^1].Max);
        return result;
    }

    /// <summary>
    /// 按 CenterY 把文本框聚成物理行（列切分与行切分共用同一套行分组）。
    /// 算法只有一份：<see cref="RowClustering.Bands"/>—— 本方法只是保持
    /// 既有调用面（<c>Diagnostics/SelfTest.cs</c> 的线格自检还在用）的薄壳。
    /// </summary>
    internal static List<List<OcrLine>> RowBands(IReadOnlyList<OcrLine> lines, double median)
        => RowClustering.Bands(lines, median);

    /// <summary>行分割线：相邻两行的外接区间取中点。</summary>
    private static List<double> RowLinesFromBands(IReadOnlyList<IReadOnlyList<OcrLine>> bands)
    {
        var extents = bands
            .Select(b => (Top: b.Min(l => l.Y0), Bottom: b.Max(l => l.Y1)))
            .ToList();

        var result = new List<double> { extents[0].Top };
        for (var i = 1; i < extents.Count; i++)
        {
            result.Add((extents[i - 1].Bottom + extents[i].Top) / 2.0);
        }

        result.Add(extents[^1].Bottom);
        return result;
    }

    /// <summary>字高中位数（收敛到 <see cref="RowClustering.MedianHeight"/>，此处只保留调用面）。</summary>
    internal static double MedianHeight(IReadOnlyList<OcrLine> lines)
        => RowClustering.MedianHeight(lines);

    /// <summary>
    /// 形状闸门（用户明确要求：**任何情况下不得输出形状不正常的表格**）：
    /// 行列数在合理范围、整表尺寸够大、每一列都有实际宽度、每一行都有实际高度。
    /// 逐列逐行那两道循环与 <see cref="IsSaneForManual"/>、<c>LineGridDetector.IsSane</c> 共用
    /// <see cref="IntervalMerge.HasMinimumSpacing"/>；**与手工档的差异只在这里的准入条件**。
    /// </summary>
    internal static bool IsSane(TableGrid grid)
    {        if (grid.RowCount < 1 || grid.ColumnCount < 2 || grid.RowCount > 400 || grid.ColumnCount > 100)
        {
            return false;
        }

        if (grid.ColumnLines[^1] - grid.ColumnLines[0] < 8 || grid.RowLines[^1] - grid.RowLines[0] < 4)
        {
            return false;
        }

        return IntervalMerge.HasMinimumSpacing(grid.ColumnLines)
               && IntervalMerge.HasMinimumSpacing(grid.RowLines);
    }

    /// <summary>
    /// **手工分割线**的闸门：比自动检测宽一档 —— 用户自己画的线是他想要的形状，
    /// 只拦真正退化的情形（没有内部分割线、0 宽/0 高的格子、行列数离谱）。
    /// 自动路径仍走严格的 <see cref="IsSane"/>（"不得输出形状不正常的表格"针对的是自动结果）。
    ///
    /// **与 <see cref="IsSane"/> 的差异（明确要求保留）**：这里**不**要求"≥2 列"、
    /// **不**要求整表最小尺寸（自动档要求列向总宽 ≥8px、行向总高 ≥4px）。差异全部落在
    /// 上面的准入条件里 —— 当时所说的"1px 差异"在 0.10.x 的代码里已收敛成这条口径：
    /// 同一道间距循环两者阈值相同（3px），手工档只是**少若干前置门槛**。
    /// </summary>
    internal static bool IsSaneForManual(TableGrid grid)
    {
        if (grid.ColumnCount < 1 || grid.RowCount < 1)
        {
            return false;
        }

        // 至少要有一条内部分割线，否则"1 行 1 列"不是表而是整块
        if (grid.ColumnCount < 2 && grid.RowCount < 2)
        {
            return false;
        }

        if (grid.RowCount > 400 || grid.ColumnCount > 100)
        {
            return false;
        }

        return IntervalMerge.HasMinimumSpacing(grid.ColumnLines)
               && IntervalMerge.HasMinimumSpacing(grid.RowLines);
    }
}
