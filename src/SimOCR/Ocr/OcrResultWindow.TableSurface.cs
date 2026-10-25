// OcrResultWindow 的 partial 拆分（表格快速通道 / 当前表状态）。
// 本文件承载 TableState 一族 + 排序 + 刷新入口的上半段；
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
    // ------------------------------------------------- 表格快速通道 / 增强识别

    // `_gridTables` 已删 —— 它唯一的用法是 `_gridTables = tables;`（写后不读），
    // 只在"检测到几张表"的日志行里被数过一次（`tables.Count`），留着它等于把一份没人读的状态
    // 挂在生产类上（0.10.2-R2 点名的四个漏收敛字段之一）。
    private long _gridDetectMs;

    /// <summary>线格检测**故障**（不是"没找到网格"）的累计次数 —— 故障与结论分码后
    /// 需要一个可读的计数，否则"回退到空白对齐"这一条路径仍无法与"真的没有网格"区分。</summary>
    private int _gridDetectFailures;

    /// <summary>
    /// 当前这张表只有这一份状态。
    ///
    /// 重构前"当前表"被 5 个字段同时表示（`_grid` / `_fastGrid` / `_enhancedContent` /
    /// `_originalCells` / `_lines`），其中 `_grid` 与 `_fastGrid` 在 5 处被赋成**同一个实例**，
    /// 只在增强识别成功那一瞬间不同；失效与刷新散落在 ≥9 处，全靠注释里的约定维持。
    /// 不变量：**看到的表 == 导出的表 == 按钮状态**。
    ///
    /// `_originalCells` 也收进来了（"识别顺序"那一份现在由
    /// <see cref="TableState.OriginalRows"/> 持有，不再是生产类上的第二个字段）。
    ///
    /// 现在：`_table`（一个当前表 + 模式枚举 + 视图 + 识别顺序）+ `_lastGeneralGrid`（"刷新"回通用识别的
    /// 结构底稿，不是"当前表"）。
    /// 显示 / 导出 / 按钮可用性全部走 <see cref="CurrentTableInfo"/> / <see cref="BuildContent"/> /
    /// <see cref="UpdateGridButtons"/>，而这三者都只读这一份状态。
    /// </summary>
    private readonly TableState _table = new();

    /// <summary>最近一次通用识别（快速通道）算出来的网格结构。**不是"当前表"** ——
    /// 它只是"刷新"要重新运行通用识别时的底稿，以及增强识别失败时的回落来源
    /// （增强识别成功与否都不该把"当前显示的是哪张表"这个判断藏进这个字段里）。</summary>
    private TableGrid? _lastGeneralGrid;

    /// <summary>当前表的模式。**显式枚举**，不再靠"哪个字段非空"去猜。</summary>
    private enum TableMode
    {
        /// <summary>还没有任何表（识别前 / 没识别到表格 / TXT 模式）。</summary>
        None,

        /// <summary>通用（快速）识别的网格表：列名只是序号，可编辑分割线。</summary>
        General,

        /// <summary>增强识别还原出的语义表：不可编辑分割线。</summary>
        Semantic,
    }

    /// <summary>一个当前表：模式 + 内容 + 屏幕上那份视图 + 识别顺序。</summary>
    private sealed class TableState
    {
        /// <summary>没有表时的空视图（<see cref="View"/> 在无表时返回它，调用方不必判 null）。</summary>
        private static readonly IReadOnlyList<IReadOnlyList<string>> EmptyView = Array.Empty<List<string>>();

        internal TableMode Mode { get; private set; }

        /// <summary>通用表（<see cref="TableMode.General"/> 时非空）。</summary>
        internal TableGrid? Grid { get; private set; }

        /// <summary>语义表（<see cref="TableMode.Semantic"/> 时非空）。</summary>
        internal OcrExportContent? Semantic { get; private set; }

        /// <summary>语义表的视图（通用表**不用**副本，见 <see cref="View"/>）。</summary>
        private List<List<string>> _semanticRows = [];

        /// <summary>
        /// 识别顺序的那一份（"取消排序"要回到它）。
        ///
        /// 以前它是 `OcrResultWindow._originalCells` —— 与 <see cref="View"/> 是**同一个逻辑状态的两份持有者**
        /// （0.10.2-R2），而且每次绑定都绕一圈（拷一份出来 → `SetView` 设回去 → `SetView` 内部又拷一次）。
        /// 现在它只在这里：只由 <see cref="BeginView"/> 写，只由 <see cref="RestoreOriginalOrder"/> 与排序读。
        /// </summary>
        private readonly List<List<string>> _originalRows = [];

        /// <summary>
        /// 屏幕上那份视图的只读包装（0.10.2-R3 的收敛点）。
        ///
        /// 它包的**就是底层那一个列表实例**（通用档 = `Grid.Cells`、语义档 = `_semanticRows`），
        /// 所以"视图副本过期"这件事不存在（成因）；但对外**只给只读接口**：
        /// 以前 <see cref="View"/> 直接返回 `List&lt;List&lt;string&gt;&gt;`，任何拿到它的人都能绕过
        /// <see cref="TableState"/> 改"当前表"（`Grid.Cells` 本身仍是 internal，见下面的"写入口"清单）。
        ///
        /// 写入口只有这七个：<see cref="SetGeneral"/> / <see cref="SetSemantic"/> / <see cref="SetView"/> /
        /// <see cref="BeginView"/> / <see cref="RestoreOriginalOrder"/> / <see cref="RefillCurrent"/> /
        /// <see cref="Fill"/> / <see cref="Clear"/> —— 每次底层列表被**整体替换**时都重建这个包装
        /// （`Refill` / `SetCells` 都是替换而不是就地改，这正是教训）。
        ///
        /// 代价（写进注释，免得以后有人"顺手"改回活引用）：包装不是 `Grid.Cells` 本身，
        /// 但 <c>IsScreenBoundToExportSource</c> 比的是"屏幕那一份 == 导出读的那一份"
        /// （两边都是 <see cref="View"/>），这个同一个性判据仍然精确成立。
        /// </summary>
        private ReadOnlyCollection<List<string>>? _view;

        /// <summary>
        /// **屏幕上正在显示的那份单元格**（排序视图也在里面，导出必须跟随它）。
        /// **只读**：见 <see cref="_view"/> 的说明。
        /// </summary>
        internal IReadOnlyList<IReadOnlyList<string>> View
            => _view is { } view ? (IReadOnlyList<IReadOnlyList<string>>)view : EmptyView;

        /// <summary>**识别顺序**那一份（"取消排序"回到它；自检用它做原序对照）。</summary>
        internal IReadOnlyList<IReadOnlyList<string>> OriginalRows => _originalRows;

        internal void Clear()
        {
            Mode = TableMode.None;
            Grid = null;
            Semantic = null;
            _semanticRows = [];
            _originalRows.Clear();
            _view = null;
        }

        internal void SetGeneral(TableGrid grid, IReadOnlyList<string> headers)
        {
            Mode = TableMode.General;
            Grid = grid;
            Semantic = null;
            _semanticRows = [];
            Headers = headers;
            _view = grid.Cells.AsReadOnly();
        }

        internal void SetSemantic(OcrExportContent content, IReadOnlyList<IReadOnlyList<string>> cells, IReadOnlyList<string> headers)
        {
            Mode = TableMode.Semantic;
            Grid = null;
            Semantic = content;
            _semanticRows = [.. cells.Select(row => row.ToList())];
            _view = _semanticRows.AsReadOnly();
            Headers = headers;
        }

        /// <summary>
        /// 一次"换表"的收尾 —— 记下识别顺序，并把视图重置成它（排序三态从"无排序"起）。
        ///
        /// 只由"换表"（通用 / 语义）调用。**重绑视图时绝不能调它**：0.10.3 的 `BindView` 把它塞进了
        /// 每一次重绑里，于是"排序 → 重绑"会把识别顺序**覆盖成排序后的顺序**，三态机当场退化成
        /// "永远升序"（`_sortColumn` / `_sortClicks` 同时被清零，第 2 次点击又被当成第 1 次）。
        /// 这是 顺手修掉的真实缺陷。
        /// </summary>
        internal void BeginView()
        {
            _originalRows.Clear();
            _originalRows.AddRange(View.Select(row => row.ToList()));
            SetView(_originalRows);
        }

        /// <summary>把视图恢复成识别顺序（"取消排序"）。</summary>
        internal void RestoreOriginalOrder() => SetView(_originalRows);

        /// <summary>换掉屏幕上那份视图（排序 / 取消排序）——**当前表视图的唯一写入口**。</summary>
        internal void SetView(IReadOnlyList<IReadOnlyList<string>> cells)
        {
            if (Mode == TableMode.General)
            {
                Grid!.SetCells(cells);
                _view = Grid!.Cells.AsReadOnly();
            }
            else
            {
                _semanticRows = [.. cells.Select(row => row.ToList())];
                _view = _semanticRows.AsReadOnly();
            }
        }

        /// <summary>
        /// 给"当前通用表"重新落格 —— 生产路径里改当前表 `Cells` 的唯一写入口。
        /// `Refill` 是**整体替换** `Cells`，所以这里必须同时换掉只读包装（否则包装指向旧列表）。
        /// </summary>
        internal void RefillCurrent(IReadOnlyList<OcrLine> lines)
        {
            Grid!.Refill(lines);
            _view = Grid!.Cells.AsReadOnly();
        }

        /// <summary>
        /// 落格的漏斗。 增强识别那几条路径手里的结构可能是"当前表"（`CurrentGrid` /
        /// `_lastGeneralGrid`），也可能只是一份临时结构；统一从这里落格，若它**就是**当前表
        /// 就顺带换掉只读包装。
        /// </summary>
        internal void Fill(TableGrid grid, IReadOnlyList<OcrLine> lines)
        {
            grid.Refill(lines);
            if (ReferenceEquals(grid, Grid))
            {
                _view = Grid!.Cells.AsReadOnly();
            }
        }

        /// <summary>屏幕上那份视图的表头（排序会重建 DataGrid 列，靠它还原）。</summary>
        internal IReadOnlyList<string> Headers { get; private set; } = [];

        /// <summary>当前表按屏幕顺序的 (行, 列)。没有表时是 null。</summary>
        internal (int Rows, int Columns)? Info
            => Mode switch
            {
                TableMode.General => (Grid!.RowCount, Grid!.ColumnCount),
                TableMode.Semantic => (Semantic!.Layout.RowCount, Semantic!.Layout.ColumnCount),
                _ => null,
            };

        /// <summary>给状态条/日志用的当前表说明。</summary>
        internal string Describe()
            => Info is { } info
                ? $"{info.Rows} 行 × {info.Columns} 列（{(Mode == TableMode.Semantic ? "语义表" : Grid?.Source ?? "通用表")}）"
                : "无表";
    }

    /// <summary>当前表（通用网格）——**显示/导出/按钮的唯一取值入口**。</summary>
    private TableGrid? CurrentGrid => _table.Mode == TableMode.General ? _table.Grid : null;

    /// <summary>当前表（语义表）——同上。</summary>
    private OcrExportContent? CurrentSemantic => _table.Mode == TableMode.Semantic ? _table.Semantic : null;

    private (int Rows, int Columns)? CurrentTableInfo => _table.Info;

    /// <summary>把当前表设成通用网格（并记下它是"刷新"的底稿）。视图直接跟着 <c>Grid.Cells</c>。
    /// **只由 <see cref="SetCurrentTable"/> 调用** —— 单独调它会把"内容已换、视图还没绑"的中间态露出来。</summary>
    private void SetGeneralPayload(TableGrid grid, IReadOnlyList<string> headers)
    {
        _table.SetGeneral(grid, headers);
        _lastGeneralGrid = grid;
    }

    /// <summary>把当前表设成增强识别的语义表（视图由 <see cref="SetCurrentTable"/> 同一步绑好后再校验）。</summary>
    private void SetSemanticPayload(OcrExportContent content, IReadOnlyList<IReadOnlyList<string>> cells, IReadOnlyList<string> headers)
    {
        _table.SetSemantic(content, cells, headers);
        _semanticMatchesLines = true; // 刚由当前这批识别行算出来
    }

    /// <summary>一次"当前表"变更要做什么（见 <see cref="SetCurrentTable"/>）。</summary>
    private enum TableUpdate
    {
        /// <summary>退役当前表（识别前 / 没识别到结构 / 手工删线）。</summary>
        Clear,

        /// <summary>换上通用（快速）识别的网格表。</summary>
        General,

        /// <summary>换上增强识别的语义表（不可编辑分割线）。</summary>
        Semantic,

        /// <summary>内容不变，只把 <c>_table.View</c>（排序视图）重新绑到预览上。</summary>
        View,
    }

    /// <summary>
    /// **唯一状态入口：所有会改变"用户看到哪张表"的路径都必须走这里。**
    ///
    /// 一次调用把三件事**一起**做掉，然后校验一次不变量：
    ///   ① 设"当前表"（模式 + 网格/语义表）；
    ///   ② 重建 DataGrid 列并把视图绑上去（<see cref="TableState.View"/> 就是导出源）；
    ///   ③ 按新模式重算按钮可用性（<see cref="UpdateGridButtons"/>）。
    ///
    /// 为什么必须是一个入口：`_table`（模式 + 内容）与 `TablePreview.ItemsSource`（屏幕）以及
    /// **文本框**（导出判据用的"用户有没有改过文本"）是同一件事的三个面。以前它们由各调用点各自
    /// 维护，于是出现"状态已经改了、屏幕还没绑"那种**可观测的中间态** —— 实测（2026-10-13，0.10.2 回归）：
    /// 「刷新」与识别收尾交错时，先清表后刷文本，退化成"屏幕 14 行、导出 1 行"，
    /// 不变量在 <c>ShowTablePreview</c> 里炸掉并把 `_stateInvariantBroken` 粘住，
    /// 后面「增强识别」即使**真的**把表切成了语义表，断言也永远读成 False。
    /// </summary>
    /// <param name="update">这次要做哪种变更。</param>
    /// <param name="grid"><see cref="TableUpdate.General"/> 时的通用网格。</param>
    /// <param name="content"><see cref="TableUpdate.Semantic"/> 时的语义表内容。</param>
    /// <param name="cells">语义表的单元格（<see cref="TableUpdate.Semantic"/> 用）。</param>
    /// <param name="headers">表头（<see cref="TableUpdate.Semantic"/> 用，或覆盖通用表的序号列名）。</param>
    /// <param name="syncText">是否同时把文本框重新同步到 <see cref="_lines"/>（识别落格用）。</param>
    /// <param name="what">日志/断言里标注的调用者。</param>
    private void SetCurrentTable(
        TableUpdate update,
        TableGrid? grid = null,
        OcrExportContent? content = null,
        IReadOnlyList<IReadOnlyList<string>>? cells = null,
        IReadOnlyList<string>? headers = null,
        bool syncText = false,
        string? what = null)
    {
        var tag = what ?? update.ToString();
        if (update == TableUpdate.Clear)
        {
            _table.Clear();
            ClearTablePreview();
            HideTablePanel();
            UpdateGridButtons();
            VerifyTableState(tag);
            return;
        }

        if (update == TableUpdate.General)
        {
            if (grid is null)
            {
                // 没有可用的网格结构 → 当前表退役（屏幕与导出一起回到"没有表"）
                SetCurrentTable(TableUpdate.Clear, what: tag);
                return;
            }

            // 文本框与 `_lines` 必须**在表状态变更之前**同步 —— 导出判据是
            // "文本框 == 文档纯文本"，文本落后一步就会让导出退回"编辑后的文本"口径，
            // 于是"屏幕上的表 ≠ 导出的表"（这正是 0.10.2 那条回归的机制）。
            if (syncText)
            {
                SyncTextFromLines();
            }

            TableHost.Visibility = Visibility.Visible;
            var columnHeaders = headers ?? [.. Enumerable.Range(1, grid.ColumnCount).Select(i => $"第 {i} 列")];
            SetGeneralPayload(grid, columnHeaders);
            // 换表才记"识别顺序"并重置排序三态；重绑视图（TableUpdate.View）不走这两步。
            _table.BeginView();
            _sort = SortState.None;
            BindView(columnHeaders);
            UpdateGridButtons();
            VerifyTableState(tag);
            return;
        }

        if (update == TableUpdate.Semantic)
        {
            if (content is null)
            {
                SetCurrentTable(TableUpdate.Clear, what: tag);
                return;
            }

            if (syncText)
            {
                SyncTextFromLines();
            }

            TableHost.Visibility = Visibility.Visible;
            var columnHeaders = headers ?? _table.Headers;
            SetSemanticPayload(content, cells ?? _table.View, columnHeaders);
            _table.BeginView();
            _sort = SortState.None;
            BindView(columnHeaders);
            UpdateGridButtons();
            VerifyTableState(tag);
            return;
        }

        // View：内容与模式都不动，只把（排序后的）视图重新绑到预览上。
        // 这里绝不能碰"识别顺序"与排序状态 —— 这一跳就是排序路径本身，
        // 重置排序状态会让三态机（升 → 降 → 取消）永远停在"升序"。
        BindView(headers ?? _table.Headers);
        UpdateGridButtons();
        VerifyTableState(tag);
    }

    /// <summary>
    /// 把"当前表"的视图重建到 DataGrid 上（列定义 + ItemsSource）。
    ///
    /// 这里不再记"识别顺序"、也不再重置排序状态（那是"换表"的收尾，
    /// 见 <see cref="TableState.BeginView"/> 与 <see cref="SetCurrentTable"/>）。
    /// 0.10.3 把它们混在每一次重绑里，于是排序路径自己把自己的三态清零（真实缺陷）。
    /// </summary>
    private void BindView(IReadOnlyList<string> headers)
    {
        TablePreview.Columns.Clear();
        for (var c = 0; c < headers.Count; c++)
        {
            TablePreview.Columns.Add(new DataGridTextColumn
            {
                Header = string.IsNullOrEmpty(headers[c]) ? $"第 {c + 1} 列" : headers[c],
                Binding = new Binding($"[{c}]"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
            });
        }

        // 绑给屏幕的就是 TableState.View 那**一个**只读实例 —— IsScreenBoundToExportSource 靠它做 O(1) 判据
        TablePreview.ItemsSource = _table.View;
    }

    /// <summary>
    /// 语义表是否**就是当前这批识别行**算出来的：<see cref="RefreshFromLines"/> 之后（删框/补识别/文本重写）
    /// 它就过期了 —— 过期的语义表不能再当导出源，否则"屏幕上看到的（已过期的那张语义表）"与
    /// "导出的（按新文本切分的表）"会分叉；也让 `TestEnhancedTsv` 这类钩子能报告"没有有效语义表"。
    /// </summary>
    private bool _semanticMatchesLines;

    /// <summary>清空表格预览（列定义 + 绑定的数据 + 排序状态）。只由 <see cref="SetCurrentTable"/> 调用。</summary>
    private void ClearTablePreview()
    {
        TablePreview.Columns.Clear();
        TablePreview.ItemsSource = null;
        _table.Clear();
        _sort = SortState.None;
    }

    /// <summary>
    /// 不变量进生产路径。 每次"当前表"变更之后都检查一次
    /// 「看到的表 == 导出的表 == 按钮状态」，破坏时 <see cref="Debug.Assert"/> 直接炸开发版，
    /// 同时写一条 <c>StateInvariantBroken</c> 日志（发布版也能留痕）。
    ///
    /// 原来是测试钩子 <c>TestStateConsistent()</c> 里的检查，而它唯一的调用者是一条不生效的断言
    /// 日志里有 `[失败]`、退出码照样是 0。
    ///
    /// **0.10.2-R1：这一跳只跑 O(1) 的结构性检查**（<see cref="IsTableStateConsistent"/>），
    /// 不再在这里重算一遍导出管线；彻底版的逐格比对在测试/验收路径
    /// （<see cref="TestStateConsistent"/> → <see cref="IsScreenExportConsistent"/>）。
    /// 只有**已经判定被破坏**时才调用 <see cref="TestScreenExportDiff"/> 把分叉点写清楚 ——
    /// 那是诊断，不是每次状态变更的固定开销。
    /// </summary>
    private void VerifyTableState(string what)
    {
        if (IsTableStateConsistent())
        {
            return;
        }
        _log.Error($"StateInvariantBroken（{what}）：看到的表 == 导出的表 == 按钮状态 这条不变量被破坏"
                   + $"（模式={_table.Mode}，网格={CurrentGrid?.Source ?? "无"}，语义表={(CurrentSemantic is null ? "无" : "有")}，"
                   + $"屏幕行数={ScreenRows().Count}）");
        // 分叉点一次说清（走的是哪条导出路径 / 网格·视图·导出·屏幕各几行 / 第一处不同的格子）：
        // 破坏时最贵的是"猜是哪一层脱节"，而这条只在**已经坏了**的时候才写，不占正常路径。
        _log.Error($"  [分叉点] {DescribeScreenExportFork()}");
        Debug.Assert(false, $"表格状态不变量被破坏（{what}）");
        _stateInvariantBroken = true;
    }

    /// <summary>
    /// 不变量是否真的被破坏过（自检用：开发版 Debug.Assert 会被测试宿主吞掉）。
    ///
    /// 这个标志不再对外暴露 —— `TestStateInvariantBroken` 是**真零引用**的钩子
    /// （全仓 `.cs/.xaml/.ps1/.md` 里除声明处无任何调用），生产类不该为"没人读的钩子"留接口。
    /// 需要它的断言走 <see cref="TestStateConsistent"/>（它已经把 `_stateInvariantBroken` 算进去）。
    /// </summary>
    private bool _stateInvariantBroken;

    /// <summary>自检用：清掉粘性的"曾经破坏过"标记，让某一段断言只对自己这一段负责
    /// （识别过程中"空表"这种过渡态不该让后面所有断言一起变红）。</summary>
    internal void TestResetInvariantFlag() => _stateInvariantBroken = false;

    /// <summary>
    /// 「看到的表 == 导出的表 == 按钮状态」里**能在常数时间内判定**的那部分：
    /// ① 模式与字段一致（不允许两套表并存）；② 只有通用网格表才允许进"编辑分割线"模式；
    /// ③ 非忙碌时「编辑分割线」可点性 == **由模式派生**的期望值
    /// （<see cref="TableMode.Semantic"/> → 必须禁用；TXT → 禁用；其余允许，没表也允许手工画线）；
    /// ④ TXT 模式不许进分割线编辑 / 用增强识别 / 展示表格区；
    /// ⑤ **屏幕绑定的那一份就是导出要读的那一份**（见 <see cref="IsScreenBoundToExportSource"/>）。
    ///
    /// **0.10.2-R1：⑤ 不再是"重算一遍导出再逐格比"，而是"两边读的是不是同一份派生结果"。**
    /// 上一版在这里调用 <see cref="IsScreenExportConsistent"/>，它会跑完整导出管线
    /// （<c>BuildContent → FromGrid/FromLayout</c>，再把导出的 TSV 反向解析回单元格逐格比），
    /// 于是 <c>SetCurrentTable</c> 的四个返回路径（识别落格 / 刷新 / 排序 / 清表）每次都要在 UI 线程上
    /// 重算一遍导出 —— 那是**测试形状的检查住进了出货路径**。
    /// 现在屏幕与导出共用同一份派生结果（<see cref="DeriveTable"/> 是唯一决策点、它读的就是 <c>TableState.View</c>，
    /// 而 <see cref="BindView"/> 把**同一个实例**绑给 <c>TablePreview</c>），
    /// "屏幕 == 导出"在**取值来源**这一层结构上成立，⑤ 只需一次引用比较；
    /// 逐格比对（导出管线的往返保真）留在测试/验收路径：
    /// <see cref="TestStateConsistent"/> 与 <see cref="TestScreenExportConsistent"/>。
    /// </summary>
    private bool IsTableStateConsistent()
    {
        if ((_table.Mode == TableMode.General) != (CurrentGrid is not null) ||
            (_table.Mode == TableMode.Semantic) != (CurrentSemantic is not null))
        {
            return false;
        }

        if (_gridEditMode && IsTextOnlyFormat)
        {
            return false;
        }

        if (!_busy && GridEditButton.IsEnabled != ExpectedGridEditEnabled())
        {
            return false;
        }

        if (IsTextOnlyFormat && (_gridEditMode || EnhanceButton.IsEnabled || TestTablePanelVisible))
        {
            return false;
        }

        // ⑤ 屏幕那一份 == 导出要读的那一份（同一个实例；不跑任何导出管线计算）
        return IsScreenBoundToExportSource();
    }

    /// <summary>
    /// **结构性不变量（O(1)，生产同步路径用）**：屏幕上显示的那份单元格，
    /// 就是 <see cref="DeriveTable"/>（导出的唯一取值入口）要读的那一份 —— 两者是**同一个实例**。
    ///
    /// 为什么一次引用比较就够：`TableState.View` 是屏幕与导出**共同**的取值来源
    /// （<see cref="BindView"/> 把 `TablePreview.ItemsSource` 设成它；<see cref="DeriveTable"/> 的
    /// 通用/语义两档都直接读它，见 <see cref="BuildContent"/>）。于是这条不变量一旦成立，
    /// "屏幕 == 导出"就不是靠事后核对维持的，而是**取值来源同一份**的直接推论。
    ///
    /// **之后比的仍然精确成立**：`View` 返回的是 `TableState` 里的那个只读包装实例
    /// （不是每次现造一个新对象），绑定与派生读的是它，所以判据没有变弱 ——
    /// 只是"拿到它的人改不动它"了（0.10.2-R3）。
    ///
    /// 它抓的是"来源被换掉"这一类破坏 —— `TableGrid.Refill` / `SetCells` 都是**整体替换**
    /// `Cells`（成因：删框后屏幕与落格都换了、导出还指向旧列表），
    /// 只要有人换了却没重绑屏幕，这里的引用比较立刻不等。
    /// </summary>
    private bool IsScreenBoundToExportSource()
        => _table.Mode == TableMode.None
            ? TablePreview.ItemsSource is null
            : ReferenceEquals(TablePreview.ItemsSource, _table.View);

    /// <summary>
    /// **逐格判据**：屏幕上的表 == **导出真正会写出去的那张表**（顺序与内容都一致）。
    ///
    /// **0.10.2-R1：这条已经移出生产同步路径。** 它要跑完整导出管线
    /// （一次 <see cref="DeriveTable"/> + TSV 反向解析）再逐格比，量级与"刷新一次"同阶，
    /// 放在 <c>SetCurrentTable</c> 的四个返回路径上等于每次状态变更都在 UI 线程重算一遍导出。
    /// 现在只由**测试 / 验收路径按需**调用（<see cref="TestStateConsistent"/> /
    /// <see cref="TestScreenExportConsistent"/>，验收链在 <c>Diagnostics/TableAcceptance.cs</c> 与
    /// <c>Diagnostics/SelfTest.cs</c>），回归判据的牙口不变；
    /// 生产路径由 O(1) 的 <see cref="IsScreenBoundToExportSource"/> 守"两边同一份来源"。
    /// </summary>
    private bool IsScreenExportConsistent()
    {
        if (TableHost.Visibility != Visibility.Visible)
        {
            return true;
        }

        var screen = Flattened(ScreenRows());
        if (screen.Count == 0)
        {
            return true;
        }

        // 与屏幕同源的**那一份**派生结果（一次派生，不再各自 BuildContent）
        var exported = ExportCells(DeriveTable());
        if (exported.Count != screen.Count)
        {
            return false;
        }

        for (var r = 0; r < screen.Count; r++)
        {
            // 导出会省略行尾的空单元格，所以只比到屏幕那一行的列数
            for (var c = 0; c < screen[r].Count; c++)
            {
                var right = c < exported[r].Count ? exported[r][c] : string.Empty;
                if (!string.Equals(screen[r][c], right, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>线格检测（纯像素，毫秒级）：有网格就先把"空表格"显示出来。</summary>
    private async Task DetectGridFastAsync()
    {
        try
        {
            var watch = Stopwatch.StartNew();
            var image = _image;
            List<GridTable> tables;
            try
            {
                tables = await Task.Run(() => LineGridDetector.Detect(image));
            }
            catch (LineGridFailureException ex)
            {
                // 故障**不再**与"没找到网格"同码 —— 这条日志是故障分支的唯一标记。
                _gridDetectFailures++;
                _log.Warn($"  [线格] 检测故障（不是「没有网格」）：{ex.Message}", ex);
                tables = [];
            }

            _gridDetectMs = watch.ElapsedMilliseconds;
            _log.Info($"线格快速通道：检测到 {tables.Count} 张表，用时 {_gridDetectMs}ms"
                      + (tables.Count == 0 && _gridDetectFailures > 0 ? "（本次是**检测故障**回退，不是没找到网格）" : string.Empty));
            if (tables.Count == 0)
            {
                TableStatus.Text = "表格预览：正在识别文字…（未检测到网格时会提示改用增强识别）";
                TableShape.Text = string.Empty;
                return;
            }

            // 一页可能检测到多张候选表，取**最适合当"用户框到的那张表"**的一张
            //（格子数 → 面积 → 行数 → 列数 → 左上优先），而不是拿"横线最多的第一张"。
            // 取舍与理由见 LineGridDetector.Best 的注释。
            var table = LineGridDetector.Best(tables);
            var detected = table is null ? null : TableGrid.FromGridTable(table);
            if (detected is null)
            {
                // "检出的网格形状不可用"是排查信息，进日志；界面上对用户等于"这张图没网格"。
                _log.Debug("表格预览：检出的网格形状不可用，已忽略");
                TableStatus.Text = "表格预览：正在识别文字…";
                TableShape.Text = string.Empty;
                return;
            }

            detected.ClearCells();
            SetCurrentTable(TableUpdate.General, detected, what: "线格快速通道");
            TableShape.Text = $"{detected.RowCount} 行 × {detected.ColumnCount} 列";
            TableStatus.Text = $"表格预览：已定位到 {detected.RowCount} 行 × {detected.ColumnCount} 列，正在识别文字…";
        }
        catch (Exception ex)
        {
            _log.Error("线格检测失败", ex);
        }
    }

    private void HideTablePanel()
    {
        TableHost.Visibility = Visibility.Collapsed;
        TableShape.Text = string.Empty;
        // 折叠时把预览也清掉：留着上一次的行会让人以为"还显示着那张表"（同一原则）
        ClearTablePreview();
    }

    /// <summary>
    /// **TXT 是纯文本模式**：不展示表格区，也禁用「编辑分割线 / 增强识别」。
    /// </summary>
    private bool IsTextOnlyFormat => _format == OcrExportFormat.Txt;

    /// <summary>
    /// 没识别到表格时给一份"只有外框"的可编辑骨架（XLSX/DOCX/PDF 模式），
    /// 让用户可以自己画分割线再按「刷新」切分；TXT 模式返回 null。
    /// </summary>
    private TableGrid? EnsureEditableGrid()
    {
        if (CurrentGrid is not null)
        {
            return CurrentGrid;
        }

        if (IsTextOnlyFormat)
        {
            return null;
        }

        var skeleton = TableGrid.FromBounds(_image.PixelWidth, _image.PixelHeight);
        SetCurrentTable(TableUpdate.General, skeleton, what: "EnsureEditableGrid");
        TableShape.Text = $"{skeleton.RowCount} 行 × {skeleton.ColumnCount} 列";
        TableStatus.Text = "手工建表：把分割线拖到要切的位置（双击空白加线 / Shift+双击加行线 / 右键删线），改完点「刷新」";
        return skeleton;
    }

    // ------------------------------------------------- 表头排序（升 → 降 → 取消）

    /// <summary>
    /// 排序三态就是一个显式状态。
    ///
    /// 以前它由 `_sortColumn` / `_sortClicks` / `_sortDirection` **三个字段**编码同一个三态机
    /// （0.10.2-R2 点名的漏收敛之一），三者可以互相矛盾；现在只有一个值，
    /// "方向"与"是不是已取消"都是**派生**出来的，结构上不可能不一致。
    ///
    /// 语义：<see cref="Column"/> 是排序列（-1 = 还没排过），<see cref="Clicks"/> 是该列累计点击数；
    /// `Clicks % 3` 为 1 = 升序、2 = 降序、0 = 已取消（列号留着，再点同一列回到升序）。
    /// </summary>
    private readonly record struct SortState(int Column, int Clicks)
    {
        /// <summary>"无排序"档。</summary>
        internal static readonly SortState None = new(-1, 0);

        internal bool IsNone => Column < 0 || Clicks % 3 == 0;

        internal bool IsAscending => Clicks % 3 == 1;

        /// <summary>给 DataGrid 列头用（取消排序 / 没排过时是 null）。</summary>
        internal ListSortDirection? Direction
            => IsNone ? null : IsAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;

        /// <summary>点一次表头：同一列累加，换一列从头（第 1 次 = 升序）起。</summary>
        internal SortState Click(int column)
            => column == Column ? new SortState(column, Clicks + 1) : new SortState(column, 1);
    }

    /// <summary>
    /// 排序是数据层的操作，导出跟随屏幕顺序。
    ///
    /// 口径（写进注释是要求之一）：用户点表头排序是为了让导出/核对按这个顺序走；
    /// 如果导出还留在识别顺序，"看到的表"与"导出的表"就是两张（口径会分叉）。
    ///
    /// **"识别顺序"那一份现在由 `TableState` 持有**（`TableState.OriginalRows`）——
    /// 以前是这里的 `_originalCells`，与 `TableState.View` 是同一个逻辑状态的两份持有者（0.10.2-R2）。
    /// 屏幕上那份视图则只有 `TableState.View` 一个来源（曾经还有一个 `_displayedCells` 副本，
    /// 于是排序后"屏幕是新序、导出是旧序"）。
    /// </summary>
    private SortState _sort = SortState.None;

    /// <summary>
    /// 表头点击排序：**升序 → 降序 → 取消排序**（第三档就是"恢复识别顺序"）。
    ///
    /// 不用 DataGrid 默认的两态排序：单元格绑定是下标绑定（<c>[0]</c>），
    /// 走 `SortMemberPath` 的反射排序对 `List&lt;string&gt;` 不可靠，这里自己排 —— 数值感知。
    /// </summary>
    private void OnTableSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var column = TablePreview.Columns.IndexOf(e.Column);
        if (column < 0)
        {
            return;
        }

        _sort = _sort.Click(column);

        foreach (var other in TablePreview.Columns)
        {
            other.SortDirection = null;
        }

        if (_sort.IsNone)
        {
            // 取消排序：恢复识别顺序（视图与导出一起回到原序）
            _table.RestoreOriginalOrder();
            SetCurrentTable(TableUpdate.View, what: "排序取消");
            StatusText.Text = "已取消排序（恢复识别顺序）";
            return;
        }

        var ascending = _sort.IsAscending;
        e.Column.SortDirection = _sort.Direction;
        // 排序**从"识别顺序"那一份派生**，不是从当前视图派生 ——
        // 从当前视图派生会把上一次的排序叠起来（且"取消排序"再也回不到原序）。
        var sorted = _table.OriginalRows
            .OrderBy(row => row, Comparer<IReadOnlyList<string>>.Create((a, b) => CompareCell(a, b, column)))
            .ToList();
        if (!ascending)
        {
            sorted.Reverse();
        }

        // 视图与导出读的是**同一份** _table.View —— 排序即改变导出顺序（口径见字段注释）
        _table.SetView(sorted);
        SetCurrentTable(TableUpdate.View, what: "排序");
        StatusText.Text = $"已按「{e.Column.Header}」{(ascending ? "升序" : "降序")}排序 —— 再点一次表头取消排序"
                          + "（导出跟随屏幕顺序）";
    }

    /// <summary>列内比较：两边都像数字就按数值比，否则按文本比（空值排最后）。</summary>
    private static int CompareCell(IReadOnlyList<string> a, IReadOnlyList<string> b, int column)
    {
        var left = column < a.Count ? a[column] : string.Empty;
        var right = column < b.Count ? b[column] : string.Empty;
        if (left.Length == 0 || right.Length == 0)
        {
            return left.Length == right.Length ? 0 : (left.Length == 0 ? 1 : -1);
        }

        if (FieldSemantics.TryParseNumber(CellValidator.Normalize(left), out var x) &&
            FieldSemantics.TryParseNumber(CellValidator.Normalize(right), out var y))
        {
            return x.CompareTo(y);
        }

        return string.Compare(left, right, StringComparison.CurrentCulture);
    }

}
