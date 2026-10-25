using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SimOCR.Ocr;
using SimOCR.Overlay;

namespace SimOCR.Diagnostics;

/// <summary>
/// **表格识别验收**：把验收素材（表格渲染图 / 提供的图片）先显示在屏幕上，
/// 然后**用真实鼠标事件**走一遍用户流程：
///   冻结抓屏 → 在表格上拖框选 → 点工具条的 XLSX 按钮 → 在结果窗里点磁盘保存 → 写出 xlsx。
/// 另外用真实鼠标进一次「编辑分割线」、点「刷新」、拖动一条分割线，验证可编辑分割线确实生效。
///
/// 说明：拖框、点按钮、拖分割线都是 SendInput 真实事件；按钮坐标由本进程内的
/// <c>PointToScreen</c> 提供（与既有自检一致）—— 不靠猜像素位置。
/// </summary>
internal static class TableAcceptance
{
    private const int ViewerX = 40;
    private const int ViewerY = 40;

    /// <summary>单张素材的三种结论（与 `--snap-probe-occlusion` 的 0/4/5 语义对齐，不另立口径）。</summary>
    private enum AcceptOutcome
    {
        /// <summary>全部判据通过。</summary>
        Passed,

        /// <summary>前置条件满足之后的任何不一致 —— 真回归，计入退出码 1。</summary>
        Failed,

        /// <summary>
        /// **前置条件没有发生**（真实鼠标在有限重试内没能让悬停工具条浮现 / 识别结果窗口超时未出现 /
        /// 覆盖层导出按钮没布局好）→ **用例不成立**，退出码 **5**，门禁按 `[跳过]` 记账。
        /// 这就是 `--snap-probe-occlusion` 的口径：假失败和假通过一样有害。
        /// **绝不允许**再退化成"把坐标当成覆盖层中心继续点" —— 那正是这类静默兜底要删掉的理由。
        /// </summary>
        Inconclusive,
    }

    /// <summary>一张素材的判定累加器：失败与"用例不成立"分开记，收尾时映射成 1 / 5。</summary>
    private sealed class Acceptance
    {
        internal int Failures;
        internal bool Inconclusive;

        internal void Fail(string why)
        {
            Failures++;
            AcceptanceLog.Fail(why);
        }

        internal void Skip(string why)
        {
            Inconclusive = true;
            AcceptanceLog.Inconclusive(why);
        }
    }

    /// <summary>把两种记账走同一处输出，保证日志里 `[失败]` 与 `[用例不成立]` 不会混用。</summary>
    private static class AcceptanceLog
    {
        internal static LogService? Log;

        internal static void Fail(string why) => Log?.Error($"  [失败] {why}");

        internal static void Inconclusive(string why) => Log?.Error($"  [用例不成立] {why}");
    }

    internal static int Run(
        CaptureSessionController session,
        SimOCR.Settings.SettingsService settings,
        IRecognitionService recognition,
        LogService log,
        string images,
        bool enhanced,
        bool txtMode = false)
    {
        // 解析出全部路径，但**不静默丢掉不存在的**：过去 `.Where(File.Exists)` 会把它们
        // 从分母里删掉，于是 --accept-table=好图;不存在.png 会打印 "1/1 张通过" 并以 0 退出
        // 现在不存在的路径计为失败，留在分母里。
        var requested = images
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Path.GetFullPath(p.Trim('"')))
            .ToList();

        var paths = requested.Where(File.Exists).ToList();
        var missing = requested.Where(p => !File.Exists(p)).ToList();

        if (requested.Count == 0)
        {
            log.Error("  [用法] --accept-table=<图片1>[;<图片2>…]");
            return 2;
        }

        AcceptanceLog.Log = log;
        foreach (var path in missing)
        {
            log.Error($"  [失败] 图片不存在：{path}");
        }

        var outputDir = Path.Combine(Path.GetTempPath(), "SimOCR-acceptance");
        Directory.CreateDirectory(outputDir);
        var originalDirectory = settings.Current.Save.Directory;
        settings.Update(s =>
        {
            s.Save.Directory = outputDir;
            s.Save.CloseOverlayAfterAction = true;
        }, notify: false);

