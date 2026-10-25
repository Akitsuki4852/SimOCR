using SimOCR.Ocr;

namespace SimOCR.Export;

/// <summary>
/// 把 OCR 文本行填进线格单元格（对应 Camelot/pdfplumber 的"字符落格"一步）。
/// 规则：**中心点落在哪个格子就归哪个格子**（pdfplumber 的做法）；中心点落在格外时退回**最近**的格子
/// （比"重叠最大"更稳：边界上的一行文字常有半个字压在线外）。
/// 一个格子里的多行文字按阅读顺序用换行拼接（Excel 里的多行单元格）。
/// </summary>
internal static class GridTableFiller
{
    internal static List<List<string>> Fill(GridTable table, IReadOnlyList<OcrLine> lines)
        => FillCore(table.X0, table.Y0, table.X1, table.Y1, table.RowLines, table.ColumnLines, lines);

    /// <summary>
    /// 按**任意**行列分割线落格（供可编辑网格 <see cref="TableGrid"/> 用）：
    /// 表区取第一条/最后一条线，因此用户拖动分割线后重算的结果与线格路径完全同源。
    /// </summary>
    internal static List<List<string>> Fill(
        IReadOnlyList<double> rowLines,
        IReadOnlyList<double> columnLines,
        IReadOnlyList<OcrLine> lines)
        => rowLines.Count < 2 || columnLines.Count < 2
            ? []
            : FillCore(columnLines[0], rowLines[0], columnLines[^1], rowLines[^1], rowLines, columnLines, lines);

    private static List<List<string>> FillCore(
        double boundsX0,
        double boundsY0,
        double boundsX1,
        double boundsY1,
        IReadOnlyList<double> rowLines,
        IReadOnlyList<double> columnLines,
        IReadOnlyList<OcrLine> lines)
    {
        var rowCount = Math.Max(rowLines.Count - 1, 0);
        var columnCount = Math.Max(columnLines.Count - 1, 0);
        var buckets = new List<List<string>[]>(rowCount);
        for (var r = 0; r < rowCount; r++)
        {
            var row = new List<string>[columnCount];
            for (var c = 0; c < columnCount; c++)
            {
                row[c] = [];
            }

            buckets.Add(row);
        }

        foreach (var line in lines.OrderBy(l => l.Y0).ThenBy(l => l.X0))
        {
            var text = line.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var (row, column) = Locate(boundsX0, boundsY0, boundsX1, boundsY1, rowLines, columnLines, line);
            if (row < 0)
            {
                continue;
            }

            // **一行文字横向压了多列时按字符位置切开**：
            // 快速识别的检测框常常把相邻两列的字符并成一个框（"被聚合的文本框"），
            // 用户手工把列线画在中间之后，这一行就该分给两列，而不是整行归"中心点那列"。
            // 行方向**不切** —— 跨行置中的文字（合并单元格里竖排居中的标签）保持整块，
            // 只按中心点落到某一行，绝不把字符从中间切开。
            if (columnCount > 1)
            {
                foreach (var (pieceColumn, piece) in SplitByColumns(text, line.X0, line.X1, columnLines))
                {
                    if (pieceColumn >= 0 && pieceColumn < columnCount)
                    {
                        buckets[row][pieceColumn].Add(piece);
                    }
                }
            }
            else if (column >= 0)
            {
                buckets[row][column].Add(text);
            }
        }

        var result = new List<List<string>>(rowCount);
        for (var r = 0; r < rowCount; r++)
        {
            var row = new List<string>(columnCount);
            for (var c = 0; c < columnCount; c++)
            {
                row.Add(string.Join("\n", buckets[r][c]));
            }

            result.Add(row);
        }

        return result;
    }

