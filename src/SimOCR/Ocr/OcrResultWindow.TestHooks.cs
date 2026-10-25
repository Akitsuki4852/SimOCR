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

/// <summary>
/// <see cref="OcrResultWindow"/> 的自检 / 验收钩子（第一步：按职责拆 partial，零行为变更）。
/// 这里只放 <c>Test*</c> 成员与它们专用的工具；生产成员仍留在 OcrResultWindow.xaml.cs。
/// </summary>
internal partial class OcrResultWindow
{
    // 便于自检/调试
    internal int LineCount => _lines.Count;

    internal bool EditModeEnabled => _editMode;

    /// <summary>自检用：增强识别的最终表格（做完数值/格式校验的内容），Tab 分列；首行是列名。
    /// 语义表已经过期（后来删过框/补过识别）时返回 null —— 它不再是导出源。</summary>
    internal string? TestEnhancedTsv()
    {
        if (CurrentSemantic is not { } semantic || !_semanticMatchesLines)
        {
            return null;
        }

        var layout = semantic.Layout;
        var header = "# 列名\t" + string.Join("\t", layout.ColumnLabels);
        return header + "\n" + string.Join("\n", layout.Cells.Select(r => string.Join("\t", r)));
    }

    /// <summary>自检用：增强识别表格的形状（行×列）；语义表过期时返回 null。</summary>
    internal (int Rows, int Columns)? TestEnhancedShape()
        => CurrentSemantic is { } semantic && _semanticMatchesLines
            ? (semantic.Layout.RowCount, semantic.Layout.ColumnCount)
            : null;

    /// <summary>测试钩子：等价于用户点「增强识别」（走竖带补充 + 语义列）。</summary>
    internal void TestEnhance() => OnEnhanceClick(this, new RoutedEventArgs());

    /// <summary>自检用：进入/退出「编辑分割线」模式。</summary>
    internal void TestSetGridEditMode(bool enabled) => SetGridEditMode(enabled);

    /// <summary>自检用：等价于用户点「刷新」（按当前分割线重算）。</summary>
    internal void TestRefreshGrid()
    {
        _log.Info($"  [刷新] 前：{_table.Describe()} 屏幕 {ScreenRows().Count} 行");
        RefreshGrid(manual: true);
        _log.Info($"  [刷新] 后：{_table.Describe()} 屏幕 {ScreenRows().Count} 行");
    }

    private static string LastRowOf(TableGrid? grid)
        => grid is not null && grid.Cells.Count > 0 ? string.Join(" | ", grid.Cells[^1]) : "(空)";

    /// <summary>自检用：当前通用表格的形状与来源；没有表格时返回 null。</summary>
    internal (int Rows, int Columns, string Source)? TestGridShape()
        => CurrentGrid is { } grid ? (grid.RowCount, grid.ColumnCount, grid.Source) : null;

    // 0.10.5 删掉的零引用钩子：TestGridTsv / TestMoveColumnLine / TestText / TestAddLine / TestSaveButtonVisible。

    internal void TestSetEditMode(bool enabled) => SetEditMode(enabled);

    /// <summary>自检用：强制显示/隐藏右下角悬停工具条（正常是靠鼠标移入预览图触发）。</summary>
    internal void TestShowPreviewToolbar(bool visible) => ShowPreviewToolbar(visible);

    /// <summary>自检用：替代点击磁盘按钮 —— 直接存到默认路径，返回写出的文件路径。</summary>
    internal string? TestSaveToDefaultPath() => ExportToDefaultPath();

    internal void TestSetText(string text) => TextHost.Text = text;

    /// <summary>自检用：悬停工具条里"保存（磁盘）"按钮中心的屏幕坐标。</summary>
    internal Point TestSaveButtonCenterScreen()
        => SaveButton.PointToScreen(new Point(SaveButton.ActualWidth / 2, SaveButton.ActualHeight / 2));

    /// <summary>自检用：屏幕坐标取用口（配合 InputSimulator 打真实鼠标事件）。</summary>
    internal Point TestEnhanceButtonCenterScreen()
        => EnhanceButton.PointToScreen(new Point(EnhanceButton.ActualWidth / 2, EnhanceButton.ActualHeight / 2));

    internal Point TestGridEditButtonCenterScreen()
        => GridEditButton.PointToScreen(new Point(GridEditButton.ActualWidth / 2, GridEditButton.ActualHeight / 2));

