using SimOCR.Ocr;

namespace SimOCR.Export;

/// <summary>
/// **可编辑的表格网格**：行列分割线都在**图片像素**坐标系里，因此可以直接叠在预览图上被拖动。
///
/// 快速识别（通用识别）用「线格」或「空白对齐」两路之一得到初始分割线 → 用户可手工增删/移动 →
/// 点「刷新」按当前分割线重新落格。语义（列名、派生列、校验）只在增强识别里做，
/// 这里**不做任何推断**，因此对"长得像表格的任何东西"都成立。
/// </summary>
internal sealed class TableGrid
{
    internal TableGrid(IEnumerable<double> rowLines, IEnumerable<double> columnLines, string source)
    {
        RowLines = Normalize(rowLines);
        ColumnLines = Normalize(columnLines);
        Source = source;
    }

    /// <summary>行分割线（像素 Y），升序，长度 = 行数 + 1。</summary>
    internal List<double> RowLines { get; }

    /// <summary>列分割线（像素 X），升序，长度 = 列数 + 1。</summary>
    internal List<double> ColumnLines { get; }

    /// <summary>来源说明（"线格" / "空白对齐" / "手工调整"），显示在状态条上。</summary>
    internal string Source { get; set; }

    /// <summary>落格后的文字，Cells[行][列]。</summary>
    internal List<List<string>> Cells { get; private set; } = [];

    internal int RowCount => Math.Max(RowLines.Count - 1, 0);

    internal int ColumnCount => Math.Max(ColumnLines.Count - 1, 0);

    /// <summary>至少 2 列 1 行才算表（单列的内容就是普通文本，不该当表格）。</summary>
    internal bool IsUsable => ColumnCount >= 2 && RowCount >= 1;

    /// <summary>把文本行按当前分割线重新落格。</summary>
    internal void Refill(IReadOnlyList<OcrLine> lines)
        => Cells = GridTableFiller.Fill(RowLines, ColumnLines, lines);

    /// <summary>
    /// 直接换掉落格内容：导出要按"屏幕上的视图顺序"写，用一个不改分割线、
    /// 只换单元格的副本承载它 —— 原网格本身（分割线编辑的状态）不受影响。
    /// </summary>
    internal void SetCells(IReadOnlyList<IReadOnlyList<string>> cells)
        => Cells = [.. cells.Select(row => row.ToList())];

    /// <summary>清空成空骨架（识别还没回来时先给用户看结构）。</summary>
    internal void ClearCells()
    {
        Cells = [];
        for (var r = 0; r < RowCount; r++)
        {
            Cells.Add([.. Enumerable.Repeat(string.Empty, ColumnCount)]);
        }
    }

    internal static TableGrid? FromGridTable(GridTable table)
    {
        var grid = new TableGrid(table.RowLines, table.ColumnLines, "线格");
        return grid.IsUsable ? grid : null;
    }

    /// <summary>
    /// **没识别到表格时给用户的空骨架**：只有外框（1 行 × 1 列 = 图片边界），
    /// 用户自己在预览图上加/移/删分割线，再点「刷新」按他的线切分文字。
    /// 设计约定：XLSX 模式下"没找到表格"也要能用分割线。
    /// </summary>
    internal static TableGrid FromBounds(int width, int height)
    {
        var grid = new TableGrid([0, Math.Max(height, 1)], [0, Math.Max(width, 1)], "手工分割线");
        grid.ClearCells();
        return grid;
    }

    /// <summary>
    /// 合并过近的分割线（&lt;<see cref="IntervalMerge.MinLineSpacing"/>px）——避免出现 0 宽的列或
    /// 0 高的行（形状异常闸门的一部分）。阈值与逐列/逐行闸门共用同一个常量；
    /// **注意它不做"区间并集"**：这里是把过近的线**丢掉**（保留第一条），不是取并集/中位。
    /// </summary>
    private static List<double> Normalize(IEnumerable<double> values)
    {
        var result = new List<double>();
        foreach (var value in values.OrderBy(v => v))
        {
            if (result.Count > 0 && value - result[^1] < IntervalMerge.MinLineSpacing)
            {
                continue;
            }

            result.Add(value);
        }

        return result;
    }
}
