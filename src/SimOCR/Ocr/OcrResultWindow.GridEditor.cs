// OcrResultWindow 的 partial 拆分（分割线编辑）。
// 本文件承载网格线绘制 / 拖拽 / 增删 / 重编号；
// 类体是从 OcrResultWindow.xaml.cs 按连续区间搬来的，没有重写或重排任何成员。

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SimOCR.Diagnostics;
using SimOCR.Export;
using SimOCR.Settings;

namespace SimOCR.Ocr;

internal partial class OcrResultWindow
{
    // ------------------------------------------------- 分割线编辑（可编辑表格结构）

    private static readonly Brush GridLineStroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x4F, 0xC3, 0xF7));

    private void OnGridEditClick(object sender, RoutedEventArgs e) => SetGridEditMode(!_gridEditMode);

    private void SetGridEditMode(bool enabled)
    {
        if (enabled)
        {
            if (IsTextOnlyFormat)
            {
                StatusText.Text = "TXT 是纯文本模式，不支持分割线编辑 —— 需要表格请用 XLSX / DOCX / PDF 重新识别";
                return;
            }

            // 没识别到表格也能进：给一份只有外框的骨架，用户自己画线
            if (EnsureEditableGrid() is null)
            {
                StatusText.Text = "当前没有可编辑的表格结构 —— 先框选一块表格区域";
                return;
            }
        }

        // 与"编辑文本框"模式**互斥**：进入一个就结束另一个（按钮一亮一暗）
        if (enabled && _editMode)
        {
            SetEditMode(false);
        }

        _gridEditMode = enabled;
        GridLineLayer.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        HighlightModeButton(GridEditButton, enabled);
        UpdateGridButtons();

        if (enabled)
        {
            DrawGridLines();
            StatusText.Text = "编辑分割线：拖动线 = 移动 · 右键线 = 删除 · 空白处双击 = 新增列线（Shift+双击 = 新增行线）· 中键退出 · 改完点「刷新」";
        }
        else
        {
            GridLineLayer.Children.Clear();
        }
    }

    /// <summary>模式按钮的"亮/暗"：亮 = 该模式正在生效。</summary>
    private static void HighlightModeButton(Button button, bool active)
        => button.Background = active
            ? EditButtonActive
            : new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));

    /// <summary>
    /// 把 `名字(k)` 形式的列名按**存活列**重新编号：同一基名的列按出现顺序依次命名为
    /// `名字`、`名字(2)`……
    ///
    /// 用在哪：「删除全空列」之后。通用档的列名是"表头原文 + 序号"（如 `AMOUNT` / `AMOUNT(2)`），
    /// 而"只有货币符号"的那一列会被清空并删掉 —— 不重编号的话，活下来的那列会一直顶着
    /// `AMOUNT(2)`，看起来像"少了一列"。只处理带 `(数字)` 后缀的名字，其它列名一律不动。
    /// </summary>
    private static void RenumberOrdinalSuffixedLabels(List<string> labels)
    {
        var suffix = new Regex(@"^(?<b>.+)\((?<n>\d+)\)$", RegexOptions.CultureInvariant);
        var byBase = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < labels.Count; i++)
        {
            var match = suffix.Match(labels[i] ?? string.Empty);
            if (!match.Success)
            {
                continue;
            }

            var b = match.Groups["b"].Value;
            if (!byBase.TryGetValue(b, out var indexes))
            {
                indexes = [];
                byBase[b] = indexes;
            }

            indexes.Add(i);
        }

        foreach (var (b, indexes) in byBase)
        {
            var hasBare = labels.Any(l => string.Equals(l, b, StringComparison.Ordinal));
            var next = hasBare ? 2 : 1;
            foreach (var i in indexes.OrderBy(v => v))
            {
                labels[i] = next == 1 ? b : $"{b}({next})";
                next++;
            }
        }
    }

    private void DrawGridLines()
    {
        GridLineLayer.Children.Clear();
        // 重建线之后，旧的拖拽状态指向的是已经被丢掉的那些 Line —— 必须先清空
        EndGridDrag();
        if (CurrentGrid is null)
        {
            return;
        }

        for (var i = 0; i < CurrentGrid.ColumnLines.Count; i++)
        {
            GridLineLayer.Children.Add(MakeGridLine(true, i, CurrentGrid.ColumnLines[i]));
        }

        for (var i = 0; i < CurrentGrid.RowLines.Count; i++)
        {
            GridLineLayer.Children.Add(MakeGridLine(false, i, CurrentGrid.RowLines[i]));
        }
    }

    private Line MakeGridLine(bool isColumn, int index, double position)
    {
        var line = new Line
        {
            Stroke = GridLineStroke,
            StrokeThickness = 5 / _scale,
            Opacity = 0.75,
            Tag = (isColumn, index),
            Cursor = isColumn ? Cursors.SizeWE : Cursors.SizeNS,
        };

        if (isColumn)
        {
            line.X1 = line.X2 = position;
            line.Y1 = 0;
            line.Y2 = _image.PixelHeight;
        }
        else
        {
            line.Y1 = line.Y2 = position;
            line.X1 = 0;
            line.X2 = _image.PixelWidth;
        }

        line.MouseLeftButtonDown += OnGridLineMouseDown;
        line.MouseRightButtonDown += OnGridLineRightDown;
        return line;
    }

    private void OnGridLineMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Line { Tag: (bool isColumn, int index) })
        {
            BeginGridDrag(isColumn, index);
            e.Handled = true;
        }
    }

    /// <summary>开始拖拽某条分割线（Tag 里的下标 → 唯一的拖拽状态）。</summary>
    private void BeginGridDrag(bool isColumn, int index)
    {
        var line = GridLineLayer.Children.OfType<Line>()
            .FirstOrDefault(l => l.Tag is (bool c, int i) && c == isColumn && i == index);
        if (line is not null)
        {
            _gridDrag = (line, isColumn, index);
        }
    }

    private void OnGridLineRightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Line { Tag: (bool isColumn, int index) })
        {
            return;
        }

        DeleteGridLine(isColumn, index);
        e.Handled = true;
    }

    /// <summary>
    /// 删掉一条内部分割线（右键点线的数据链路）。
    /// 删线会重建全部 Line 元素（<see cref="DrawGridLines"/>），刚被拖的那条已经脱离视觉树，
    /// 所以这里先清拖拽状态 —— 否则后续 MouseMove 会拿旧下标去写分割线、拿旧 Line 去挪位置（幽灵线）。
    /// </summary>
    private bool DeleteGridLine(bool isColumn, int index)
    {
        if (CurrentGrid is not { } grid)
        {
            return false;
        }

        var lines = isColumn ? grid.ColumnLines : grid.RowLines;
        if (lines.Count <= 2 || index <= 0 || index >= lines.Count)
        {
            StatusText.Text = "至少要保留 2 条分割线（否则连 1 列/1 行都不剩）";
            return false;
        }

        lines.RemoveAt(index);
        EndGridDrag();

        // 删掉一条线之后，**旧的落格结果整份作废**：行列数已经变了，
        // 再把它留在屏幕上就是"看到的表 ≠ 导出的表"（实测：删到只剩 1 列时，屏幕上还是旧的 3 列）。
        // 干脆把预览清掉 + 折叠表格区，提示用户点「刷新」按新分割线重算。
        SetCurrentTable(TableUpdate.Clear, what: "删分割线");
        TableStatus.Text = $"已删除一条{(isColumn ? "列" : "行")}分割线 —— 点「刷新」按新分割线重算";
        DrawGridLines();
        StatusText.Text = TableStatus.Text;
        return true;
    }

    private void OnGridLayerMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        DragGridLineTo(e.GetPosition(GridLineLayer));
    }

    /// <summary>
    /// 把"正在拖的那条线"移到画布坐标 <paramref name="position"/>。
    /// 状态只有 <see cref="_gridDrag"/> 一份 —— 删线/重建/离开图层都会把它清掉，
    /// 因此这里不会再照着删除前的旧下标去写分割线（幽灵线）。
    /// </summary>
    private void DragGridLineTo(Point position)
    {
        if (_gridDrag is not { } drag || CurrentGrid is null)
        {
            return;
        }

        var lines = drag.IsColumn ? CurrentGrid.ColumnLines : CurrentGrid.RowLines;
        if (drag.Index <= 0 || drag.Index >= lines.Count - 1)
        {
            return; // 最外侧两条线不拖：它们定义表区范围
        }

        var value = drag.IsColumn ? position.X : position.Y;
        var lower = lines[drag.Index - 1] + 3;
        var upper = lines[drag.Index + 1] - 3;
        value = Math.Clamp(value, lower, Math.Max(lower, upper));
        lines[drag.Index] = value;

        if (drag.IsColumn)
        {
            drag.Line.X1 = drag.Line.X2 = value;
        }
        else
        {
            drag.Line.Y1 = drag.Line.Y2 = value;
        }
    }

    private void OnGridLayerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_gridDrag is null)
        {
            return;
        }

        EndGridDrag();
        StatusText.Text = "分割线已移动 —— 点「刷新」按新分割线重算";
    }

    /// <summary>清空分割线拖拽状态（删线 / 新增线 / 离开图层 / 重建线 都必须调）。</summary>
    private void EndGridDrag() => _gridDrag = null;

    /// <summary>空白处双击 = 新增分割线（默认列线；按住 Shift 为行线）。</summary>
    private void OnGridLayerBackgroundDown(object sender, MouseButtonEventArgs e)
    {
        if (!_gridEditMode || CurrentGrid is null || e.OriginalSource is not Canvas || e.ClickCount < 2)
        {
            return;
        }

        var position = e.GetPosition(GridLineLayer);
        var isColumn = (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift;
        TryAddLine(isColumn, isColumn ? position.X : position.Y);
        e.Handled = true;
    }

    /// <summary>在两条已有线之间插入一条新分割线（双击新增与自检共用同一段逻辑）。</summary>
    private bool TryAddLine(bool isColumn, double value)
    {
        if (CurrentGrid is not { } grid)
        {
            return false;
        }

        var lines = isColumn ? grid.ColumnLines : grid.RowLines;
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            if (value > lines[i] + 3 && value < lines[i + 1] - 3)
            {
                lines.Insert(i + 1, value);
                DrawGridLines();
                StatusText.Text = $"已新增一条{(isColumn ? "列" : "行")}分割线 —— 点「刷新」按新分割线重算";
                return true;
            }
        }

        StatusText.Text = "新增分割线要落在两条已有分割线之间（且间距 ≥3px）";
        return false;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshGrid(manual: true);

    /// <summary>
    /// 「刷新」按钮：
    ///   * 当前显示的是**增强识别的语义表** → **重新运行通用识别**；
    ///   * 否则 → 按当前（可能被手工改过的）分割线重新落格。
    /// </summary>
    private void RefreshGrid(bool manual)
    {
        if (IsTextOnlyFormat)
        {
            StatusText.Text = "TXT 模式只导出文本 —— 需要表格请用 XLSX / DOCX / PDF 重新识别";
            return;
        }

        if (CurrentSemantic is not null)
        {
            RerunGeneralRecognition();
            return;
        }

        if (CurrentGrid is not { } grid)
        {
            // 没识别到表格时按「刷新」= **重新运行通用识别**
            RerunGeneralRecognition();
            return;
        }

        // 自动检测出来的结构仍走严格闸门；用户手工画过的线走宽一档的闸门
        //（"不得输出形状不正常的表格"针对自动结果，手工线由用户负责，
        //  这里只拦退化的 0 宽/0 高与"一条内部线都没有"）
        var manualGrid = grid.Source == "手工分割线";
        if (manualGrid ? !FastTableBuilder.IsSaneForManual(grid) : !FastTableBuilder.IsSane(grid))
        {
            StatusText.Text = manualGrid
                ? "还没有可用的分割线 —— 至少加一条列线或行线（空白处双击加列线 / Shift+双击加行线），再点「刷新」"
                : "分割线不合法（出现 0 宽/0 高的格子或行列数异常）—— 请调整后再刷新";
            return;
        }

        if (manual)
        {
            grid.Source = "手工分割线";
        }

        // 落格统一经 TableState 的漏斗（它若就是"当前表"，顺带换掉只读视图包装）
        _table.RefillCurrent(_lines);

        // **唯一状态入口**：一次调用同时改"当前表"、屏幕视图与按钮可用性，并校验不变量
        //（走 syncText：文本比识别行落后一步时，导出会退回"编辑后的文本"口径而与屏幕分叉）。
        SetCurrentTable(TableUpdate.General, grid, syncText: true, what: "刷新");
        TableShape.Text = $"{grid.RowCount} 行 × {grid.ColumnCount} 列";
        // 状态栏只说用户做了什么、得到几行几列；"哪条检测路径算出来的"属于排查信息，进日志。
        TableStatus.Text = manualGrid
            ? $"已按你画的线切分：{grid.RowCount} 行 × {grid.ColumnCount} 列"
            : $"已按当前分割线重新切分：{grid.RowCount} 行 × {grid.ColumnCount} 列";
        _log.Info($"按分割线切分（{grid.Source}，手工={manualGrid}）：{grid.RowCount}x{grid.ColumnCount}");
    }

    /// <summary>
    /// 重新运行**通用识别**：重新在像素上找线格，找不到就用文本框位置做"空白对齐"；
    /// 结果覆盖当前语义表（增强识别的成果退役），并用当前 OCR 行重新落格。
    /// </summary>
    private void RerunGeneralRecognition()
    {
        var watch = Stopwatch.StartNew();
        List<GridTable> tables;
        try
        {
            tables = LineGridDetector.Detect(_image);
        }
        catch (LineGridFailureException ex)
        {
            // 故障分支 —— 与"这张图没有网格（走空白对齐）"分开记账；
            // **返回值不变**：照样回退到空白对齐 / 退役当前表，用户可见结果与改动前一致。
            _gridDetectFailures++;
            _log.Warn($"  [线格] 检测故障（不是「没有网格」）：{ex.Message}", ex);
            tables = [];
        }
        // 不再是 tables[0]（那是"横线最多的簇"），而是最适合的那一张；见 LineGridDetector.Best。
        var grid = LineGridDetector.Best(tables) is { } best ? TableGrid.FromGridTable(best) : null;
        var source = "线格";
        if (grid is null)
        {
            grid = FastTableBuilder.FromLines(_lines);
            source = "空白对齐";
        }

        if (grid is null)
        {
            // 还原不出来：当前表退役（显示/导出保持一致地回到"没有表"）
            SetCurrentTable(TableUpdate.Clear, what: "刷新(重跑通用识别)");
            TableShape.Text = string.Empty;
            TableStatus.Text = "重新运行通用识别：没能还原出行列结构 —— 可再点「增强识别」";
            _log.Warn("刷新：重新运行通用识别失败（没有可用行列结构）");
            return;
        }

        // 这是一份**全新的**结构（还没进过 TableState），所以在这里落格不算"绕过 TableState 改当前表"：
        // 它由紧接着的 SetCurrentTable 一次性发布出去。
        grid.Refill(_lines);

        // **必须重新评估按钮可用性**：增强识别把当前表换成语义表 → 「编辑分割线」被禁用；
        // 这里已经恢复出通用表格，若不给它重新启用，用户就会卡在"灰着点不动"的状态。
        // 唯一状态入口一次做完（含按钮重算）；文本落后一步会与屏幕分叉，所以走 syncText。
        SetCurrentTable(TableUpdate.General, grid, syncText: true, what: "刷新(重跑通用识别)");
        if (_gridEditMode)
        {
            DrawGridLines();
        }

        TableShape.Text = $"{grid.RowCount} 行 × {grid.ColumnCount} 列";
        // 耗时与检测路径只进日志：状态栏是给用户看结果的，不是性能面板。
        TableStatus.Text = $"已重新识别表格：{grid.RowCount} 行 × {grid.ColumnCount} 列"
                           + " —— 分割线不对就点「编辑分割线」";
        _log.Info($"刷新：重新运行通用识别（{source}）→ {grid.RowCount}x{grid.ColumnCount}，{watch.ElapsedMilliseconds}ms");
    }

    // ------------------------------------------------- 定向重认（增强识别的"放大"）

    /// <summary>
    /// **定向重认**：按快速识别的行列结构逐格检查，只对"空的 / 数值列里读不出数"的格子
    /// 裁剪出来用**增强档参数**（短边自适应，等效放大）重新识别一次。
    ///
    /// 为什么这样做而不是整图再放大一遍：整图放大已被实测证伪（救回 3 处、把 279 读成 27），
    /// 而"按单元格裁剪 + 只补有问题的格子"能把代价限定在少数格子上，
    /// 且这里不覆盖已有结果 —— 只有读出来能解析成数值时才采纳。
    /// </summary>
    /// <param name="structure">用哪份网格结构去裁剪单元格。**显式传参而不是读 <c>_grid</c>**：
    /// 增强识别期间语义表还没定论，此时不能把"仅供重认用的结构"写进 <c>_grid</c>，
    /// 否则一旦语义表没还原出来，就会出现"界面显示语义表、导出/按钮却按网格表走"的不一致。</param>
    private async Task<int> TargetedReOcrAsync(TableGrid structure, int maxCells = 30, int maxMs = 20000)
    {
        if (structure.ColumnCount < 2 || structure.RowCount < 1)
        {
            return 0;
        }

        // 这份结构可能**就是**当前表（见调用点的来源链：CurrentGrid → _lastGeneralGrid → 现推），
        // 所以落格也走 TableState 的漏斗 —— 它是当前表时会顺带换掉只读视图包装，避免包装指向旧列表。
        _table.Fill(structure, _lines);
        var numeric = NumericColumns(structure);
        if (!numeric.Any(v => v))
        {
            return 0;
        }

        var watch = Stopwatch.StartNew();
        var done = 0;
        for (var r = 0; r < structure.RowCount && done < maxCells && watch.ElapsedMilliseconds < maxMs; r++)
        {
            for (var c = 0; c < structure.ColumnCount && done < maxCells && watch.ElapsedMilliseconds < maxMs; c++)
            {
                if (!numeric[c])
                {
                    continue;
                }

                var text = r < structure.Cells.Count && c < structure.Cells[r].Count ? structure.Cells[r][c] : string.Empty;
                var (cleaned, _) = CellValidator.CleanNumeric(CellValidator.Normalize(text));
                if (text.Length > 0 && FieldSemantics.TryParseNumber(cleaned, out _))
                {
                    continue; // 这一格已经读对了，不用重认
                }

                var x0 = (int)Math.Max(0, Math.Floor(structure.ColumnLines[c]) + 1);
                var x1 = (int)Math.Min(_image.PixelWidth, Math.Ceiling(structure.ColumnLines[c + 1]) - 1);
                var y0 = (int)Math.Max(0, Math.Floor(structure.RowLines[r]) + 1);
                var y1 = (int)Math.Min(_image.PixelHeight, Math.Ceiling(structure.RowLines[r + 1]) - 1);
                if (x1 - x0 < 6 || y1 - y0 < 6)
                {
                    continue;
                }

                done++;
                try
                {
                    var region = new Int32Rect(x0, y0, x1 - x0, y1 - y0);
                    // 短边自适应的目标：只把小格子放大到 4 倍左右（96~1536 之间），
                    // 否则 57px 高的小格会被放大 27 倍 —— 实测单格要 4.9 秒。
                    var limitSideLen = Math.Clamp(region.Height * 4, 128, 1536);
                    var part = await _recognition.RecognizeRegionAsync(_image, region, default, OcrProfile.Enhance, limitSideLen);
                    foreach (var line in part.Lines)
                    {
                        var candidate = CellValidator.CleanNumeric(CellValidator.Normalize(line.Text ?? string.Empty));
                        if (candidate.Value.Length == 0 || !FieldSemantics.TryParseNumber(candidate.Value, out _))
                        {
                            continue;
                        }

                        // 与已有行重叠就替换（新读数能解析成数值，更可信），否则作为补充行加入
                        var index = _lines.FindIndex(existing => IntersectionOverUnion(existing, line) > 0.3);
                        if (index >= 0)
                        {
                            _lines[index] = line;
                        }
                        else
                        {
                            _lines.Add(line);
                        }

                        break;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"定向重认失败（第 {r + 1} 行第 {c + 1} 列）：{ex.Message}");
                }
            }
        }

        _table.Fill(structure, _lines);
        _log.Info($"定向重认：检查并重认 {done} 格（上限 {maxCells} 格 / {maxMs}ms，实用 {watch.ElapsedMilliseconds}ms）");
        return done;
    }

    /// <summary>快速表格里哪些列是数值列（列内 ≥70% 的非空单元格能解析成数值）。</summary>
    private static List<bool> NumericColumns(TableGrid grid)
    {
        var result = new List<bool>(grid.ColumnCount);
        for (var c = 0; c < grid.ColumnCount; c++)
        {
            var total = 0;
            var numeric = 0;
            for (var r = 0; r < grid.RowCount; r++)
            {
                var raw = r < grid.Cells.Count && c < grid.Cells[r].Count ? grid.Cells[r][c] : string.Empty;
                var text = CellValidator.Normalize(raw);
                if (text.Length == 0)
                {
                    continue;
                }

                total++;
                var (cleaned, _) = CellValidator.CleanNumeric(text);
                if (FieldSemantics.TryParseNumber(cleaned, out _))
                {
                    numeric++;
                }
            }

            result.Add(total >= 2 && (double)numeric / total >= 0.7);
        }

        return result;
    }

}