    internal Point TestRefreshButtonCenterScreen()
        => RefreshButton.PointToScreen(new Point(RefreshButton.ActualWidth / 2, RefreshButton.ActualHeight / 2));

    /// <summary>自检用：表格区（表头 + DataGrid）当前是否可见。</summary>
    internal bool TestTablePanelVisible => TableHost.Visibility == Visibility.Visible;

    /// <summary>线格检测**故障**次数（与"没找到网格"分开计数；故障时的回退行为不变）。</summary>
    internal int TestGridDetectFailures => _gridDetectFailures;

    /// <summary>自检用：增强识别按钮是否可用（TXT 模式应为 false）。</summary>
    internal bool TestEnhanceEnabled => EnhanceButton.IsEnabled;

    /// <summary>自检用：当前是否 TXT 纯文本模式。</summary>
    internal bool TestTextOnlyFormat => IsTextOnlyFormat;

    /// <summary>自检用：分割线编辑模式是否打开。</summary>
    internal bool TestGridEditMode => _gridEditMode;

    /// <summary>自检用：「编辑分割线」按钮当前是否可点（回归用：增强后点刷新必须重新可用）。</summary>
    internal bool TestGridEditEnabled => GridEditButton.IsEnabled;

    /// <summary>
    /// 自检/验收用：**不变式**——「界面显示的表」「导出的表」「分割线按钮状态」必须自洽。
    ///
    /// 起这条检查进了生产路径（<see cref="VerifyTableState"/> 在每次状态变更后调用，
    /// 破坏时写 <c>StateInvariantBroken</c> 日志 + <c>Debug.Assert</c>）。
    ///
    /// 生产那一跳只剩 O(1) 的结构性检查（<see cref="IsTableStateConsistent"/>），
    /// 而这里把"逐格比对导出内容"的彻底版一起做**——两者合起来与 时代的判据**完全等价**，
    /// 只是不再让出货路径替测试付这份导出管线的代价：
    ///   * <see cref="IsTableStateConsistent"/>：模式/字段/按钮/TXT 规则 + 屏幕与导出同源（常数时间）；
    ///   * <see cref="IsScreenExportConsistent"/>：跑一次导出管线并逐格比（只在自检/验收路径按需跑）；
    ///   * <c>_stateInvariantBroken</c>：生产路径曾经抓到过的破坏（粘性标记）。
    /// 这个钩子保留给自检断言读同一套规则，避免"生产一套、测试另一套"。
    /// </summary>
    internal bool TestStateConsistent()
        => IsTableStateConsistent() && IsScreenExportConsistent() && !_stateInvariantBroken;

    /// <summary>自检用：导出内容某一列的值（按行顺序；格内换行按导出口径压成空格）。</summary>
    internal IReadOnlyList<string> TestExportColumn(int column)
    {
        var cells = ExportCells();
        return [.. cells.Select(r => (column < r.Count ? r[column] : string.Empty).Trim())];
    }

    /// <summary>自检用：屏幕上某一列的值（按行顺序；归一与 <see cref="TestExportColumn"/> 一致）。</summary>
    internal IReadOnlyList<string> TestScreenColumn(int column)
        => [.. Flattened(ScreenRows()).Select(r => (column < r.Count ? r[column] : string.Empty).Trim())];

    // -------------------------------------------------- 诊断取值口

    /// <summary>
    /// 这一组"两串单元格的差异报告"工具已抽到测试侧。
    ///
    /// 实现只有一份：`tests/SimOCR.Tests/PureLogic/TableTsvDiff.cs`，由 `SimOCR.csproj` 的
    /// <c>&lt;Compile Include="..\..\tests\SimOCR.Tests\PureLogic\*.cs"&gt;</c> 链接进本程序集
    /// （与 0.10.3 落地的纯逻辑真源**完全同一套接法**），于是
    /// ① xUnit `[Fact]`、② 无 testhost 的控制台运行器 `SimOCR.Tests.exe`、③ 生产/自检诊断
    /// 读的是同一份实现 —— 不是两份。
    ///
    /// 原来挂在这里的 4 个钩子（`TestDiffDetails` / `TestViewCellsKey` / `TestExportedCellsKey` /
    /// `TestScreenExportDiff`）已删：它们是**工具**不是**断言**，挂在生产类上只会让 `Test*` 成员继续膨胀。
    /// 下面两个是**诊断取值口**（不是测试钩子）：自检、验收与生产的不变量诊断都从这里取数据，
    /// 再交给 <see cref="TableTsvDiff"/> 算差异。
    /// </summary>