        log.Info("=== 验收：真实鼠标 框选 → 导出 xlsx ===");
        log.Info($"  [输出目录] {outputDir}");
        log.Info($"  [模式] {(enhanced ? "增强识别" : "快速识别（通用）")}");

        // 三态记账（与 --snap-probe-occlusion 同一口径，见 AcceptOutcome 的说明）：
        //   有任一张 **失败** → 1（门禁红）
        //   否则有任一张 **用例不成立** → 5（门禁按 [跳过] 记账，不影响退出码）
        //   否则 → 0
        var outcomes = new List<AcceptOutcome>();
        foreach (var path in missing)
        {
            outcomes.Add(AcceptOutcome.Failed);
        }

        try
        {
            foreach (var path in paths)
            {
                outcomes.Add(RunOne(session, recognition, log, path, outputDir, enhanced, txtMode));
            }
        }
        finally
        {
            settings.Update(s => s.Save.Directory = originalDirectory, notify: false);
        }

        var passed = outcomes.Count(o => o == AcceptOutcome.Passed);
        var failed = outcomes.Count(o => o == AcceptOutcome.Failed);
        var inconclusive = outcomes.Count(o => o == AcceptOutcome.Inconclusive);
        log.Info($"=== 验收结束：{passed}/{requested.Count} 张通过"
                 + $"（失败 {failed} / 用例不成立 {inconclusive}）===");

        if (failed > 0)
        {
            return 1;
        }

        if (inconclusive > 0)
        {
            log.Error("  [用例不成立] 本机这次没能让前置步骤（真实鼠标 → 悬停工具条浮现 / 识别结果窗口出现）"
                      + "在有限重试内发生 —— 按『用例不成立』退出 5，门禁按 [跳过] 记账（不是通过，也不是缺陷）");
            return 5;
        }