    /// <summary>找这一行文字所属的 (行, 列)：先看中心点，再退回最近格子。</summary>
    private static (int Row, int Column) Locate(
        double boundsX0,
        double boundsY0,
        double boundsX1,
        double boundsY1,
        IReadOnlyList<double> rows,
        IReadOnlyList<double> columns,
        OcrLine line)
    {
        var cx = (line.X0 + line.X1) / 2.0;
        var cy = (line.Y0 + line.Y1) / 2.0;

        // 中心点在表区之外（表格上/下方的正文说明、标题）一律不收 ——
        // 否则"最近格"兜底会把表格外面的文字塞进第一行（实测）。
        const double outside = 4.0;
        if (cx < boundsX0 - outside || cx > boundsX1 + outside || cy < boundsY0 - outside || cy > boundsY1 + outside)
        {
            return (-1, -1);
        }

        var row = IndexAt(rows, cy);
        var column = IndexAt(columns, cx);
        if (row >= 0 && column >= 0)
        {
            return (row, column);
        }

        // 中心点落在表格内部的缝上（压线/超边）：取最近的一格
        var rowCount = Math.Max(rows.Count - 1, 0);
        var columnCount = Math.Max(columns.Count - 1, 0);
        var bestRow = Math.Max(0, Math.Min(rowCount - 1, Nearest(rows, cy)));
        var bestColumn = Math.Max(0, Math.Min(columnCount - 1, Nearest(columns, cx)));
        return rowCount > 0 && columnCount > 0 ? (bestRow, bestColumn) : (-1, -1);
    }

    /// <summary>位置落在第几段（线数组的相邻两项构成一段）；不在任何段内返回 -1。</summary>
    private static int IndexAt(IReadOnlyList<double> boundaries, double value)
    {
        for (var i = 0; i + 1 < boundaries.Count; i++)
        {
            if (value >= boundaries[i] && value <= boundaries[i + 1])
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>最近的一段（按段中心距离）。</summary>
    private static int Nearest(IReadOnlyList<double> boundaries, double value)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i + 1 < boundaries.Count; i++)
        {
            var center = (boundaries[i] + boundaries[i + 1]) / 2.0;
            var distance = Math.Abs(center - value);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// 把一行文字按**字符估计位置**分给各列：每个字符取估计区间的中心，中心落在哪列就归哪列 ——
    /// 因此**永远不会把一个字符切成两半**。
    ///
    /// "检查是否有空位"：当分界正好落在两个**非空格**字符之间时，先在附近（±3 字）找空格，
    /// 把切点挪到空格处；找不到空格就保持按中心分配（宁可带走半个词，也不切字符）。
    /// </summary>
    internal static List<(int Column, string Text)> SplitByColumns(
        string text,
        double x0,
        double x1,
        IReadOnlyList<double> columns)
    {
        var columnCount = Math.Max(columns.Count - 1, 0);
        if (columnCount <= 1 || text.Length == 0 || x1 <= x0)
        {
            var only = IndexAt(columns, (x0 + x1) / 2.0);
            return [(Math.Clamp(only < 0 ? 0 : only, 0, Math.Max(columnCount - 1, 0)), text)];
        }

        var charWidth = (x1 - x0) / text.Length;
        var assignment = new int[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var center = x0 + ((i + 0.5) * charWidth);
            var column = IndexAt(columns, center);
            if (column < 0)
            {
                column = Math.Clamp(Nearest(columns, center), 0, columnCount - 1);
            }

            assignment[i] = column;
        }

        // 空隙优先：分界落在字符内部时，尽量挪到附近的空格
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (assignment[i] == assignment[i + 1])
            {
                continue;
            }

            if (char.IsWhiteSpace(text[i]) || char.IsWhiteSpace(text[i + 1]))
            {
                continue;
            }

            var moved = false;
            for (var k = i + 1; k <= Math.Min(text.Length - 2, i + 3); k++)
            {
                if (char.IsWhiteSpace(text[k]))
                {
                    for (var j = i + 1; j <= k; j++)
                    {
                        assignment[j] = assignment[i];
                    }

                    moved = true;
                    break;
                }
            }

            if (moved)
            {
                continue;
            }

            for (var k = i; k >= Math.Max(0, i - 3); k--)
            {
                if (char.IsWhiteSpace(text[k]))
                {
                    for (var j = k + 1; j <= i; j++)
                    {
                        assignment[j] = assignment[i + 1];
                    }

                    break;
                }
            }
        }

        var pieces = new List<(int Column, string Text)>();
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i < text.Length && assignment[i] == assignment[start])
            {
                continue;
            }

            var piece = text[start..i].Trim();
            if (piece.Length > 0)
            {
                pieces.Add((assignment[start], piece));
            }

            start = i;
        }

        return pieces.Count > 0 ? pieces : [(assignment[0], text)];
    }
}