    /// <summary>屏幕上的表（按导出口径归一后的 (行, 列)）——诊断取值口。</summary>
    internal IReadOnlyList<IReadOnlyList<string>> ScreenCellRows
        => [.. Flattened(ScreenRows()).Select(r => (IReadOnlyList<string>)r)];

    /// <summary>导出真正会写出去的那张表（同 <see cref="ScreenCellRows"/> 的口径）——诊断取值口。</summary>
    internal IReadOnlyList<IReadOnlyList<string>> ExportedCellRows
        => [.. ExportCells().Select(r => (IReadOnlyList<string>)r)];

    /// <summary>
    /// "屏幕 vs 导出"的分叉点，一行说清：走了哪条导出路径 / 网格·视图·导出·屏幕各几行 /
    /// 第一个不同的格子。断言失败时没这条就只能靠猜（实测第一次就吃了这个亏）——
    /// 它是**诊断**，不是判据（逐格判据在 <see cref="IsScreenExportConsistent"/>）。
    /// </summary>
    internal string DescribeScreenExportFork()
    {
        var screen = Flattened(ScreenRows());
        var exported = ExportCells();
        var parts = new List<string>
        {
            $"导出路径={TestExportPath()}",
            $"模式={_table.Mode}",
            $"当前表={_table.Describe()}",
            $"网格行数={CurrentGrid?.Cells.Count ?? -1} 视图行数={_table.View.Count} 导出行数={exported.Count} 屏幕行数={screen.Count}",
            $"网格末行={LastRowOf(CurrentGrid)}",
            $"视图末行={(_table.View.Count > 0 ? string.Join(" | ", _table.View[^1]) : "(空)")}",
            $"导出末行={(exported.Count > 0 ? string.Join(" | ", exported[^1]) : "(空)")}",
            $"屏幕末行={(screen.Count > 0 ? string.Join(" | ", screen[^1]) : "(空)")}",
            $"ItemsSource 同一实例={ReferenceEquals(TablePreview.ItemsSource, _table.View)}",
            $"文本框==文档纯文本：{TextHost.Text == BuildDocument().ToPlainText()}",
        };

        // 逐格分叉点交给共享真源（一份实现，三条消费路径）
        parts.AddRange(TableTsvDiff.ForkDetail(screen, exported));
        return string.Join("；", parts);
    }

    /// <summary>自检用（逐格判据）：屏幕上的表 == 导出会写出去的那张表。</summary>
    internal bool TestScreenExportConsistent() => IsScreenExportConsistent();

    /// <summary>自检用：当前导出走的是哪条取值路径（"唯一取值入口"要能被外部看见）。</summary>
    internal string TestExportPath()
    {
        var document = BuildDocument();
        var textUntouched = (TextHost.Text ?? string.Empty) == document.ToPlainText();
        return _table.Mode switch
        {
            TableMode.Semantic when CurrentSemantic is not null && _semanticMatchesLines => "语义表",
            TableMode.General when CurrentGrid is not null && _table.View.Count > 0 && textUntouched => "通用网格表（屏幕顺序）",
            _ when !textUntouched => "编辑后的文本",
            _ => "文档",
        };
    }

    /// <summary>导出内容按 (行, 列) 拆开（见 <see cref="ExportCells(OcrExportContent)"/>）。</summary>
    private List<List<string>> ExportCells() => ExportCells(DeriveTable());