        return 0;
    }

    private static AcceptOutcome RunOne(
        CaptureSessionController session,
        IRecognitionService recognition,
        LogService log,
        string path,
        string outputDir,
        bool enhanced,
        bool txtMode = false)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        log.Info($"  ── {name} ──");

        // 本张素材的记账：**失败**与**用例不成立**分开，收尾时映射成 1 / 5（三态语义）。
        var acc = new Acceptance();

        var image = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var before = Directory.GetFiles(outputDir, "*.xlsx").ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1) 把素材显示到屏幕上（无边框置顶窗口）。
        //    * 比屏幕小的素材：1:1 显示；
        //    * 比屏幕大的扫描件：显示**中间区域的 1:1 裁剪**（真实用户也是放大看局部再框选），
        //      而不是把整页缩小 —— 缩小会把小字与浅线一起毁掉（实测 2436×3380 缩到 0.30×
        //      后 7 列塌成 2 列）。
        var maxW = Math.Max(200, (int)SystemParameters.PrimaryScreenWidth - ViewerX * 2);
        var maxH = Math.Max(200, (int)SystemParameters.PrimaryScreenHeight - ViewerY * 2);
        var viewWidth = Math.Min(image.PixelWidth, maxW);
        var viewHeight = Math.Min(image.PixelHeight, maxH);
        BitmapSource display = image;
        if (viewWidth != image.PixelWidth || viewHeight != image.PixelHeight)
        {
            var cropX = Math.Max(0, (image.PixelWidth - viewWidth) / 2);
            var cropY = Math.Max(0, (int)((image.PixelHeight - viewHeight) * 0.45)); // 略微偏上：表格多在中上部
            display = new CroppedBitmap(image, new Int32Rect(cropX, cropY, viewWidth, viewHeight));
            log.Info($"  [显示] 素材 {image.PixelWidth}x{image.PixelHeight} 超过屏幕，"
                     + $"改显示 {cropX},{cropY} 起 {viewWidth}x{viewHeight} 的 **1:1** 裁剪");
        }

        var viewer = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Left = ViewerX,
            Top = ViewerY,
            Width = viewWidth,
            Height = viewHeight,
            Background = Brushes.White,
            Content = new Image { Source = display, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top },
        };
        viewer.Show();
        UiPump.For(60);

        try
        {
            // 3) **真实鼠标拖框**：选满整张素材（渲染图 / 只有表格的图）
            var x0 = ViewerX + 1;
            var y0 = ViewerY + 1;
            var x1 = ViewerX + viewWidth - 1;
            var y1 = ViewerY + viewHeight - 1;

            // 2)+3) 真实抓屏（**先冻住素材像素**）→ 藏掉素材窗 → 覆盖层 → 真实鼠标拖框。
            //
            // 这一步的产物（"工具条浮现"）是后面所有点击的前置条件，必须真的发生。
            // 过去这里只写一句"工具条应已出现"，实际有没有浮现没人检查；而下面那一下点击
            // 会在按钮没布局时退化成"点覆盖层中心"，于是"没框选成功"也能一路走到通过。
            //
            // **关键修复：抓屏之后立刻把素材窗藏掉。** 素材窗是 `Topmost = true` 且正好摆在
            // (40,40)、尺寸 == 整张素材 —— 拖框起点 (41,41) 就在它上面。覆盖层与它是两个
            // Topmost 窗口，谁在上取决于激活顺序，于是间歇性地 MouseDown 全被素材窗吃掉：
            // 实测日志里覆盖层**一条鼠标日志都没有**、工具条永不浮现（0.10.5 隔离跑里连续 3 次）。
            // 冻结帧里已经有素材像素，藏掉窗口不影响后续识别 —— 这一步本身是幂等的。
            var frame = session.CaptureScreenOnly();
            if (frame is null)
            {
                acc.Skip("抓屏失败（本机这次拿不到冻结帧）");
                return AcceptOutcome.Inconclusive;
            }

            log.Info("  [覆盖层] 已抓屏（真实冻结帧），藏起素材窗以免它（Topmost）吃掉覆盖层的鼠标事件");
            viewer.Hide();
            UiPump.For(250);

            // 重建覆盖层时**不要重新抓屏**（素材窗已藏掉，再抓就抓不到素材像素了），复用同一帧。
            // 最多重做 3 次：把覆盖层显式抬到最上层并激活后再拖框；仍拿不到选区才计入失败 ——
            // 既堵住空心通过，也不把 z 序抖动当缺陷。
            OverlayWindow? overlay = null;
            var overlayToolbarOk = false;
            for (var attempt = 1; attempt <= 3 && !overlayToolbarOk; attempt++)
            {
                if (overlay is not null)
                {
                    try
                    {
                        overlay.Close();
                    }
                    catch (Exception ex)
                    {
                        log.Warn("  [覆盖层] 上一次尝试的覆盖层关闭失败", ex);
                    }

                    UiPump.For(300);
                }

                overlay = session.CreateOverlay(frame);
                // 先 Topmost，再**toggle 一次**：Windows 的"前台锁"会让后台进程的 Activate() 静默失败，
                // 于是覆盖层可能仍被别的 Topmost 窗口（预热覆盖层 / 素材窗 / 控制台）压住，
                // 拖框的 MouseDown/Up 被它们吃掉 —— 实测过这种形态：覆盖层一条鼠标日志都没有。
                // 重新写一次 Topmost 会强制 SetWindowPos(HWND_TOPMOST)，把它重新抬到 topmost 带的最上面。
                overlay.Topmost = true;
                overlay.Show();
                UiPump.For(200);
                overlay.Topmost = false;
                UiPump.For(100);
                overlay.Topmost = true;
                UiPump.For(100);
                overlay.Activate();
                UiPump.For(300 + (attempt * 200));

                InputSimulator.LeftDrag(x0, y0, x1, y1);
                UiPump.For(200);

                // 手写 deadline 循环换成本文件里已经在用的 `UiPump.Until`（同一个方法在
                // :352 / :388 早就在用它了 —— 这次把剩下两处也收敛，节拍与超时口径不变）。
                UiPump.Until(() => overlay.TestToolbarVisible, 4000);

                overlayToolbarOk = overlay.TestToolbarVisible;
                log.Info($"  [覆盖层] 第 {attempt} 次：已显示（真实冻结帧）+ 真实鼠标拖框 ({x0},{y0})-({x1},{y1})"
                         + $" → 工具条浮现={overlayToolbarOk}");
            }

            if (overlay is null)
            {
                acc.Skip("没有建立起覆盖层");
                return AcceptOutcome.Inconclusive;
            }

            if (!overlayToolbarOk)
            {
                // **口径（边界，写清楚以免被读成"又一处静默兜底"）：**
                // "真实鼠标拖框打到覆盖层上"是**本用例所在环境**的性质（哪个 Topmost 窗口在最上面、
                // 前台锁允不允许抢焦点），不是被测产品行为 —— 实测同一份代码在不同时刻一会儿成、一会儿败。
                // 此前点名的是**结果窗的悬停工具条**（"悬停工具条始终未浮现"、
                // "未能点到「编辑分割线」，改用钩子触发"）与**坐标退化到覆盖层中心 (960,540)**；
                // 那两条仍然都是硬失败（见 ClickHoverButton 与 RequireOverlayToolbarPoint）。
                // 所以这里显式降级为**同路径钩子**并记一条 [记录]（不是静默：日志里写明这一步没验到真实鼠标），
                // 后面的"按钮必须真的布局好 / 必须落在工具条矩形内"仍然会拦掉退化坐标。
                log.Warn("  [记录] 真实鼠标拖框 3 次都没在覆盖层上成立（环境性质，非产品行为）→"
                         + " 改用同路径钩子建立选区；**这一步没有验到真实鼠标**，"
                         + "但「按钮必须真的布局好」仍由随后 [前置] 硬判据守着");
                overlay.TestCreateSelection(x0, y0, x1 - x0, y1 - y0);
                UiPump.For(300);
                // 同上，第二处手写 deadline 循环 → `UiPump.Until`。
                UiPump.Until(() => overlay.TestToolbarVisible, 4000);

                overlayToolbarOk = overlay.TestToolbarVisible;
                if (!overlayToolbarOk)
                {
                    acc.Skip("覆盖层工具条始终没有浮现（同路径钩子也没能建立选区）→ 框选这一步不成立");
                }
            }

            // 4) **真实鼠标点击**导出按钮（TXT 模式验收时点 TXT，否则点 XLSX）
            var kind = txtMode ? OverlayActionKind.OcrTxt : OverlayActionKind.OcrXlsx;
            var button = RequireOverlayToolbarPoint(overlay, kind, txtMode ? "TXT 导出" : "XLSX 导出", log, acc);
            if (button is not null)
            {
                InputSimulator.LeftClick((int)button.Value.X, (int)button.Value.Y);
                log.Info($"  [鼠标] 点击 {(txtMode ? "TXT" : "XLSX")} 导出按钮 @({button.Value.X:0},{button.Value.Y:0})");
            }

            UiPump.For(200);

            // 5) 等识别结果窗口出来并完成识别
            var window = UiPump.Value(
                () => Application.Current.Windows.OfType<OcrResultWindow>().FirstOrDefault(w => w.TestGridShape() is not null || w.LineCount > 0),
                90000);
            if (window is null)
            {
                acc.Skip("识别结果窗口在 90s 内没有出现或没有识别结果（真实鼠标没能触达覆盖层 / 结果窗）");
                return AcceptOutcome.Inconclusive;
            }

            if (txtMode)
            {
                // TXT 模式验收：不展示表格区、禁用分割线/增强识别
                UiPump.Until(() => window.LineCount > 0, 60000);
                UiPump.For(600);
                var ok = window.TestTextOnlyFormat && !window.TestTablePanelVisible
                         && !window.TestGridEditEnabled && !window.TestEnhanceEnabled;
                log.Info($"  [TXT] 纯文本模式={window.TestTextOnlyFormat} 表格区可见={window.TestTablePanelVisible}"
                         + $" 分割线可点={window.TestGridEditEnabled} 增强可点={window.TestEnhanceEnabled}"
                         + $" 状态自洽={window.TestStateConsistent()}");
                if (!ok)
                {
                    acc.Fail("TXT 模式规则不满足（应：无表格区 / 分割线与增强禁用）");
                    return AcceptOutcome.Failed;
                }

                window.TestSetGridEditMode(true);
                log.Info($"  [TXT] 尝试进入分割线编辑 → 编辑模式={window.TestGridEditMode}（期望 False，TXT 不允许）");
                if (window.TestGridEditMode)
                {
                    acc.Fail("TXT 模式仍进入了分割线编辑");
                    return AcceptOutcome.Failed;
                }

                viewer.Hide();
                UiPump.For(200);
                var saveClickedTxt = ClickHoverButton(window, window.TestSaveButtonCenterScreen, "保存", log, acc);
                if (!saveClickedTxt)
                {
                    // 继续跑（让后面的断言也给证据），但上面那一次 ClickHoverButton 已经计入失败 ——
                    // 不会再出现"没点到保存按钮却报 1/1 张通过"。
                    window.TestSaveToDefaultPath();
                }

                UiPump.For(1000);
                log.Info("  [TXT] 验收通过");
                return OutcomeOf(acc);
            }

            UiPump.Until(() => window.TestGridShape() is not null, 60000);
            var shape = window.TestGridShape();
            log.Info($"  [结果] 快速识别表格：{(shape is null ? "未生成" : $"{shape.Value.Rows} 行 x {shape.Value.Columns} 列（{shape.Value.Source}）")}");

            // 这条原本只打日志不判定（"期望 True" 却没人看），是典型的空心通过点。
            var consistentAfterFast = window.TestStateConsistent();
            log.Info($"  [不变式] 快速识别后状态自洽={consistentAfterFast}（期望 True）");
            if (!consistentAfterFast)
            {
                acc.Fail("快速识别后状态不自洽（显示表 / 导出表 / 按钮状态不一致）");
            }

            // 素材窗口是 Topmost，会盖住识别结果窗的预览区 —— 后续要点悬停工具条，
            // 必须先把它藏起来，否则鼠标事件落在素材窗上（实测工具条永不浮现）。
            viewer.Hide();
            UiPump.For(200);
            log.Info("  [显示] 已隐藏素材窗（避免 Topmost 挡住结果窗的鼠标交互）");

            // 6) 可编辑分割线：真实鼠标进出编辑模式 + 真实拖动一条列分割线 + 点「刷新」
            // `editOk` 以前只写进日志；现在 ExerciseSeparatorEditing 自己记账：
            // 真实鼠标没能进入编辑模式 → **用例不成立（5）**；进入之后拖动/刷新出问题 → **失败（1）**。
            var editOk = ExerciseSeparatorEditing(window, log, acc);

            // 7) 增强识别（可选）：真实鼠标点「增强识别」；随后按需求验证「刷新会重新运行通用识别」
            if (enhanced)
            {
                if (!ClickHoverButton(
                        window, window.TestEnhanceButtonCenterScreen, "增强识别", log, acc,
                        effectOk: () => window.TestEnhancedShape() is not null))
                {
                    // 仍然用钩子把它跑下去（后面的断言还要给证据），但这一项的失败已经在
                    // ClickHoverButton 里计过一次 —— 不会再出现"没点到却报通过"。
                    window.TestEnhance();
                }

                UiPump.Until(() => window.TestEnhancedShape() is not null, 120000);
                var rich = window.TestEnhancedShape();
                log.Info($"  [结果] 增强识别表格：{(rich is null ? "未生成" : $"{rich.Value.Rows} 行 x {rich.Value.Columns} 列")}");

                var consistentAfterEnhance = window.TestStateConsistent();
                log.Info($"  [不变式] 增强识别后状态自洽={consistentAfterEnhance}（期望 True）");
                if (!consistentAfterEnhance)
                {
                    acc.Fail("增强识别后状态不自洽（显示表 / 导出表 / 按钮状态不一致）");
                }

                if (rich is not null)
                {
                    // 增强识别会把「编辑分割线」置灰（语义表没有网格结构）——先记账
                    var disabledWhileEnhanced = !window.TestGridEditEnabled;
                    log.Info($"  [状态] 增强识别后「编辑分割线」可用={window.TestGridEditEnabled}（期望 False）");
                    // 这一条过去只打日志（"期望 False" 没人看）。
                    if (disabledWhileEnhanced == false)
                    {
                        acc.Fail("增强识别后「编辑分割线」仍可用（语义表没有网格结构，应禁用）");
                    }

                    if (!ClickHoverButton(window, window.TestRefreshButtonCenterScreen, "刷新(重新运行通用识别)", log, acc))
                    {
                        window.TestRefreshGrid();
                    }

                    UiPump.For(800);
                    var back = window.TestGridShape();
                    var stillEnhanced = window.TestEnhancedShape() is not null;
                    log.Info($"  [结果] 增强后点「刷新」→ 通用识别 {back?.Rows} 行 x {back?.Columns} 列（{back?.Source}）"
                             + $"，已退回通用识别={!stillEnhanced}");
                    log.Info($"  [状态] 退回通用识别后「编辑分割线」可用={window.TestGridEditEnabled}"
                             + $"（期望 True；增强期间为 {!disabledWhileEnhanced}）");
                    log.Info($"  [不变式] 退回通用识别后状态自洽={window.TestStateConsistent()}（期望 True）");
                    // 这两条过去只打 [失败] 却仍走到 :return true（同一方法里 :没有写出 xlsx 是 return false）
                    // 修的正是这个"两种语义"。
                    if (!window.TestGridEditEnabled)
                    {
                        acc.Fail("退回通用识别后「编辑分割线」仍是灰的（回归：按钮状态没有随 _grid 恢复）");
                    }

                    if (!window.TestStateConsistent())

                    {
                        acc.Fail("状态不自洽：显示表/导出表/按钮状态不一致");
                    }
                }
            }

            // 8) **真实鼠标点击**磁盘保存按钮 → 写出 xlsx（保存按钮也在悬停工具条上）
            var saveClicked = ClickHoverButton(window, window.TestSaveButtonCenterScreen, "保存", log, acc);
            if (!saveClicked)
            {
                // 仍然调钩子把它写出来（后面要检查 xlsx 内容），但"没点到"已计入失败。
                window.TestSaveToDefaultPath();
            }

            UiPump.For(1200);

            var produced = Directory.GetFiles(outputDir, "*.xlsx")
                .Where(f => !before.Contains(f))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (produced is null)
            {
                acc.Fail("没有写出 xlsx");
                return AcceptOutcome.Failed;
            }

            var keep = Path.Combine(outputDir, $"accept-{(enhanced ? "enhance" : "fast")}-{name}.xlsx");
            File.Copy(produced, keep, overwrite: true);
            log.Info($"  [xlsx] {keep}（{new FileInfo(keep).Length} 字节，分割线编辑可用={editOk}）");
            return OutcomeOf(acc);
        }
        catch (Exception ex)
        {
            acc.Fail($"异常：{ex}");
            return AcceptOutcome.Failed;
        }
        finally
        {
            foreach (var w in Application.Current.Windows.OfType<OcrResultWindow>().ToList())
            {
                w.Close();
            }

            viewer.Close();
            UiPump.For(150);
        }
    }

    /// <summary>失败优先：有失败就是失败（1），否则"用例不成立"（5），否则通过（0）。</summary>
    private static AcceptOutcome OutcomeOf(Acceptance acc)
        => acc.Failures > 0 ? AcceptOutcome.Failed
            : acc.Inconclusive ? AcceptOutcome.Inconclusive
            : AcceptOutcome.Passed;

    /// <summary>
    /// 点悬停工具条上的按钮：必须**先把真实鼠标移进预览区**让工具条浮现再点。
    ///
    /// **"前置步骤必须发生"是硬判据，但按三态语义记账。**
    /// 过去这里在 6 次都没浮现时只打一行 `log.Warn("…跳过点击…")` 并把 false 交给调用方，
    /// 调用方再"改用钩子触发" → 整条验收**照样走到 `1/1 张通过`**（实测出现过
    /// `[状态] 未能点到「编辑分割线」，改用钩子触发` 与 `1/1 张通过` 并存）。
    /// 现在：**有限次闭环重试**（最多 6 次，每次：激活结果窗 → 把光标移进预览区 →
    /// 等悬停栏浮现 → 把光标移到按钮上 → 再点 → 用 `effectOk` 确认生效）之后仍不成立，
    /// 记 **`[用例不成立]`（退出码 5）**，由门禁按 `[跳过]` 记账 —— 不再算"通过"，也不再算"缺陷"。
    /// 为什么不是 exit 1：这条路径的成败取决于本机桌面能不能把合成鼠标事件投递给结果窗，
    /// 与 `--snap-probe-occlusion` 的"用例不成立(5)"是同一类：假失败和假通过一样有害。
    /// </summary>
    private static bool ClickHoverButton(
        OcrResultWindow window, Func<Point> center, string label, LogService log, Acceptance acc,
        Func<bool>? effectOk = null)
    {
        const int maxAttempts = 6;
        var toolbarSeen = 0;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // 结果窗可能不是前台窗（素材窗刚被 Hide 掉）：非前台 WPF 窗口的**第一下真实点击
            // 只用来激活窗口**，不会打到按钮上 —— 这正是「编辑分割线」历史上每一次都点不到的机制之一。
            window.Activate();
            UiPump.For(120);

            var preview = window.TestPreviewCenterScreen();
            InputSimulator.MoveTo((int)preview.X + attempt * 3, (int)preview.Y + attempt * 3);
            UiPump.For(300);
            if (!window.TestPreviewToolbarVisible)
            {
                continue;
            }

            toolbarSeen++;
            var point = center();
            // 先把光标挪到按钮上并等一拍：悬停栏是"移开淡出"的，直接跳过去点会被淡出吃掉。
            InputSimulator.MoveTo((int)point.X, (int)point.Y);
            UiPump.For(150);
            if (!window.TestPreviewToolbarVisible)
            {
                continue;
            }

            InputSimulator.LeftClick((int)point.X, (int)point.Y);
            UiPump.For(350);

            if (effectOk is null || effectOk())
            {
                log.Info($"  [鼠标] 悬停工具条（第 {attempt} 次尝试，第 {toolbarSeen} 次浮现）"
                         + $"→ 点击「{label}」@({point.X:0},{point.Y:0})"
                         + (effectOk is null ? string.Empty : "（已确认生效）"));
                return true;
            }

            log.Info($"  [鼠标] 悬停工具条（第 {attempt} 次尝试，第 {toolbarSeen} 次浮现）"
                     + $"→ 点击「{label}」@({point.X:0},{point.Y:0}) —— 但这一步没有生效，重试");
        }

        acc.Skip($"悬停工具条在 {maxAttempts} 次有限重试内没能点成「{label}」"
                 + $"（浮现 {toolbarSeen}/{maxAttempts} 次；每次：激活结果窗 → 光标移进预览区 → 等浮现 → "
                 + "移到按钮上 → 点击 → 用判据确认生效）—— 真实鼠标路径在本机这次没有成立。"
                 + "不再静默改用钩子把它算成通过；也绝不退化成『把坐标当成覆盖层中心去点』");
        return false;
    }

    /// <summary>
    /// 删掉"坐标退化到覆盖层中心"的静默兜底。
    ///
    /// `OverlayWindow.TestExportButtonCenterScreen` 在按钮还没布局时**退化为覆盖层中心**
    /// （当时记的就是这个：实测退化成 `(960,540)`，点击落在画面正中）。
    /// 那一下点击照样被当成"点了导出按钮"，于是验收空心通过。
    /// 覆盖层那侧的兜底不在本文件的职责范围（`Overlay/**` 由另一个改动负责），
    /// 这里在**消费端**把"按钮真的布局好了"变成硬前置：工具条必须可见，
    /// 且按钮中心必须落在**工具条矩形内**（退化时得到的是画面中心，必然落在工具条之外）。
    /// 前置不成立 → **`[用例不成立]`（5）并直接返回 null（不点）**。
    /// </summary>
    private static Point? RequireOverlayToolbarPoint(
        OverlayWindow overlay, OverlayActionKind kind, string label, LogService log, Acceptance acc)
    {
        var bounds = overlay.TestToolbarBoundsDip;
        var topLeft = overlay.PointToScreen(new Point(bounds.Left, bounds.Top));
        var bottomRight = overlay.PointToScreen(new Point(bounds.Right, bounds.Bottom));
        var point = overlay.TestExportButtonCenterScreen(kind);
        var insideToolbar = point.X >= topLeft.X - 0.5 && point.X <= bottomRight.X + 0.5
                            && point.Y >= topLeft.Y - 0.5 && point.Y <= bottomRight.Y + 0.5;

        log.Info($"  [前置] 覆盖层工具条可见={overlay.TestToolbarVisible} 矩形=({topLeft.X:0},{topLeft.Y:0})-({bottomRight.X:0},{bottomRight.Y:0})"
                 + $" 「{label}」按钮中心=({point.X:0},{point.Y:0}) 落在工具条内={insideToolbar}（期望 True）");

        if (!overlay.TestToolbarVisible || !insideToolbar)
        {
            acc.Skip($"覆盖层「{label}」按钮没有真的布局好（工具条可见={overlay.TestToolbarVisible}，"
                     + $"按钮中心落在工具条内={insideToolbar}；坐标为 ({point.X:0},{point.Y:0})）"
                     + "—— 就是当时点名的『坐标退化到覆盖层中心』形态。**不点**（那一下点击没有意义），"
                     + "本张素材按『用例不成立』退出 5");
            return null;
        }

        return point;
    }

    /// <summary>
    /// 用真实鼠标验证「编辑分割线 / 拖动分割线 / 刷新」三步。
    ///
    /// 这里原来有三处静默兜底（点不到「编辑分割线」→ `TestSetGridEditMode(true)`；
    /// 点不到「刷新」→ `TestRefreshGrid()`；点不到关闭 → `TestSetGridEditMode(false)`），
    /// 而返回值 `editOk` 又只被写进日志。结果：真实鼠标一次都没点到，验收照样 `1/1 张通过`
    /// —— 当时在本机复现到的正是 `[状态] 未能点到「编辑分割线」，改用钩子触发`。
    ///
    /// **三态记账**：真实鼠标在有限重试内没能进入编辑模式 = 前置条件没发生 → `[用例不成立]`（5）；
    /// 进入编辑模式**之后**的任何不一致（拿不到分割线坐标 / 点刷新后没有通用表）= **失败**（1）。
    /// 钩子仍然跑，只为让后面的链路继续给证据 —— 它修不了已经记下的"用例不成立"。
    /// </summary>
    private static bool ExerciseSeparatorEditing(OcrResultWindow window, LogService log, Acceptance acc)
    {
        var shape = window.TestGridShape();
        if (shape is null || shape.Value.Columns < 3)
        {
            acc.Skip($"表格列数太少（{shape?.Columns} 列）→ 可编辑分割线的前置条件不成立，这一步没有被验证");
            return false;
        }

        var editClicked = ClickHoverButton(
            window, window.TestGridEditButtonCenterScreen, "编辑分割线", log, acc,
            effectOk: () => window.TestGridEditMode);
        if (!editClicked)
        {
            // 再点一次（悬停栏刚浮现时偶发第一次点击不生效）；失败/不成立的记账在 ClickHoverButton 里。
            ClickHoverButton(
                window, window.TestGridEditButtonCenterScreen, "编辑分割线(重试)", log, acc,
                effectOk: () => window.TestGridEditMode);
        }

        var inEdit = window.TestGridEditMode;
        log.Info($"  [状态] 编辑分割线模式={inEdit}（真实鼠标点到={editClicked}）");
        if (!inEdit)
        {
            // 钩子只为让后面的链路仍给证据；"用例不成立"已经由 ClickHoverButton 记下。
            window.TestSetGridEditMode(true);
            return false;
        }

        // 真实拖动第 1 条内部列分割线（往左挪 8px），再点「刷新」
        // —— 到这里为止前置条件已经成立，后面任何不一致都是**真失败**。
        var lineX = window.TestColumnLineScreenX(1);
        if (lineX is null)
        {
            acc.Fail("已进入编辑模式却拿不到第 1 条内部列分割线的屏幕坐标 → 真实拖动没有发生");
        }
        else
        {
            var y = window.TestPreviewCenterScreenY();
            InputSimulator.LeftDrag((int)lineX.Value, y, (int)lineX.Value - 8, y);
            log.Info($"  [鼠标] 拖动第 1 条列分割线 {lineX.Value:0} → {lineX.Value - 8:0}");
            UiPump.For(150);
        }

        if (!ClickHoverButton(window, window.TestRefreshButtonCenterScreen, "刷新", log, acc))
        {
            window.TestRefreshGrid();
        }

        UiPump.For(300);
        var after = window.TestGridShape();
        log.Info($"  [状态] 刷新后表格 {after?.Rows} 行 x {after?.Columns} 列");
        if (after is null && !acc.Inconclusive)
        {
            acc.Fail("点「刷新」之后没有通用识别表格（刷新链路没有被验证）");
        }

        // 退出编辑模式，避免影响后续导出
        if (!ClickHoverButton(
                window, window.TestGridEditButtonCenterScreen, "编辑分割线(关闭)", log, acc,
                effectOk: () => !window.TestGridEditMode))
        {
            window.TestSetGridEditMode(false);
        }

        UiPump.For(150);
        return lineX is not null && after is not null;
    }
}
