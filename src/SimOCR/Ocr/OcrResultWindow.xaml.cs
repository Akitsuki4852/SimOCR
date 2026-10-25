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
/// 识别结果窗口（P0-B / P0-C）：
///   * 上半部：预览图 + 识别框叠加（框随图片一起缩放，"适应窗口"）；
///   * 下半部：可编辑文本，与框**双向点选联动**；
///   * 鼠标移入预览图时，**右下角浮现**与覆盖层同款样式的工具条：编辑(铅笔) / 保存(磁盘) / ✕；
///   * 铅笔编辑模式：**左键拖框补识别**、**右键点框删除**、**滚轮按钮（中键）退出**。
///
/// 界面布局从上到下为：预览区（悬停工具条）→ 表格预览 → 可编辑文本；左下角是统一提示框。
/// </summary>
internal partial class OcrResultWindow : Window
{
    private static readonly Brush BoxStroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x45, 0x45));
    private static readonly Brush BoxStrokeActive = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD5, 0x4F));
    private static readonly Brush EditButtonActive = new SolidColorBrush(Color.FromArgb(0xFF, 0x2E, 0x9E, 0x4A));

    private readonly BitmapSource _image;
    private readonly OcrExportFormat _format;
    private readonly IRecognitionService _recognition;
    private readonly ExportService _exporter;
    private readonly LogService _log;

    private readonly List<OcrLine> _lines = [];
    private readonly List<Polygon> _boxShapes = [];
    private IReadOnlyList<(int Start, int Length)> _spans = [];

    private double _scale = 1;
    private bool _editMode;
    private bool _busy;
    private bool _dragging;
    private bool _suppressTextSync;
    private Point _dragStart;
    private int _activeBoxIndex = -1;

    // 分割线编辑（快速识别的表格结构可手工调整）
    private bool _gridEditMode;

    /// <summary>
    /// 拖拽状态只留这一份（原来是 `_gridDragLine` / `_gridDragIsColumn` / `_gridDragIndex`
    /// 三个字段 + 每个 <see cref="Line"/>.Tag 里的 `(isColumn, index)` 副本，共两套）。
    /// 删线会重建全部 Line 元素（<see cref="DrawGridLines"/>），旧字段于是指向一条**已脱离视觉树**的线，
    /// 随后的 MouseMove 会按旧下标去写分割线 —— 表现为"幽灵线"。
    /// 现在 Tag 只用来"找回是哪条线"，线段/下标一律从这一份状态读，且删线/离开图层时整体清空。
    /// </summary>
    private (Line Line, bool IsColumn, int Index)? _gridDrag;

    internal OcrResultWindow(
        BitmapSource image,
        OcrExportFormat format,
        IRecognitionService recognition,
        ExportService exporter,
        LogService log)
    {
        InitializeComponent();

        _image = image;
        // **必须在 UI 线程冻结**：覆盖层裁出来的 CroppedBitmap 属于 UI 线程，
        // 线格检测在后台线程读 PixelWidth/CopyPixels 会抛"调用线程无法访问此对象"
        // —— 实测导致线格快速通道**静默失效**（回退到空白对齐，规则表格被切成 2 列）。
        if (_image.CanFreeze && !_image.IsFrozen)
        {
            _image.Freeze();
        }

        _format = format;
        _recognition = recognition;
        _exporter = exporter;
        _log = log;

        PreviewImage.Source = image;

        // 缩放宿主与画布都用**物理像素**尺寸，缩放交给 LayoutTransform ——
        // 这样框的坐标可以直接用识别结果里的像素值，不需要任何换算。
        ZoomHost.Width = image.PixelWidth;
        ZoomHost.Height = image.PixelHeight;
        BoxCanvas.Width = image.PixelWidth;
        BoxCanvas.Height = image.PixelHeight;
        EditLayer.Width = image.PixelWidth;
        EditLayer.Height = image.PixelHeight;
        GridLineLayer.Width = image.PixelWidth;
        GridLineLayer.Height = image.PixelHeight;

        // 分割线的拖动/新增走 Canvas 自己的事件（线只是 Canvas 的子元素，事件会冒泡上来）
        GridLineLayer.MouseMove += OnGridLayerMouseMove;
        GridLineLayer.MouseLeftButtonUp += OnGridLayerMouseUp;
        GridLineLayer.MouseLeftButtonDown += OnGridLayerBackgroundDown;

        // 右上的导出信息（原来那条黑色状态行已删除，格式与像素信息挪到表格预览栏）
        TableInfo.Text = format.ShortLabel();
        TableInfo.ToolTip = $"{image.PixelWidth} × {image.PixelHeight} 像素";

        Loaded += OnWindowLoaded;
        Closed += (_, _) => _log.Info("识别结果窗口已关闭");
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplyZoom();
        UpdateToolbarAvailability();

        if (!_recognition.IsAvailable)
        {
            _busy = false;
            BusyPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = _recognition.StatusMessage;
            return;
        }

        // **快速通道**：先在像素上找线格（毫秒级）——框选出来的规则表格先出结构，
        // 文字等 OCR 回来再填（用户 2026-10-07 的预期交互：1 秒内看到简单表格）。
        // TXT 是纯文本模式：不找表格（表格区不展示、分割线/增强都禁用），省掉这段开销。
        if (!IsTextOnlyFormat)
        {
            await DetectGridFastAsync();
        }

        await RecognizeWholeImageAsync();
    }

    // ---------------------------------------------------------------- 识别

    private async Task RecognizeWholeImageAsync()
    {
        SetBusy(true, "正在识别…");
        try
        {
            var document = await _recognition.RecognizeAsync(_image);
            _lines.Clear();
            _lines.AddRange(document.Lines);

            // **先把文本框同步到新的识别行，再动"当前表"**（唯一状态入口）。
            // 导出判据是"文本框 == 文档纯文本"，文本框落后一步，导出就会退回"编辑后的文本"口径 ——
            // 于是"屏幕上的表（14 行）≠ 导出的表（1 行）"，不变量在 ShowTablePreview 里炸掉
            // 并把 `_stateInvariantBroken` 粘住（实测 2026-10-13：识别收尾与用户点「刷新」交错时必现）。
            // 注意：**不在 `await` 之前清表** —— 那会把"清空当前表"这个中间态暴露给用户与断言。
            SetCurrentTable(TableUpdate.Clear, what: "RecognizeWholeImageAsync");

            // 注意：**识别完不自动保存** ——
            // 用户校对/编辑之后，自己点右下角的磁盘按钮才写文件。
            // 新一轮识别开始：当前表（含上一次的增强结果）与"刷新"的通用结构底稿都作废
            _lastGeneralGrid = null;

            var status = $"识别完成：{_lines.Count} 行 —— "
                         + $"检查/编辑后点右下角保存按钮保存为 {_format.ShortLabel()}";

            // **通用快速识别**：
            //   ① 有框线 → 上面已经用线格定好了行列，直接落格；
            //   ② 没有框线 → 用文本框位置推行列（区间并集 + 覆盖低谷切列，再按行聚类切行）。
            // 两条路都**不做语义推断**，因此对"长得像表格的任何东西"都成立；
            // 分割线不满意可以点「编辑分割线」手工改，再点「刷新」按新分割线重算。
            if (!IsTextOnlyFormat && CurrentGrid is null && CurrentSemantic is null)
            {
                var aligned = FastTableBuilder.FromLines(_lines);
                if (aligned is not null)
                {
                    SetCurrentTable(TableUpdate.General, aligned, syncText: true, what: "快速识别");
                }
            }

            if (CurrentGrid is not null)
            {
                // 给"当前表"落格也只走 TableState（生产路径里改当前表 Cells 的唯一写入口）
                _table.RefillCurrent(_lines);
                var grid = _table.Grid!;
                SetCurrentTable(TableUpdate.General, grid, syncText: true, what: "快速识别落格");
                var sourceLabel = grid.Source switch
                {
                    "线格" => "按表格线",
                    "空白对齐" => "按文字位置",
                    _ => grid.Source,
                };
                TableShape.Text = $"{grid.RowCount} 行 × {grid.ColumnCount} 列";
                TableStatus.Text = $"已还原表格：{grid.RowCount} 行 × {grid.ColumnCount} 列（{sourceLabel}）"
                                   + " —— 分割线不对就点「编辑分割线」，要识别表头语义请点「增强识别」";
            }
            else if (IsTextOnlyFormat)
            {
                // TXT 模式：不展示表格区（TXT 下不显示没有表格的预览块）
                HideTablePanel();
                StatusText.Text = status;
            }
            else
            {
                // 没识别到表格：**不展示空表格区**（否则是一大片黑色空白），
                // 提示挪到左下角统一提示框；用户仍可点「编辑分割线」手工画线或点「刷新」重跑通用识别。
                HideTablePanel();
                StatusText.Text = "没有识别到表格结构 —— 可点右下角「编辑分割线」自己画线后按「刷新」切分，"
                                  + "或按「刷新」重新运行通用识别；也可直接编辑下方文本（用制表符分列）后保存";
            }

            RefreshFromLines(status);
            UpdateToolbarAvailability();
        }
        catch (Exception ex)
        {
            _log.Error("识别失败", ex);
            StatusText.Text = "识别失败，请重试。如果反复失败，请到「设置 → 运行信息 → 打开日志目录」查看日志。";
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    /// <summary>「增强识别」：竖带补充识别 + 语义列还原（按配置档声明的字段）+ 算术校验。</summary>
    private async void OnEnhanceClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        // 增强识别会重建语义表，先退出分割线编辑（避免两套结构并存）
        SetGridEditMode(false);
        SetBusy(true, "增强识别中…");
        try
        {
            var lines = _lines.ToList();
            if (lines.Count == 0)
            {
                var first = await _recognition.RecognizeAsync(_image);
                lines.AddRange(first.Lines);
            }

            var baseDocument = new OcrDocument(_image.PixelWidth, _image.PixelHeight, lines, 0);
            var enhanced = await StripEnhancer.EnhanceAsync(_recognition, _image, baseDocument, _log);
            _lines.Clear();
            _lines.AddRange(enhanced.Lines);
            RefreshFromLines($"增强识别：{_lines.Count} 行（含竖带补充）—— 正在做定向重认与数值校验…");

            // —— 定向重认：按快速识别的行列结构逐格检查，只对"空的 / 数值列里读不出数"的格子
            //    裁剪出来用增强档（短边自适应 = 等效放大）重新识别 ——
            //    注意：这里只在**局部变量**上拿结构，**不动"当前表"** ——
            //    语义表还没定论，提前把结构设成当前表会让"显示/导出/按钮"三者不一致。
            //    结构来源：当前通用表 → `_lastGeneralGrid`（上次通用识别的底稿）→ 按文本位置现推。
            var structure = CurrentGrid ?? _lastGeneralGrid ?? FastTableBuilder.FromLines(_lines);
            _lastGeneralGrid ??= structure; // 留着这份结构：增强识别后点「刷新」要能回到通用识别
            var reOcrCount = structure is null ? 0 : await TargetedReOcrAsync(structure);

            var layout = OcrTableLayout.Build(BuildDocument());
            if (layout.ColumnCount > 0)
            {
                // —— 数值/格式校验：先无损归一，再按列类型清洗，全部留痕 ——
                var cells = layout.Cells.Select(row => row.ToList()).ToList();
                var fixes = CellValidator.Apply(cells, layout.ColumnNumeric, layout.ColumnLabels);
                var changed = fixes.Count(f => f.Changed);
                var flagged = fixes.Count(f => !f.Changed);
                var details = fixes
                    .Select(f => $"({f.Row + 1},{f.Column + 1}) {f.Reason}：{f.Before} → {f.After}")
                    .ToList();

                var summaryParts = new List<string>();
                if (!string.IsNullOrEmpty(layout.ValidationSummary))
                {
                    summaryParts.Add(layout.ValidationSummary!);
                }

                if (changed > 0)
                {
                    summaryParts.Add($"数值/格式修正 {changed} 处");
                }

                if (flagged > 0)
                {
                    summaryParts.Add($"待人工确认 {flagged} 处");
                }

                // 清洗后可能整列变空（实测：数值列旁那一列其实是货币符号 `¥`，被 OCR 误读成 `我`/`天`，
                // 校验把噪声全部清掉后整列就空了）—— 这种列必须**整列删掉**，
                // 否则导出里会多出一列空列，还会出现两个同名列（用户反馈过"多出不应显示的内容"）。
                var numericFlags = layout.ColumnNumeric.ToList();
                var labelList = layout.ColumnLabels.ToList();
                var merges = layout.Merges.ToList();
                var columnCount = cells.Count > 0 ? cells.Max(r => r.Count) : 0;
                if (columnCount > 0)
                {
                    var drops = new List<int>();
                    for (var c = 0; c < columnCount; c++)
                    {
                        if (cells.TrueForAll(row => c >= row.Count || row[c].Length == 0))
                        {
                            drops.Add(c);
                        }
                    }

                    if (drops.Count > 0 && drops.Count < columnCount)
                    {
                        foreach (var row in cells)
                        {
                            foreach (var c in drops.OrderByDescending(v => v))
                            {
                                row.RemoveAt(c);
                            }
                        }

                        foreach (var c in drops.OrderByDescending(v => v))
                        {
                            if (c < numericFlags.Count)
                            {
                                numericFlags.RemoveAt(c);
                            }

                            if (c < labelList.Count)
                            {
                                labelList.RemoveAt(c);
                            }
                        }

                        merges = merges
                            .Where(m => !drops.Contains(m.Column))
                            .Select(m => m with { Column = m.Column - drops.Count(d => d < m.Column) })
                            .ToList();
                        summaryParts.Add($"删除全空列 {drops.Count} 列");

                        // 删完重编号：通用档的列名是"表头原文 + 序号"（如 AMOUNT、AMOUNT(2)），
                        // 而"只有货币符号"的那一列会被上面清空并删掉 —— 若不重编号，活下来的那列
                        // 会一直顶着 AMOUNT(2)，看起来像"少了一列"。
                        RenumberOrdinalSuffixedLabels(labelList);
                    }
                }

                var fixedLayout = OcrTableLayout.FromCells(
                    cells,
                    numericFlags,
                    labelList,
                    merges,
                    summaryParts.Count > 0 ? string.Join(" / ", summaryParts) : null,
                    details);

                // 语义表接管显示与导出（一个入口同时改"当前表"与屏幕视图）
                var enhancedContent = OcrExportContent.FromLayout(fixedLayout, BuildDocument());

                var headers = fixedLayout.ColumnLabels.Count == fixedLayout.ColumnCount
                    ? fixedLayout.ColumnLabels.ToList()
                    : Enumerable.Range(1, fixedLayout.ColumnCount).Select(i => $"第 {i} 列").ToList();

                // **唯一状态入口**：语义表接管显示与导出 —— 设模式 Semantic + 绑视图 + 重算按钮
                // 三件事一起做。"编辑分割线"由 UpdateGridButtons 按 _table.Mode 派生 → 这里必然是禁用的
                //（曾经分成三处各自维护：SetCurrentSemantic / ShowTablePreview / UpdateGridButtons，
                //  漏掉任何一处就会留下"语义表已接管、按钮却还亮着"的状态）。
                SetCurrentTable(TableUpdate.Semantic, content: enhancedContent, cells: cells, headers: headers, what: "增强识别");
                TableShape.Text = $"{fixedLayout.RowCount} 行 × {fixedLayout.ColumnCount} 列";

                // 界面只留用户能行动的两项；分列/校验的细目进日志（技术细节不占状态栏）
                var userSummary = new List<string>();
                if (changed > 0)
                {
                    userSummary.Add($"已自动修正 {changed} 处");
                }

                if (flagged > 0)
                {
                    userSummary.Add($"建议核对 {flagged} 处");
                }

                TableStatus.Text = $"增强识别完成：{fixedLayout.RowCount} 行 × {fixedLayout.ColumnCount} 列"
                                   + (userSummary.Count > 0 ? " · " + string.Join(" · ", userSummary) : string.Empty);
                _log.Info($"增强识别完成：{fixedLayout.RowCount}x{fixedLayout.ColumnCount}，定向重认 {reOcrCount} 格，"
                          + $"修正 {changed} 处，待确认 {flagged} 处"
                          + (summaryParts.Count > 0 ? $"；校验摘要：{string.Join(" / ", summaryParts)}" : string.Empty));
            }
            else
            {
                // 语义表没还原出来 → 按"认不出就输出通用表"回落到通用识别结果，
                // 保证 **显示 == 导出 == 按钮状态** 三者一致（原来的写法会让当前表还是语义表、
                // 界面却停在上一次成功增强的语义表上：导出的表与看到的表不是同一张）。
                var fallback = _lastGeneralGrid ?? FastTableBuilder.FromLines(_lines);
                if (fallback is not null)
                {
                    // `_lastGeneralGrid` 常常**就是**当前表 → 落格走漏斗（见 TableState.Fill）
                    _table.Fill(fallback, _lines);
                    SetCurrentTable(TableUpdate.General, fallback, syncText: true, what: "增强识别回落");
                    TableShape.Text = $"{fallback.RowCount} 行 × {fallback.ColumnCount} 列";
                }
                else
                {
                    SetCurrentTable(TableUpdate.Clear, what: "增强识别回落(无结构)");
                    TableShape.Text = string.Empty;
                }

                TableStatus.Text = fallback is null
                    ? "增强识别：未还原出表格，也没能回落到通用识别（文本见下方，可直接编辑）"
                    : $"增强识别：未还原出语义表 —— 已回落到通用识别结果（{fallback.RowCount} 行 × {fallback.ColumnCount} 列）";
                _log.Warn($"增强识别未还原出表格：已回落到通用识别结果（{fallback?.RowCount} 行 x {fallback?.ColumnCount} 列）");
            }
        }
        catch (Exception ex)
        {
            _log.Error("增强识别失败", ex);
            TableStatus.Text = "增强识别失败，已保留当前识别结果；详情见日志「设置 → 运行信息 → 打开日志目录」";

            // 异常也不能让"显示 / 导出 / 按钮"脱节：回落到通用结构；
            // 连结构都拿不到就**退役当前表**（屏幕与导出一起回到"没有表"），绝不把旧表留在屏幕上。
            if (CurrentGrid is null && CurrentSemantic is null)
            {
                if (_lastGeneralGrid is { } fallbackGrid)
                {
                    // 同一漏斗（此处当前表已退役，Fill 只落格、不动视图包装）
                    _table.Fill(fallbackGrid, _lines);
                    SetCurrentTable(TableUpdate.General, fallbackGrid, syncText: true, what: "增强识别异常回落");
                    TableShape.Text = $"{fallbackGrid.RowCount} 行 × {fallbackGrid.ColumnCount} 列";
                }
                else
                {
                    SetCurrentTable(TableUpdate.Clear, what: "增强识别异常(无结构)");
                }
            }
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async Task RecognizeRegionAsync(Int32Rect region)
    {
        SetBusy(true, "补识别中…");
        try
        {
            var document = await _recognition.RecognizeRegionAsync(_image, region);
            if (document.Lines.Count == 0)
            {
                StatusText.Text = "补识别：这块区域没有识别到文字，换个范围再试";
                return;
            }

            // 去重：与已有框重叠（IoU ≥ 0.5）的视为同一行 —— 用户框到"已经识别过的位置"时不该出现重复
            var added = 0;
            var skipped = 0;
            foreach (var line in document.Lines)
            {
                if (_lines.Any(existing => IntersectionOverUnion(existing, line) >= 0.5))
                {
                    skipped++;
                    continue;
                }

                _lines.Add(line);
                added++;
            }

            RefreshFromLines(
                skipped > 0
                    ? $"补识别：新增 {added} 行（{skipped} 行与已有框重叠，已跳过），共 {_lines.Count} 行"
                    : $"补识别：新增 {added} 行（共 {_lines.Count} 行），保存后写入导出文件");
        }
        catch (Exception ex)
        {
            _log.Error("补识别失败", ex);
            StatusText.Text = $"补识别失败：{FriendlyError.ForIo(ex)}";
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    /// <summary>两个框的交并比（判断"是不是同一行"）。</summary>
    private static double IntersectionOverUnion(OcrLine a, OcrLine b)
    {
        var ix0 = Math.Max(a.X0, b.X0);
        var iy0 = Math.Max(a.Y0, b.Y0);
        var ix1 = Math.Min(a.X1, b.X1);
        var iy1 = Math.Min(a.Y1, b.Y1);
        var intersection = Math.Max(0, ix1 - ix0) * Math.Max(0, iy1 - iy0);
        if (intersection <= 0)
        {
            return 0;
        }

        var areaA = Math.Max(0, a.X1 - a.X0) * Math.Max(0, a.Y1 - a.Y0);
        var areaB = Math.Max(0, b.X1 - b.X0) * Math.Max(0, b.Y1 - b.Y0);
        var union = areaA + areaB - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    /// <summary>
    /// 把 <see cref="_lines"/> 重新排序、刷新框与文本 —— **行级真源变了以后唯一的收口点**。
    ///
    /// 识别行变了，通用网格表的 `Cells` 也必须跟着重算。否则"删掉的行"从文本与框里消失了，
    /// 却还留在 <c>_grid.Cells</c> 里 —— 而导出读的是它（当时的典型症状：
    /// 删框之后 xlsx/docx/pdf 里仍然有那一行）。
    /// 文本被整体重写 → `_spans`（框 ↔ 文本映射）按新文本重建，不能留旧下标的映射。
    /// </summary>
    private void RefreshFromLines(string? status)
    {
        var sorted = OcrDocument.SortReadingOrder(_lines);
        _lines.Clear();
        _lines.AddRange(sorted);

        var document = BuildDocument();
        var (text, spans) = document.ToPlainTextWithSpans();
        SyncTextFromLines(text);
        // 文本已定稿：映射直接用刚算出来的这一份（赋值触发的 TextChanged 会走"按新文本重建"的兜底逻辑）
        _spans = spans;

        // 当前是通用网格表 → 按新的识别行重新落格，并同步屏幕视图与导出（走唯一状态入口）。
        // 语义表那一档：布局是按"上一次的识别行"算出来的 → 标记它已过期（导出回到文本口径）；
        // 但**屏幕上的语义表仍是"用户看到的那张表"**，所以视图照旧不动、也不再重复校验。
        if (CurrentGrid is not null)
        {
            // 改当前表 Cells 的唯一写入口（不再拿 CurrentGrid 直接 Refill）
            _table.RefillCurrent(_lines);
            SetCurrentTable(TableUpdate.General, _table.Grid!, what: "RefreshFromLines");
        }
        else if (CurrentSemantic is not null)
        {
            _semanticMatchesLines = false;
        }

        RebuildBoxes();
        if (status is not null)
        {
            StatusText.Text = status;
        }
    }

    /// <summary>
    /// 把文本框同步到**当前识别行**（<see cref="_lines"/>）。识别回来之后必须先做这一步，
    /// 再让任何"当前表"变更落屏 —— 否则文本框还停在旧内容，导出判据
    /// （"文本框 == 文档纯文本"）会把它判成"用户编辑过"，导出于是退回按文本切分那条路，
    /// 与屏幕上那张表分叉（0.10.2 回归的机制，见 <see cref="SetCurrentTable"/>）。
    /// </summary>
    private void SyncTextFromLines()
        => SyncTextFromLines(BuildDocument().ToPlainText());

    /// <summary>同上，但直接给已经算好的文本（<see cref="RefreshFromLines"/> 已经算过一次）。</summary>
    private void SyncTextFromLines(string text)
    {
        _suppressTextSync = true;
        TextHost.Text = text;
        _suppressTextSync = false;
    }

    private OcrDocument BuildDocument()
        => new(_image.PixelWidth, _image.PixelHeight, [.. _lines], 0);

    // ------------------------------------------------- 框 ↔ 文本映射不过期

    /// <summary>
    /// `_spans`（框 → 文本下标）在用户编辑文本之后会过期。
    /// 原来 `TextHost` 只挂了 `SelectionChanged`，没有 `TextChanged`：
    /// 用户改了文本（或补识别后整段文本被重写）之后，`_spans` 仍指向**旧文本**的下标，
    /// 于是"点某个框 → 选中的文本"选到的是别的格子（映射还指向旧文本）。
    ///
    /// 这里在文本变化的当下就把映射重建到**当前文本**上。重建口径与
    /// <see cref="OcrDocument.ToPlainTextWithSpans"/> 一致：每个识别行的文字在文本里按顺序定位；
    /// 找不到（用户把某行文字整个改掉了）就**不再重建**，宁可让映射空着
    /// —— 错位的映射比没有映射更糟（会选到不相干的文本）。
    /// </summary>
    private void OnTextHostTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextSync)
        {
            return; // 程序自己写文本（RefreshFromLines）时由调用方直接给正确的 _spans
        }

        RebuildSpansFromText();
    }

    /// <summary>按 <see cref="TextHost"/> 的当前文本重建框 → 文本下标映射（见 <see cref="OnTextHostTextChanged"/>）。</summary>
    private void RebuildSpansFromText()
    {
        var text = TextHost.Text ?? string.Empty;
        if (_lines.Count == 0 || text.Length == 0)
        {
            _spans = [];
            return;
        }

        var spans = new List<(int Start, int Length)>(_lines.Count);
        var cursor = 0;
        foreach (var line in _lines)
        {
            var current = line.Text ?? string.Empty;
            if (current.Length == 0)
            {
                spans.Add((Math.Min(cursor, text.Length), 0));
                continue;
            }

            var at = text.IndexOf(current, cursor, StringComparison.Ordinal);
            if (at < 0)
            {
                // 某行文字被用户整个改掉了：映射失去意义（框与文本已经不能一一对应）
                _spans = [];
                return;
            }

            spans.Add((at, current.Length));
            cursor = at + current.Length;
        }

        _spans = spans;
    }

    // ---------------------------------------------------------------- 渲染

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => ApplyZoom();

    /// <summary>适应窗口：按预览区大小算出缩放比，交给 LayoutTransform（框会跟着一起缩放）。</summary>
    private void ApplyZoom()
    {
        var hostWidth = PreviewHost.ActualWidth;
        var hostHeight = PreviewHost.ActualHeight;
        if (hostWidth <= 1 || hostHeight <= 1 || _image.PixelWidth <= 0 || _image.PixelHeight <= 0)
        {
            return;
        }

        // 留 16px 边距，缩放比夹在 [0.05, 8]
        var scale = Math.Min(
            (hostWidth - 16) / _image.PixelWidth,
            (hostHeight - 16) / _image.PixelHeight);
        _scale = Math.Clamp(scale, 0.05, 8);
        ZoomTransform.ScaleX = _scale;
        ZoomTransform.ScaleY = _scale;

        // 线宽按缩放反向补偿，视觉上始终约 1.2 DIP
        foreach (var shape in _boxShapes)
        {
            shape.StrokeThickness = 1.2 / _scale;
        }
    }

    private void RebuildBoxes()
    {
        BoxCanvas.Children.Clear();
        _boxShapes.Clear();
        _activeBoxIndex = -1;

        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var polygon = new Polygon
            {
                Points = [.. line.Box.Select(p => new Point(p.X, p.Y))],
                Stroke = BoxStroke,
                StrokeThickness = 1.2 / _scale,
                Fill = Brushes.Transparent,
                Tag = i,
                Cursor = Cursors.Hand,
                ToolTip = line.Text.Length > 60 ? line.Text[..60] + "…" : line.Text,
            };

            polygon.MouseLeftButtonDown += OnBoxLeftButtonDown;
            polygon.MouseRightButtonDown += OnBoxRightButtonDown;

            BoxCanvas.Children.Add(polygon);
            _boxShapes.Add(polygon);
        }
    }

    private void HighlightBox(int index)
    {
        if (_activeBoxIndex == index)
        {
            return;
        }

        if (_activeBoxIndex >= 0 && _activeBoxIndex < _boxShapes.Count)
        {
            _boxShapes[_activeBoxIndex].Stroke = BoxStroke;
            _boxShapes[_activeBoxIndex].StrokeThickness = 1.2 / _scale;
        }

        _activeBoxIndex = index;

        if (index >= 0 && index < _boxShapes.Count)
        {
            _boxShapes[index].Stroke = BoxStrokeActive;
            _boxShapes[index].StrokeThickness = 2.4 / _scale;
        }
    }

    // ---------------------------------------------------------------- 悬停工具条

    private void OnPreviewMouseEnter(object sender, MouseEventArgs e) => ShowPreviewToolbar(true);

    private void OnPreviewMouseLeave(object sender, MouseEventArgs e)
    {
        // 编辑模式下也允许工具条淡出（编辑状态由左上角提示条继续显示）
        ShowPreviewToolbar(false);
    }

    private void ShowPreviewToolbar(bool visible)
    {
        PreviewToolbar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewToolbar.Opacity = visible ? 1 : 0;
    }

    private void UpdateToolbarAvailability()
    {
        var available = _recognition.IsAvailable;
        EditButton.IsEnabled = available;
        SaveButton.IsEnabled = available;
        UpdateGridButtons();
    }

    // ---------------------------------------------------------------- 点选联动

    private void OnBoxLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_editMode || _busy)
        {
            return; // 编辑模式下左键要用来拉框，交给预览区的处理器
        }

        if (sender is Polygon { Tag: int index } && index >= 0 && index < _lines.Count)
        {
            HighlightBox(index);
            SelectTextForLine(index);
            e.Handled = true;
        }
    }

    private void SelectTextForLine(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _spans.Count)
        {
            return;
        }

        var (start, length) = _spans[lineIndex];
        _suppressTextSync = true;
        TextHost.Focus();
        TextHost.Select(start, Math.Max(1, length));
        var visualLine = TextHost.GetLineIndexFromCharacterIndex(start);
        if (visualLine >= 0)
        {
            TextHost.ScrollToLine(visualLine);
        }

        _suppressTextSync = false;
    }

    private void OnTextSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressTextSync || _spans.Count == 0)
        {
            return;
        }

        var caret = TextHost.SelectionStart;
        for (var i = 0; i < _spans.Count; i++)
        {
            var (start, length) = _spans[i];
            if (caret >= start && caret <= start + length)
            {
                HighlightBox(i);
                break;
            }
        }
    }

    // ---------------------------------------------------------------- 编辑模式

    private void OnEditClick(object sender, RoutedEventArgs e) => SetEditMode(!_editMode);

    private void SetEditMode(bool enabled)
    {
        // 与"编辑分割线"模式**互斥**：进入一个就结束另一个（按钮一亮一暗）
        if (enabled && _gridEditMode)
        {
            SetGridEditMode(false);
        }

        _editMode = enabled;
        PreviewHost.Cursor = enabled ? Cursors.Cross : Cursors.Arrow;
        HighlightModeButton(EditButton, enabled);

        if (!enabled)
        {
            CancelDrag();
        }

        StatusText.Text = enabled
            ? "编辑文本框：左键拖框补识别 · 右键点框删除 · 中键退出"
            : "已退出编辑文本框模式";
        _log.Info(enabled ? "识别结果窗口：进入编辑模式" : "识别结果窗口：退出编辑模式");
    }

    private void OnPreviewLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editMode || _busy)
        {
            return;
        }

        _dragging = true;
        _dragStart = e.GetPosition(BoxCanvas);
        RubberBand.Visibility = Visibility.Visible;
        Canvas.SetLeft(RubberBand, _dragStart.X);
        Canvas.SetTop(RubberBand, _dragStart.Y);
        RubberBand.Width = 0;
        RubberBand.Height = 0;
        PreviewHost.CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var current = e.GetPosition(BoxCanvas);
        var x = Math.Min(_dragStart.X, current.X);
        var y = Math.Min(_dragStart.Y, current.Y);
        Canvas.SetLeft(RubberBand, x);
        Canvas.SetTop(RubberBand, y);
        RubberBand.Width = Math.Abs(current.X - _dragStart.X);
        RubberBand.Height = Math.Abs(current.Y - _dragStart.Y);
    }

    private async void OnPreviewLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        PreviewHost.ReleaseMouseCapture();
        var width = RubberBand.Width;
        var height = RubberBand.Height;
        var x = Canvas.GetLeft(RubberBand);
        var y = Canvas.GetTop(RubberBand);
        RubberBand.Visibility = Visibility.Collapsed;
        _dragging = false;

        if (width < 6 || height < 6)
        {
            return; // 误点
        }

        var region = new Int32Rect(
            (int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(width), (int)Math.Round(height));
        var clamped = ClampToImage(region);
        if (clamped.Width <= 0 || clamped.Height <= 0)
        {
            return;
        }

        await RecognizeRegionAsync(clamped);
    }

    private Int32Rect ClampToImage(Int32Rect region)
    {
        var x = Math.Clamp(region.X, 0, Math.Max(0, _image.PixelWidth - 1));
        var y = Math.Clamp(region.Y, 0, Math.Max(0, _image.PixelHeight - 1));
        var width = Math.Clamp(region.Width, 1, _image.PixelWidth - x);
        var height = Math.Clamp(region.Height, 1, _image.PixelHeight - y);
        return new Int32Rect(x, y, width, height);
    }

    private void CancelDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        RubberBand.Visibility = Visibility.Collapsed;
        if (PreviewHost.IsMouseCaptured)
        {
            PreviewHost.ReleaseMouseCapture();
        }
    }

    private void OnBoxRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editMode || _busy)
        {
            return;
        }

        if (sender is Polygon { Tag: int index } && index >= 0 && index < _lines.Count)
        {
            DeleteLineAt(index);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 删掉第 index 个识别行（右键点框的数据链路）。
    /// 删完必须让"当前表"重新落格（<see cref="RefreshFromLines"/> 里做），
    /// 否则导出的还是删除前的旧投影。
    /// </summary>
    private bool DeleteLineAt(int index)
    {
        if (index < 0 || index >= _lines.Count)
        {
            return false;
        }

        var removed = _lines[index];
        _lines.RemoveAt(index);
        RefreshFromLines($"已删除 1 行（共 {_lines.Count} 行），保存后写入导出文件");
        _log.Info($"识别结果窗口：删除行 “{(removed.Text.Length > 20 ? removed.Text[..20] : removed.Text)}”");
        return true;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 滚轮按钮（中键）= 退出当前编辑模式（文本框编辑、分割线编辑都支持）
        if (e.ChangedButton == MouseButton.Middle && (_editMode || _gridEditMode))
        {
            SetEditMode(false);
            SetGridEditMode(false);
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- 保存 / 关闭

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // Shift+点击 = 另存为…（默认直接存到设置里的保存目录，与截图保存的习惯一致，不打断操作）
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (!shift)
        {
            ExportToDefaultPath();
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存识别结果",
            Filter = _format.FileDialogFilter(),
            FileName = System.IO.Path.GetFileName(_exporter.BuildDefaultPath(_format)),
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var result = _exporter.Export(BuildContent(), _format, _image, dialog.FileName);
        StatusText.Text = result.Success ? $"已另存为：{result.Path}" : $"保存失败：{result.Error}";
    }

    /// <summary>按默认路径（设置里的保存目录 + 文件名模板）保存；返回写出的路径。</summary>
    private string? ExportToDefaultPath()
    {
        var result = _exporter.Export(BuildContent(), _format, _image);
        StatusText.Text = result.Success
            ? $"已保存 {_format.ShortLabel()}：{result.Path}"
            : $"保存失败：{result.Error}";
        return result.Path;
    }

    /// <summary>
    /// 屏幕与导出读的是同一份派生结果 —— 这里是"当前表 → 导出内容"的唯一决策点。
    ///
    /// 为什么要有这一层：修复前"屏幕显示哪张表"由 <c>TableState.View</c> 决定，而"导出哪张表"
    /// 由 <see cref="BuildContent"/> 里另一段分支决定，两者的关系**只靠不变量事后核对**（0.10.2-R1）。
    /// 现在两者读同一个来源：
    ///   * 屏幕：<see cref="BindView"/> 把 `TablePreview.ItemsSource` 绑到 `TableState.View`；
    ///   * 导出：下面这**一个**方法按模式派生内容，通用/语义两档都直接读 `TableState.View`
    ///     （`TableState.View` 是包的**底层同一个列表**的只读包装，不是副本 —— 之前的教训；
    ///      起对外只暴露只读接口，见 `TableState._view`）；
    /// 于是"屏幕 == 导出"不再是一份可以被漂移的副本，而是"同源"的直接推论；
    /// 生产路径只需 O(1) 的 <see cref="IsScreenBoundToExportSource"/> 守住这个来源，
    /// 逐格往返比对留给测试路径 <see cref="IsScreenExportConsistent"/>。
    /// </summary>
    private OcrExportContent DeriveTable()
    {
        var document = BuildDocument();
        var currentText = TextHost.Text ?? string.Empty;
        var textUntouched = currentText == document.ToPlainText();

        // 语义表：只有在"它就是当前这批识别行算出来的"时候才按它导出 ——
        // 否则（后来删过框/补过识别）它已经过期，应回到文本口径（同一原则）。
        if (CurrentSemantic is { } semantic && _semanticMatchesLines)
        {
            return OcrExportContent.FromLayout(RebuildLayout(semantic.Layout, _table.View), document);
        }

        // 通用网格表：文本没被用户改过 → 按屏幕上那张表导出（排序后的顺序就是导出的顺序）
        if (CurrentGrid is { } grid && _table.View.Count > 0 && textUntouched)
        {
            return OcrExportContent.FromGrid(AsGrid(grid, _table.View), document);
        }

        return textUntouched
            ? OcrExportContent.FromDocument(document)
            : OcrExportContent.FromEditedText(currentText, document);
    }

    /// <summary>
    /// 组装导出内容（<see cref="DeriveTable"/> 的薄壳，保留这个名字因为它是导出的"正门"）：
    /// 文本框里的文字是**用户可见的最终内容**，所以它以文本框为准；
    /// 但如果用户没动过文本，就直接用**当前表**（框坐标、表格结构都最准）。
    ///
    /// 「有没有动过文本」的判据是 <c>TextHost.Text == 文档纯文本</c>：这两个值由
    /// <see cref="RefreshFromLines"/> 一起写、之后只有用户能在文本框里改，所以相等就是"没改过"。
    /// **不能**反过来拿"当前表能拼出什么文本"去比 —— 表格视图里同一视觉行的多段是用 Tab /
    /// 空格相接的、和纯文本口径不同，那样比会把刚刷新过的表判成"编辑过"，
    /// 导出于是走按文本切分那条路（实测：屏幕 42 行 × 7 列、导出 41 行 × 3 列）。
    ///
    /// 例外（同一口径）：**必须按"用户看到的那张表"导出** ——
    ///   * 通用（快速）识别的网格表：不做语义推断、不写人造表头，逐格与屏幕一致；
    ///   * 表格被排序过时：导出**跟随屏幕顺序**（排序是数据层操作，见 `TableState.View` /
    ///     `TableState.OriginalRows` 的注释）。
    /// </summary>
    private OcrExportContent BuildContent() => DeriveTable();

    /// <summary>
    /// 把"当前视图顺序"的单元格套回一份网格结构上（行列分割线仍来自原网格）。
    /// 只用于导出：不改变 <paramref name="grid"/> 本身，避免视图顺序污染分割线编辑的状态。
    ///
    /// **说明**：这里的 `view.SetCells(...)` 写的是**这份新建的副本**，不是当前表 ——
    /// 所以它不算"绕过 <see cref="TableState"/> 改当前表"（`ReferenceEquals(view, _table.Grid)` 恒为 false）。
    /// </summary>
    private static TableGrid AsGrid(TableGrid grid, IReadOnlyList<IReadOnlyList<string>> cells)
    {
        var view = new TableGrid(grid.RowLines, grid.ColumnLines, grid.Source);
        view.SetCells(cells);
        return view;
    }

    /// <summary>把"当前视图顺序"的单元格套回语义表的布局（列名/数值标记/合并都沿用原布局）。</summary>
    private static OcrTableLayout RebuildLayout(OcrTableLayout layout, IReadOnlyList<IReadOnlyList<string>> cells)
        => OcrTableLayout.FromCells(
            [.. cells.Select(r => r.ToList())],
            layout.ColumnNumeric,
            layout.ColumnLabels,
            layout.Merges,
            layout.ValidationSummary,
            layout.ValidityDetails);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// **按钮可用性的唯一推导点**：「编辑分割线 / 增强识别 / 表格区可见性」全部只由
    /// <see cref="TableState.Mode"/> 派生 —— 不再由各调用点各自维护（曾经的分散维护就是
    /// "语义表已经接管、按钮却还亮着"的来源）。
    ///
    /// 口径：
    ///   * TXT 纯文本模式：三个都禁用、表格区折叠；
    ///   * <see cref="TableMode.Semantic"/>：语义表没有网格结构 → 「编辑分割线」必须禁用；
    ///   * <see cref="TableMode.General"/> / <see cref="TableMode.None"/>：允许编辑分割线
    ///     （没表时也允许：手工画线是合法用法，会现给一份外框骨架）。
    /// </summary>
    private void UpdateGridButtons()
    {
        var textOnly = IsTextOnlyFormat;
        GridEditButton.IsEnabled = !_busy && ExpectedGridEditEnabled();
        RefreshButton.IsEnabled = !_busy && !textOnly;
        // TXT 模式禁用「增强识别」
        EnhanceButton.IsEnabled = !_busy && !textOnly && _recognition.IsAvailable;
    }

    /// <summary>
    /// 「编辑分割线」**由模式派生的期望可用性**（<see cref="UpdateGridButtons"/> 与
    /// <see cref="IsTableStateConsistent"/> 共用这一份推导 —— 两边各写一遍就是"按钮状态与不变量口径不一致"
    /// 的来源）。语义表没有网格结构 → 必须禁用；TXT → 禁用；通用表与"还没有表"（手工画线建表）→ 允许。
    /// </summary>
    private bool ExpectedGridEditEnabled() => !IsTextOnlyFormat && _table.Mode != TableMode.Semantic;

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        if (!busy)
        {
            UpdateGridButtons();
        }

        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (message is not null)
        {
            BusyText.Text = message;
            StatusText.Text = message;
        }

        EditButton.IsEnabled = !busy && _recognition.IsAvailable;
        SaveButton.IsEnabled = !busy && _recognition.IsAvailable;
        UpdateGridButtons();
    }

}