    /// <summary>
    /// 导出内容按 (行, 列) 拆开；行内换行当**同一格的多行**（与屏幕上的单元格一一对应）。
    /// 导出器把格内换行压成空格（见 <c>OcrExportContent.FromGrid</c>），所以还要还原成空格。
    ///
    /// 接受**已经派生好的那一份**内容，避免"判据一次派生、诊断再一次派生"——
    /// 屏幕与导出本来就是同一份来源，重复派生只会让两边更容易分叉。
    /// </summary>
    private static List<List<string>> ExportCells(OcrExportContent content)
    {
        var rows = new List<List<string>>();
        // 去掉结尾的空格/换行，避免多出一行"空行"（导出文本末尾就是这个样子）
        foreach (var line in content.Text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
        {
            rows.Add([.. line.Split('\t').Select(c => c.Replace('\n', ' ').Replace('\r', ' '))]);
        }

        return rows;
    }

    /// <summary>屏幕上的单元格按导出口径归一（格内换行 → 空格）。</summary>
    private static List<List<string>> Flattened(IEnumerable<IReadOnlyList<string>> rows)
        => [.. rows.Select(r => r.Select(c => c.Replace('\n', ' ').Replace('\r', ' ')).ToList())];

    // -------------------------------------------------- 回归探针

    /// <summary>自检用：**屏幕上那张表的内容**（Tab 分列、换行分行）——即 DataGrid 的 ItemsSource，
    /// 而不是 <c>_grid.Cells</c>。的断言用它区分"屏幕顺序"与"导出顺序"。</summary>
    internal string? TestTableScreenTsv()
    {
        var rows = ScreenRows();
        return rows.Count == 0
            ? null
            : string.Join("\n", rows.Select(r => string.Join('\t', r)));
    }

    /// <summary>自检用：当前屏幕表格的行数。</summary>
    internal int TestTableScreenRowCount => ScreenRows().Count;

    /// <summary>自检用：当前表格的列数（没有表格时为 0）。</summary>
    internal int TestColumnCount => CurrentTableInfo?.Columns ?? 0;

    /// <summary>自检用：第 index 个识别框的文字。</summary>
    internal string TestBoxText(int index)
        => index >= 0 && index < _lines.Count ? _lines[index].Text : string.Empty;

    /// <summary>自检用：识别结果的纯文本（与文本框内容应当一致）。</summary>
    internal string TestPlainText() => BuildDocument().ToPlainText();

    /// <summary>
    /// 自检用：**走真实的"右键点框删行"路径** —— 它内部按 <c>Polygon.Tag</c> 找人机一致的
    /// 识别行下标（<see cref="RebuildBoxes"/> 与框一一对应），比测试自己按表格行号去猜可靠得多。
    /// 返回删掉的那一行的文字（没删成返回 null）。
    /// </summary>
    internal string? TestRightClickDeleteBox(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _boxShapes.Count)
        {
            return null;
        }

        // 与真实右键同一条链路：从 Polygon.Tag 取下标 + 要求处于编辑模式
        if (!_editMode || _busy || _boxShapes[lineIndex].Tag is not int index
            || index < 0 || index >= _lines.Count)
        {
            return null;
        }

        var text = _lines[index].Text;
        return DeleteLineAt(index) ? text : null;
    }

    /// <summary>自检用：当前的"框 → 文本"映射（判据）。</summary>
    internal IReadOnlyList<(int Start, int Length)> TestSpans() => _spans;

    /// <summary>
    /// 自检用：**删掉表格的一行**（走"右键点框删行"同一条数据链路），
    /// 返回删完之后**当前表的落格结果**（与导出同源的 <c>TableState.View</c>）。
    ///
    /// 为什么返回落格结果而不是"删没删成"的判据是"**删框之后导出的表 == 这台机器上真实的落格结果**"。
    /// 修复前 <c>_grid.Cells</c> 是删除前的旧投影，于是"屏幕与落格都变了、导出还是旧的"；
    /// 修复后两者是同一份（<c>TableState.View</c> 就是 <c>Grid.Cells</c> 的**只读包装**）。
    /// </summary>
    internal IReadOnlyList<IReadOnlyList<string>> TestDeleteGridRowAndGetView(int index)
    {
        if (!_editMode)
        {
            SetEditMode(true); // 右键删框只在编辑文本框模式下生效（与用户操作一致）
        }

        var boxIndex = Math.Clamp(index, 0, Math.Max(0, _boxShapes.Count - 1));
        TestRightClickDeleteBox(boxIndex);
        return [.. _table.View.Select(r => (IReadOnlyList<string>)r)];
    }

    private List<IReadOnlyList<string>> ScreenRows()
        => TablePreview.ItemsSource is IEnumerable<IReadOnlyList<string>> rows ? [.. rows] : [];

    /// <summary>自检用：**导出真正会写出去的那张表**（Tab 分列、换行分行）。
    /// 与 <see cref="BuildContent"/> 同一条取值路径，因此"屏幕 == 导出"这条不变量可以被断言。</summary>
    internal string TestExportContentTsv()
        => BuildContent().Text.Replace("\r\n", "\n").TrimEnd('\n');

    /// <summary>自检用：**原始（识别）顺序**的那份单元格，Tab 分列。
    /// 识别顺序由 <c>TableState</c> 持有（原来是生产类上的 `_originalCells`）。</summary>
    internal string TestOriginalTsv()
        => string.Join("\n", _table.OriginalRows.Select(r => string.Join('\t', r)));

    /// <summary>自检用：把一条分割线拖到某个像素位置（等价于按住线拖到该处）。
    /// 分割线的 Line 元素只在「编辑分割线」模式下存在，所以这里按需临时打开该模式再复原。</summary>
    internal bool TestGridDragAt(bool isColumn, int index, double position)
    {
        var wasEditing = _gridEditMode;
        if (!wasEditing)
        {
            SetGridEditMode(true);
        }

        try
        {
            var lines = isColumn ? ColumnLinesSnapshot() : RowLinesSnapshot();
            if (lines is null || index <= 0 || index >= lines.Count - 1)
            {
                return false;
            }

            var line = GridLineLayer.Children.OfType<Line>()
                .FirstOrDefault(l => l.Tag is (bool c, int i) && c == isColumn && i == index);
            if (line is null)
            {
                return false;
            }

            BeginGridDrag(isColumn, index);
            var point = isColumn
                ? new Point(position, _image.PixelHeight / 2.0)
                : new Point(_image.PixelWidth / 2.0, position);
            DragGridLineTo(point);
            return true;
        }
        finally
        {
            if (!wasEditing)
            {
                SetGridEditMode(false);
            }
        }
    }

    /// <summary>自检用：分割线编辑的拖拽状态是否还挂在某条线上（删线后必须为 false）。</summary>
    internal bool TestGridDragActive => _gridDrag is not null;

    /// <summary>自检用：当前拖拽的线是否还在视觉树里（幽灵线的判据）。</summary>
    internal bool TestGridDragLineAttached
        => _gridDrag?.Line is { } line && GridLineLayer.Children.Contains(line);

    /// <summary>自检用：当前拖拽的下标（应恒等于该线 Tag 里的下标；两份状态已收敛为一份）。</summary>
    internal int TestGridDragIndex => _gridDrag?.Index ?? -1;

    /// <summary>自检用：右键删掉某条内部分割线（与用户右键点线走同一条数据链路），返回是否真的删掉了。</summary>
    internal bool TestDeleteGridLine(bool isColumn, int index)
    {
        if (!_gridEditMode)
        {
            SetGridEditMode(true); // Line 元素只在「编辑分割线」模式下存在
        }

        return DeleteGridLine(isColumn, index);
    }

    /// <summary>自检用：某条列线的像素位置（拖动/删线前后比对用）。</summary>
    internal double? TestColumnLine(int index)
        => CurrentGrid is { } grid && index >= 0 && index < grid.ColumnLines.Count ? grid.ColumnLines[index] : null;

    /// <summary>
    /// 自检用：模拟"按住某条分割线，把鼠标移到 position"这一下（**带左键按下状态**）。
    /// 只用来复现/验证：删线之后若拖拽状态没清，这一下会去写一条**已脱离视觉树**的线。
    /// </summary>
    internal void TestGridDragMovePressed(bool isColumn, double position)
    {
        var point = isColumn
            ? new Point(position, _image.PixelHeight / 2.0)
            : new Point(_image.PixelWidth / 2.0, position);
        DragGridLineTo(point);
    }

    /// <summary>自检用：清掉拖拽状态（模拟松开鼠标）。</summary>
    internal void TestEndGridDrag() => EndGridDrag();

    private List<double>? ColumnLinesSnapshot() => CurrentGrid?.ColumnLines.ToList();

    private List<double>? RowLinesSnapshot() => CurrentGrid?.RowLines.ToList();

    /// <summary>
    /// 自检用：把某个识别框当作用户点了一下 —— 走真实的"框 → 文本"联动
    /// （<see cref="SelectTextForLine"/>），返回文本框里**真正被选中的那段文字**。
    ///
    /// 判据：它必须等于该框自己的文字。若 <c>_spans</c> 是编辑之前的旧映射，
    /// 这里会选中完全不相干的一段（或越界返回空），断言立刻失败。
    /// </summary>
    internal string TestClickBoxSelectText(int index)
    {
        SelectTextForLine(index);
        return TextHost.SelectedText;
    }

    /// <summary>自检用：退出排序（回到识别顺序），让后面的断言从干净状态开始。</summary>
    internal void TestClearSort()
    {
        if (_sort.IsNone)
        {
            return;
        }

        _sort = SortState.None;
        foreach (var other in TablePreview.Columns)
        {
            other.SortDirection = null;
        }

        _table.RestoreOriginalOrder();
        SetCurrentTable(TableUpdate.View, what: "TestClearSort");
    }

    /// <summary>自检用：模拟点击某一列表头（返回 asc / desc / none），用于验证"升→降→取消"三态。
    /// 方向是从**一个**排序状态派生的，不再读 `_sortDirection` 字段。</summary>
    internal string TestSortColumn(int column)
    {
        if (column < 0 || column >= TablePreview.Columns.Count)
        {
            return "invalid";
        }

        var target = TablePreview.Columns[column];
        OnTableSorting(this, new DataGridSortingEventArgs(target));
        return _sort.IsNone
            ? "none"
            : _sort.IsAscending ? "asc" : "desc";
    }

    /// <summary>自检用：当前表格第一行第一列的显示值（验证排序/刷新确实改了预览）。</summary>
    internal string TestFirstCell()
    {
        if (TablePreview.ItemsSource is not IEnumerable<IReadOnlyList<string>> rows)
        {
            return string.Empty;
        }

        foreach (var row in rows)
        {
            return row.Count > 0 ? row[0] : string.Empty;
        }

        return string.Empty;
    }

    /// <summary>自检用：某条列分割线在屏幕上的 x 坐标（供真实鼠标拖动）。</summary>
    internal double? TestColumnLineScreenX(int index)
    {
        if (CurrentGrid is not { } grid || index < 0 || index >= grid.ColumnLines.Count)
        {
            return null;
        }

        // GridLineLayer 的坐标系就是图片像素，PointToScreen 会自动串上 ZoomHost 的缩放
        var screen = GridLineLayer.PointToScreen(new Point(grid.ColumnLines[index], _image.PixelHeight / 2.0));
        return screen.X;
    }

    /// <summary>自检用：预览区中心的屏幕 y 坐标（拖分割线时给一个稳定的 y）。</summary>
    internal int TestPreviewCenterScreenY()
        => (int)PreviewHost.PointToScreen(new Point(PreviewHost.ActualWidth / 2, PreviewHost.ActualHeight / 2)).Y;

    /// <summary>自检用：预览区中心点的屏幕坐标（先把鼠标移进去，右下角悬停工具条才会浮现）。</summary>
    internal Point TestPreviewCenterScreen()
        => PreviewHost.PointToScreen(new Point(PreviewHost.ActualWidth / 2, PreviewHost.ActualHeight / 2));

    internal bool TestPreviewToolbarVisible => PreviewToolbar.Visibility == Visibility.Visible;

    /// <summary>预览图里某个像素点对应的**屏幕物理像素**坐标。</summary>
    internal Point TestImagePointScreen(double imageX, double imageY)
        => PreviewImage.PointToScreen(new Point(imageX, imageY));

    /// <summary>悬停工具条里"编辑（铅笔）"按钮中心的屏幕坐标。</summary>
    internal Point TestEditButtonCenterScreen()
        => EditButton.PointToScreen(new Point(EditButton.ActualWidth / 2, EditButton.ActualHeight / 2));

    /// <summary>第 index 个识别框的像素范围（用来算"右键点框"和"拖框补识别"的落点）。</summary>
    internal (double X0, double Y0, double X1, double Y1)? TestBoxBounds(int index)
        => index >= 0 && index < _lines.Count
            ? (_lines[index].X0, _lines[index].Y0, _lines[index].X1, _lines[index].Y1)
            : null;

    /// <summary>自检用：跑一次"补识别"（与左键拖框走同一条数据链路），返回新的行数。</summary>
    internal async Task<int> TestAddRegionAsync(Int32Rect region)
    {
        await RecognizeRegionAsync(region);
        return _lines.Count;
    }

    /// <summary>自检用：删掉一行（与右键点框走同一条数据链路），返回新的行数。</summary>
    internal int TestDeleteLine(int index)
    {
        if (index < 0 || index >= _lines.Count)
        {
            return _lines.Count;
        }

        _lines.RemoveAt(index);
        RefreshFromLines($"已删除 1 行（共 {_lines.Count} 行）");
        _log.Info($"  [删行] 后：{_table.Describe()} 屏幕 {ScreenRows().Count} 行");
        return _lines.Count;
    }
}
