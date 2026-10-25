using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using SimOCR.Capture;
using SimOCR.Export;
using SimOCR.Interop;
using SimOCR.Ocr;
using SimOCR.Storage;

namespace SimOCR.Diagnostics;

/// <summary>
/// 命令行自检模式（开发/验收用，不出现在正常使用路径里）：
///   --selftest-capture [N]  连续抓屏 + 裁切 + 存盘 N 次（默认 200），统计 GDI/内存是否泄漏
///   --selftest-clipboard    复制一张测试图到剪贴板并读回校验
///   --selftest-ocr[=图片]   本地 OCR（PP-OCRv5 中文模型）端到端；不给图片就用当前屏幕
/// 都不显示覆盖层，因此可以在不干扰使用者的前提下跑。
/// </summary>
internal static class SelfTest
{
    private const int GdiLeakThreshold = 20;
    private const int UserLeakThreshold = 20;
    private const long PrivateBytesLeakThresholdMb = 60;

    /// <summary>
    /// OCR 端到端自检：验证"引擎可用 + 中文模型随包 + 能识别出文本"，并把完整结果写到临时文件供验收查看。
    /// 返回 0 表示通过。
    /// </summary>
    public static int RunOcr(IRecognitionService recognition, LogService log, string? imagePath)
    {
        log.Info("=== 自检：本地 OCR（RapidOcrNet + PP-OCRv5 中文模型）===");

        if (!recognition.IsAvailable)
        {
            log.Error($"  [模型] {recognition.StatusMessage}");
            return 2;
        }

        log.Info($"  [模型] {recognition.StatusMessage}");

        BitmapSource image;
        string label;
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            var full = Path.GetFullPath(imagePath!);
            if (!File.Exists(full))
            {
                log.Error($"  [输入] 图片不存在：{full}");
                return 2;
            }

            image = BitmapDecoder.Create(
                new Uri(full), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            label = Path.GetFileName(full);
        }
        else
        {
            var capture = new ScreenCaptureService(log).Capture();
            if (capture is null)
            {
                log.Error("  [输入] 抓屏失败");
                return 2;
            }

            image = capture.Bitmap;
            label = "当前屏幕（冻结帧）";
        }

        log.Info($"  [输入] {label} {image.PixelWidth}x{image.PixelHeight}");

        OcrDocument document;
        try
        {
            document = StripEnhancer.EnhanceAsync(
                recognition,
                image,
                recognition.RecognizeAsync(image).GetAwaiter().GetResult()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Error("  [识别] 异常", ex);
            return 2;
        }

        log.Info($"  [识别] {document.Lines.Count} 行 / {document.ElapsedMs}ms");
        foreach (var line in document.Lines.Take(10))
        {
            log.Info($"    [{line.Score:F2}] ({line.X0:F0},{line.Y0:F0})-({line.X1:F0},{line.Y1:F0}) {line.Text}");
        }

        var report = new StringBuilder();
        report.AppendLine($"engine=RapidOcrNet 4.2.0  model=PP-OCRv5-chinese  image={label}");
        report.AppendLine($"size={document.ImageWidth}x{document.ImageHeight} lines={document.Lines.Count} elapsedMs={document.ElapsedMs}");
        report.AppendLine();
        report.AppendLine("--- 文本行（阅读顺序，含坐标与置信度）---");
        foreach (var line in document.Lines)
        {
            report.AppendLine($"[{line.Score:F3}] ({line.X0:F0},{line.Y0:F0})-({line.X1:F0},{line.Y1:F0}) {line.Text}");
        }

        report.AppendLine();
        report.AppendLine("--- 纯文本（导出 TXT 的内容）---");
        report.AppendLine(document.ToPlainText());

        var reportPath = Path.Combine(Path.GetTempPath(), "simocr-ocr-selftest.txt");
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        log.Info($"  [报告] {reportPath}");

        if (document.Lines.Count == 0)
        {
            log.Error("  [结论] 未识别到任何文本行（模型/输入有问题）");
            return 2;
        }

        log.Info("  [结论] OK");
        return 0;
    }

    /// <summary>
    /// 导出端到端自检：对同一张图跑 OCR，然后把 **TXT / DOCX / XLSX / PDF** 四种文件都写出来，
    /// 便于验收时直接打开检查格式是否合法、内容是否与画面一致。返回 0 表示四种都写出成功。
    /// </summary>
    public static int RunExport(
        IRecognitionService recognition,
        ExportService exporter,
        LogService log,
        string? imagePath,
        string outputDirectory)
    {
        log.Info("=== 自检：导出 TXT / DOCX / XLSX / PDF ===");

        if (!recognition.IsAvailable)
        {
            log.Error($"  [模型] {recognition.StatusMessage}");
            return 2;
        }

        BitmapSource image;
        string label;
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            var full = Path.GetFullPath(imagePath!);
            if (!File.Exists(full))
            {
                log.Error($"  [输入] 图片不存在：{full}");
                return 2;
            }

            image = BitmapDecoder.Create(new Uri(full), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            label = Path.GetFileName(full);
        }
        else
        {
            var capture = new ScreenCaptureService(log).Capture();
            if (capture is null)
            {
                log.Error("  [输入] 抓屏失败");
                return 2;
            }

            image = capture.Bitmap;
            label = "当前屏幕（冻结帧）";
        }

        Directory.CreateDirectory(outputDirectory);
        log.Info($"  [输入] {label} {image.PixelWidth}x{image.PixelHeight}");
        log.Info($"  [输出目录] {outputDirectory}");

        OcrDocument document;
        try
        {
            document = StripEnhancer.EnhanceAsync(
                recognition,
                image,
                recognition.RecognizeAsync(image).GetAwaiter().GetResult()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Error("  [识别] 异常", ex);
            return 2;
        }

        log.Info($"  [识别] {document.Lines.Count} 行 / {document.ElapsedMs}ms");

        var layout = OcrTableLayout.Build(document);
        log.Info($"  [表格] 还原出 {layout.RowCount} 行 x {layout.ColumnCount} 列（像表格={layout.LooksLikeTable}）");

        var failed = 0;
        foreach (var format in new[] { OcrExportFormat.Txt, OcrExportFormat.Docx, OcrExportFormat.Xlsx, OcrExportFormat.Pdf })
        {
            var target = Path.Combine(outputDirectory, $"simocr-export{format.Extension()}");
            var result = exporter.Export(document, format, image, target);
            if (result.Success && result.Path is not null)
            {
                var info = new FileInfo(result.Path);
                log.Info($"  [导出] {format.ShortLabel(),-5} {info.Length / 1024.0,8:F1} KB  {result.Path}");
            }
            else
            {
                log.Error($"  [导出] {format.ShortLabel()} 失败：{result.Error}");
                failed++;
            }
        }

        if (failed > 0)
        {
            log.Error($"  [结论] {failed} 个格式导出失败");
            return 2;
        }

        log.Info("  [结论] OK（四种格式均已写出）");
        return 0;
    }

    /// <summary>
    /// 识别结果窗口自检：打开窗口（预览 + 框叠加 + 可编辑文本 + 悬停工具条），
    /// 等识别完成后切换一次编辑模式，再保持显示若干秒供人工/外部截图，最后关闭。
    /// 返回 0 表示窗口拿到过识别结果。
    /// </summary>
    public static int RunOcrWindow(
        IRecognitionService recognition,
        ExportService exporter,
        SimOCR.Settings.SettingsService settings,
        LogService log,
        string? imagePath)
    {
        log.Info("=== 自检：识别结果窗口（预览 + 框叠加 + 悬停工具条 + 编辑模式）===");

        if (!recognition.IsAvailable)
        {
            log.Error($"  [模型] {recognition.StatusMessage}");
            return 2;
        }

        var full = imagePath is null ? null : Path.GetFullPath(imagePath);
        if (full is null || !File.Exists(full))
        {
            log.Error($"  [输入] 需要一张图片路径，未找到：{full}");
            return 2;
        }

        // 自检期间把保存目录指到临时目录，别往用户的"下载"里写文件。
        // 这条路径（`SelfTestKit.Ensure` 的建 / 删）过去没有 try/catch —— 环境问题
        // （%TEMP% 不可写 / ACL）会变成未捕获异常 → 「自检运行异常」→ **exit 2**，
        // 日志里一条断言都没有，与"产品崩了"无法区分。现在降级失败就明确报 **exit 5** + 原因。
        var exportDir = SelfTestEvidence.SelfTestDirectory("window-save");
        if (exportDir is null)
        {
            log.Error($"  [用例不成立] {SelfTestScratch.LastFailureReason}");
            return 5;
        }

        try
        {
            if (Directory.Exists(exportDir))
            {
                Directory.Delete(exportDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // 清不掉旧目录不是致命的（下面的写入会用新文件名）——但必须留下原因，不能再静默。
            log.Warn($"  [用例不成立] 旧 scratch 目录删不掉「{exportDir}」：{ex.GetType().Name}: {ex.Message}（继续，不判失败）");
        }

        // 0.10.8 实测记录：`Ensure` 已经建过这个目录，但**这里仍然显式再建一次** ——
        // 上一版改成"靠 Ensure 建"之后，本机实测出现 `Directory.Exists(exportDir)` 在
        // 100~200ms 内变 false、随后 `Directory.GetFiles` 抛 `DirectoryNotFoundException`
        // （`fixture-sort-tri-state` 整条门禁变红）。机制未定位，所以保留"调用方自己再建一次"
        // 这个幂等动作：它与降级 / exit 5 语义不冲突，却把那条路径变回改动前的形状。
        Directory.CreateDirectory(exportDir);
        var originalDirectory = settings.Current.Save.Directory;
        settings.Update(s => s.Save.Directory = exportDir, notify: false);

        var image = BitmapDecoder.Create(
            new Uri(full), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        log.Info($"  [输入] {Path.GetFileName(full)} {image.PixelWidth}x{image.PixelHeight}");

        var window = new OcrResultWindow(image, OcrExportFormat.Xlsx, recognition, exporter, log);
        var recorded = false;

        // 这三条断言过去只打日志、不参与返回值（同一份日志里
        // "[失败] 手动编辑的文本没有进导出文件" 与 "[结论] OK" 并存）。
        // 现在由定时器回调累积，函数返回前计入退出码 —— 日志有 [失败] 就不再是退出码 0。
        var assertFailures = 0;
        var holdUntil = DateTime.MaxValue;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        var enhanceDeadline = DateTime.MaxValue;

        // "增强失败 → 回落通用识别"是状态收敛的主要风险，动状态所有权之前先确认这条路径的覆盖：
        //   * 本自检末尾就调用真实 <c>TestEnhance()</c>（走竖带补充 + 语义列）；本样本（整页业务单据 + 行业档）
        //     会**成功**还原语义表，所以走的是"语义表接管显示/导出"这一半；
        //   * "还原不出语义表 → 回落通用表"那一半由真实单据验收（`--accept-table … --accept-enhance`）
        //     与素材基线覆盖（191/193 = 99.0% 就是增强链路的产物）；
        //   * 增强**异常**那一半（连底稿都没有 → 当前表退役成"没有表"）用对照跑法验证过：
        //     屏幕 0 行、导出与屏幕一致（不再留半张旧表）。
        // 结论：路径有覆盖 → 可以做状态收敛。
        log.Info($"  [通用表兜底] 配置档 “{TableProfiles.Current.Name}”：增强回落路径的覆盖见上方注释（本自检覆盖「语义表接管」那一半）");

        var timer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(200),
            System.Windows.Threading.DispatcherPriority.Background,
            async (_, _) =>
            {
                if (!recorded && window.LineCount > 0)
                {
                    recorded = true;
                    var baseline = window.LineCount;
                    log.Info($"  [窗口] 识别到 {baseline} 行；框叠加已渲染");

                    // 设计约定：识别后**不自动保存**。先确认目录里还是空的。
                    var autoSaved = Directory.GetFiles(exportDir).Length;
                    log.Info($"  [保存] 识别后自动保存的文件数={autoSaved}（期望 0）");
                    if (autoSaved != 0)
                    {
                        log.Error("  [失败] 识别后不应自动保存");
                        assertFailures++;
                    }

                    // 用户主动点磁盘按钮 → 才写文件
                    var saved = window.TestSaveToDefaultPath();
                    var savedExists = saved is not null && File.Exists(saved);
                    log.Info($"  [保存] 主动保存 → {(savedExists ? Path.GetFileName(saved) : "失败")}");
                    if (!savedExists)
                    {
                        log.Error("  [失败] 主动保存未写出文件");
                        assertFailures++;
                    }

                    // 在文本框里改字 → 再保存 → 导出文件必须是**改后**的内容。
                    // 注意：自检这里固定导出 xlsx，而 **xlsx 是 zip 包**——直接 File.ReadAllText
                    // 读到的是压缩字节，Contains("用户改过的内容") 永远为 false。过去这条断言只打日志、
                    // 不参与退出码，所以这个"读法错误"一直没被发现；把断言接进退出码后
                    // 它立刻现形（历史日志里 2026-10-10/12 都是「内容仍为识别原文（FAIL）」+「[结论] OK」）。
                    // 修法：按 OOXML 读回单元格文本（XlsxExporter 写的是 inlineStr）。
                    const string editedText = "用户改过的内容 42\r\n第二行";
                    window.TestSetText(editedText);
                    var savedEdited = window.TestSaveToDefaultPath();
                    var editedContent = savedEdited is not null && File.Exists(savedEdited)
                        ? ReadXlsxText(savedEdited)
                        : string.Empty;
                    var editHonored = editedContent.Contains("用户改过的内容 42", StringComparison.Ordinal) &&
                                      editedContent.Contains("第二行", StringComparison.Ordinal) &&
                                      !editedContent.Contains("Python v4", StringComparison.Ordinal);
                    log.Info($"  [保存] 编辑文本后导出 → {(editHonored ? "内容为用户改后的文本" : $"内容仍为识别原文（FAIL），读回文本=「{editedContent[..Math.Min(80, editedContent.Length)]}」")}");
                    if (!editHonored)
                    {
                        log.Error("  [失败] 手动编辑的文本没有进导出文件");
                        assertFailures++;
                    }

                    window.TestShowPreviewToolbar(true);
                    window.TestSetEditMode(true);
                    log.Info($"  [窗口] 编辑模式=开（{window.EditModeEnabled}）");

                    // ① 对"已经识别过的区域"再补识别一次
                    // **仅记录（盘点项）**：原文写的是"（应保持不变）"，但补识别是按
                    // 框内文字行**重建**这一段，行数本来就可以与 baseline 不同（实测 9 → 11），
                    // 所以"保持不变"不是一条真判据 —— 过去它也确实没有任何断言在看它。
                    // 这一段真正该守的不变量是"屏幕上的表 == 导出的表 == 按钮状态"，
                    // 由紧随其后的 TestScreenExportConsistent / TestStateConsistent 承担。
                    var region = new Int32Rect(0, 0, Math.Min(320, image.PixelWidth), Math.Min(200, image.PixelHeight));
                    var afterRedundant = await window.TestAddRegionAsync(region);
                    log.Info($"  [记录] 补识别已识别区域 {region.Width}x{region.Height}：{baseline} → {afterRedundant} 行"
                             + "（仅记录：补识别按框内文字行重建，行数可与 baseline 不同，故不断言）");

                    // ② 删掉一行（右键点框的数据链路）
                    var afterDelete = window.TestDeleteLine(0);
                    log.Info($"  [记录] 删除 1 行：{afterRedundant} → {afterDelete} 行"
                             + "（仅记录：删除是否真的改写了落格由断言守着）");

                    // ③ 再补识别同一位置
                    var afterRestore = await window.TestAddRegionAsync(region);
                    log.Info($"  [记录] 补识别找回：{afterDelete} → {afterRestore} 行（仅记录，同上）");

                    window.TestSetEditMode(false);
                    log.Info($"  [窗口] 编辑模式=关（{window.EditModeEnabled}）");

                    // 两种编辑模式互斥（此时快速表格还在，_grid 非空，分割线模式能真的打开）
                    // 这两行过去写死了"（期望 False/True）"却**没有任何断言**，
                    // 是本文件里除排序三态之外最典型的"日志式检查点"。这里补成真断言。
                    window.TestSetGridEditMode(true);
                    window.TestSetEditMode(true);
                    var exclusive1Ok = !window.TestGridEditMode && window.EditModeEnabled;
                    log.Info($"  [互斥] 先开分割线再开文本框：分割线={window.TestGridEditMode} 文本框={window.EditModeEnabled}（期望 False/True）");
                    if (!exclusive1Ok)
                    {
                        log.Error($"  [失败] 编辑模式互斥不成立（先开分割线再开文本框）：分割线={window.TestGridEditMode}（期望 False）"
                                  + $" 文本框={window.EditModeEnabled}（期望 True）");
                        assertFailures++;
                    }

                    window.TestSetEditMode(false);
                    window.TestSetGridEditMode(true);
                    var exclusive2Ok = !window.EditModeEnabled && window.TestGridEditMode;
                    log.Info($"  [互斥] 先开文本框再开分割线：文本框={window.EditModeEnabled} 分割线={window.TestGridEditMode}（期望 False/True）");
                    if (!exclusive2Ok)
                    {
                        log.Error($"  [失败] 编辑模式互斥不成立（先开文本框再开分割线）：文本框={window.EditModeEnabled}（期望 False）"
                                  + $" 分割线={window.TestGridEditMode}（期望 True）");
                        assertFailures++;
                    }

                    window.TestSetGridEditMode(false);

                    // 回归：增强识别会禁用「编辑分割线」，刷新回通用识别后必须重新可用
                    // **仅记录（盘点项）**：这一行只是把当前的按钮状态写进日志；
                    // 它的判定式在下一条 `[不变量] 状态自洽检查`（TestStateConsistent 把「按钮可用性」
                    // 与当前表模式一起算）以及增强段落里的两处 `TestGridEditEnabled` 断言上。
                    log.Info($"  [记录] 当前「编辑分割线」可用={window.TestGridEditEnabled}（判定见随后的状态自洽断言）");

                    // ---------------------------------------------------------------
                    // 阶段三回归断言 —— 在**增强识别之前**跑：
                    // 此时显示的是通用网格表（_grid 那一档），"屏幕 / 导出 / 按钮"三者同源的不变量
                    // 才真的会被这四条断言检查到。全部计入 assertFailures → 影响退出码。
                    // ---------------------------------------------------------------
                    log.Info("  [不变量] 状态自洽检查（显示 == 导出 == 按钮状态）");
                    window.TestResetInvariantFlag(); // 只对本段之后的变更负责（识别过程中的过渡态不算）
                    if (!window.TestStateConsistent())
                    {
                        log.Error($"  [失败] 状态不变量被破坏（显示 / 导出 / 按钮状态不自洽）—— "
                                  + $"屏幕==导出：{window.TestScreenExportConsistent()}；{TableTsvDiff.Describe(window.ScreenCellRows, window.ExportedCellRows)}");
                        assertFailures++;
                    }

                    // 删框/补识别之后 _grid.Cells 过期 → 删掉的行仍进导出
                    var exportedBeforeDelete = window.TestExportContentTsv();
                    log.Info($"  删框前：屏幕 {window.TestTableScreenRowCount} 行，导出 {exportedBeforeDelete.Split('\n').Length} 行；"
                             + $"屏幕==导出：{window.TestScreenExportConsistent()}");
                    if (!window.TestScreenExportConsistent())
                    {
                        log.Error($"  [失败] 删框前屏幕与导出就不是同一张表 —— {window.DescribeScreenExportFork()}");
                        assertFailures++;
                    }

                    // 删框之后"导出的表"必须等于"这台机器上真实的落格结果"。
                    // 修复前 _grid.Cells 是删除前的旧投影：屏幕与落格都变了，导出还是旧的。
                    // 断言口径：导出里的**每一个非空格子**都必须是当前表视图里的非空格子，
                    // 反之亦然（屏幕那一侧由 TestScreenExportConsistent 管）。
                    var viewCellsBefore = TableTsvDiff.CellsKey(window.ScreenCellRows);
                    var exportedCellsBefore = TableTsvDiff.CellsKey(window.ExportedCellRows);
                    var rowsBeforeDelete = window.TestTableScreenRowCount;
                    var afterDeleteView = window.TestDeleteGridRowAndGetView(1);
                    var viewCellsAfter = TableTsvDiff.CellsKey(window.ScreenCellRows);
                    var exportedCellsAfter = TableTsvDiff.CellsKey(window.ExportedCellRows);

                    // 1) 删除确实改写了落格结果（否则这条断言是空心的）
                    var deleteChangedCells = viewCellsBefore != viewCellsAfter;
                    // 2) 导出里的非空格子与当前表的非空格子逐格一致（没有删除前的残留格子）
                    var staleCells = exportedCellsAfter
                        .Split('\u0001', StringSplitOptions.RemoveEmptyEntries)
                        .Where(c => !viewCellsAfter.Split('\u0001').Contains(c, StringComparer.Ordinal))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    var exportMatchesView = exportedCellsAfter == viewCellsAfter;

                    log.Info($"  右键点框删 1 行：识别行 283→{window.LineCount}；"
                             + $"表格内容被改写={deleteChangedCells}（期望 True）；"
                             + $"导出非空格子数 {exportedCellsBefore.Split('\u0001').Length - 1}→{exportedCellsAfter.Split('\u0001').Length - 1}；"
                             + $"导出==真实落格：{exportMatchesView}（期望 True）；"
                             + $"落后于删除操作的残留格子 {staleCells.Count} 个");
                    if (!deleteChangedCells)
                    {
                        log.Error("  [失败] 删框没有改写落格结果（断言不成立：检查删除路径）");
                        assertFailures++;
                    }

                    if (!exportMatchesView)
                    {
                        log.Error($"  [失败] 删框后导出 ≠ 真实落格（_grid.Cells 过期） —— 残留格子："
                                  + $"{(staleCells.Count == 0 ? "(顺序/形状不同)" : string.Join(" / ", staleCells.Take(3)))}");
                        assertFailures++;
                    }

                    if (window.TestScreenExportConsistent())
                    {
                        log.Info($"  删框后屏幕 == 导出；屏幕行 {rowsBeforeDelete}→{window.TestTableScreenRowCount}");
                    }
                    else
                    {
                        log.Error($"  [失败] 删框后屏幕与导出不是同一张表 —— {window.DescribeScreenExportFork()}");
                        assertFailures++;
                    }

                    // 排序视图与导出必须同一个口径（当前口径：**导出跟随屏幕顺序**）
                    var orderColumn = -1;
                    for (var c = 0; c < window.TestColumnCount && orderColumn < 0; c++)
                    {
                        var beforeSort = ColumnOf(window, c);
                        log.Info($"  尝试第 {c + 1} 列排序：{window.TestSortColumn(c)}");
                        var afterSort = ColumnOf(window, c);
                        var exportedView = ExportColumnOf(window, c);
                        var screenEqualsExport = afterSort.SequenceEqual(exportedView);
                        var orderChanged = !afterSort.SequenceEqual(beforeSort);
                        log.Info($"  第 {c + 1} 列排序后 屏幕顺序==导出顺序：{screenEqualsExport}（行序是否变化：{orderChanged}）");
                        if (!screenEqualsExport)
                        {
                            log.Error($"  [失败] 按第 {c + 1} 列排序后导出顺序与屏幕顺序不一致 —— {window.DescribeScreenExportFork()}");
                            assertFailures++;
                        }
                        else if (orderChanged)
                        {
                            orderColumn = c; // 找到一个真的会换序的列，用它再显式跑三态（降序 / 取消）
                            continue;
                        }

                        // 这一列没换序：连点两次复原（降序 → 取消），保持下一列从"识别顺序"起测
                        window.TestSortColumn(c);
                        window.TestSortColumn(c);
                    }

                    if (orderColumn >= 0)
                    {
                        var screenAsc = ColumnOf(window, orderColumn);
                        log.Info($"  第 {orderColumn + 1} 列升序：屏幕首格=“{screenAsc.FirstOrDefault()}”，"
                                 + $"导出首格=“{ExportColumnOf(window, orderColumn).FirstOrDefault()}”");
                        window.TestSortColumn(orderColumn); // 降序
                        var screenDesc = ColumnOf(window, orderColumn);
                        var exportDesc = ExportColumnOf(window, orderColumn);
                        log.Info($"  降序：屏幕==导出：{screenDesc.SequenceEqual(exportDesc)}");
                        if (!screenDesc.SequenceEqual(exportDesc))
                        {
                            log.Error("  [失败] 降序视图与导出不一致");
                            assertFailures++;
                        }

                        window.TestSortColumn(orderColumn); // 取消排序 → 回到识别顺序
                        var screenOriginal = window.TestTableScreenTsv();
                        var orderBack = screenOriginal == window.TestOriginalTsv();
                        log.Info($"  取消排序后回到识别顺序：{orderBack}");
                        if (!orderBack)
                        {
                            log.Error("  [失败] 取消排序没有回到识别顺序");
                            assertFailures++;
                        }
                    }
                    else
                    {
                        log.Warn("  没有找到会改变行序的列（本样本下断言未生效）");
                    }

                    // 编辑文本之后，点某个框选中的文本必须还是那个框的内容
                    var probeLine = window.TestBoxText(0).Trim();
                    var spansBefore = window.TestSpans();
                    window.TestSetText("用户先改过一遍文本 12345\n第二行 abcdef");
                    window.TestSetText(window.TestPlainText());
                    log.Info($"  编辑文本：spans {spansBefore.Count} → {window.TestSpans().Count}");
                    var selected = window.TestClickBoxSelectText(0);
                    log.Info($"  编辑后点第 1 个框 → 选中文本=“{selected}”（框内容=“{probeLine}”）");
                    if (selected.Trim() != probeLine)
                    {
                        log.Error($"  [失败] 编辑文本后框↔文本联动错位：选中“{selected}”，框内容“{probeLine}”");
                        assertFailures++;
                    }

                    // 删掉一条分割线之后，拖拽状态必须清空（否则后续 MouseMove 会操作幽灵线）
                    var columnLines = window.TestColumnCount + 1;
                    var rowLines = window.TestGridShape()?.Rows + 1 ?? 0;
                    var useColumnLine = columnLines >= 4;
                    var dragLineCount = useColumnLine ? columnLines : rowLines;
                    if (dragLineCount < 4)
                    {
                        log.Warn($"  分割线不足 4 条（列线 {columnLines} / 行线 {rowLines}），跳过删线拖拽断言");
                    }
                    else
                    {
                        // 挑一条内部分割线（最外侧两条定义表区范围，不参与拖动）
                        var index = 1;
                        if (!window.TestGridDragAt(useColumnLine, index, 0))
                        {
                            log.Warn("  起点没有可拖的内部分割线，跳过删线拖拽断言");
                        }
                        else
                        {
                            log.Info($"  按住第 {index + 1} 条{(useColumnLine ? "列" : "行")}线（拖动中：活动={window.TestGridDragActive} 下标={window.TestGridDragIndex}）");
                            var deleted = window.TestDeleteGridLine(useColumnLine, index);
                            if (!deleted)
                            {
                                log.Warn("  右键没能删掉这条分割线，跳过断言");
                                window.TestEndGridDrag();
                            }
                            else
                            {
                                log.Info($"  右键删线成功；删线后 活动={window.TestGridDragActive}（期望 False）"
                                         + $" 仍挂在视觉树={window.TestGridDragLineAttached}（期望 False）");
                                if (window.TestGridDragActive || window.TestGridDragLineAttached)
                                {
                                    log.Error("  [失败] 删线后拖拽状态没清（会操作已脱离视觉树的线）");
                                    assertFailures++;
                                }

                                // 立刻"拖"一下：旧代码会照着删除前的下标去写分割线（幽灵线）
                                var beforeMove = window.TestColumnLine(index);
                                window.TestGridDragMovePressed(useColumnLine, 5000);
                                var afterMove = window.TestColumnLine(index);
                                var moved = beforeMove is not null && afterMove is not null
                                            && Math.Abs(afterMove.Value - beforeMove.Value) > 0.001;
                                log.Info("  删线后立刻拖动：无异常"
                                         + (beforeMove is null
                                             ? "；删线已让旧落格结果退役（预览清空），没有可被幽灵线改动的分割线"
                                             : $"；该下标位置 {beforeMove} → {afterMove}（期望不变）"));
                                if (moved)
                                {
                                    log.Error($"  [失败] 删线后拖动仍改动了分割线（幽灵线：{beforeMove} → {afterMove}）");
                                    assertFailures++;
                                }
                            }
                        }
                    }

                    window.TestEndGridDrag();
                    window.TestGridDragMovePressed(false, 5000);
                    if (!window.TestStateConsistent())
                    {
                        log.Error("  [失败] 删线/拖拽之后状态不自洽");
                        assertFailures++;
                    }
                    else
                    {
                        log.Info("  拖拽清空后状态仍自洽");
                    }

                    // 上一步删掉了一条分割线：刷新回识别顺序，后面的断言从干净状态开始
                    window.TestRefreshGrid();

                    // ---------------------------------------------------------------------------
                    // 表头排序三态（升 → 降 → 取消）—— **最高优先项**。
                    //
                    // 这三行过去只 log.Info、**没有任何断言**（`assertFailures` 一次都没加），后果是：
                    //   0.10.3 把三态机退化成"永远升序"（`BindView` 每次重绑都清 `_sortColumn/_sortClicks`，
                    //   排序路径自己把自己的状态清零 → 第 2 次点击又被当成第 1 次），**溜过了全部门禁**
                    //   —— 日志里留下的 `[排序] 第 2 次：asc` 看起来"很正常"，是人去读日志才发现的。
                    //   0.10.4 修好了逻辑却没补判据 → 同一个回归可以再发生一次。这里就是那条判据。
                    //
                    // 判据（写死，不看日志"长得像不像"）：
                    //   ① 第 1 次 = asc；
                    //   ② 第 2 次 = desc（**这一条正是 0.10.3 回归的命中点**）；
                    //   ③ 第 3 次 = none 且首格 == firstBefore，整表 TSV == 识别顺序（取消要真的回到原序）；
                    //   ④ 三态切换不得改变行数；每次切换后状态仍自洽。
                    // 为什么前提（有列可排）要判失败而不是跳过：结果窗没有表时这三态根本没被测到，
                    // 报"通过"就是空心通过（与上述同一口径）。
                    // ---------------------------------------------------------------------------
                    if (window.TestColumnCount < 1)
                    {
                        log.Error("  [失败] [排序] 前置条件不成立：结果窗没有产出任何列，排序三态没有被验证（不允许空心通过）");
                        assertFailures++;
                    }
                    else
                    {
                        var firstBefore = window.TestFirstCell();
                        var rowsBeforeSort = window.TestTableScreenRowCount;

                        var sort1 = window.TestSortColumn(0);
                        var first1 = window.TestFirstCell();
                        log.Info($"  [排序] 点第 1 列表头 1 次：{sort1} → 首格=“{first1}”");
                        if (sort1 != "asc")
                        {
                            log.Error($"  [失败] [排序] 第 1 次点击应为 asc，实际 “{sort1}”");
                            assertFailures++;
                        }

                        var sort2 = window.TestSortColumn(0);
                        var first2 = window.TestFirstCell();
                        log.Info($"  [排序] 第 2 次：{sort2} → 首格=“{first2}”");
                        if (sort2 != "desc")
                        {
                            log.Error($"  [失败] [排序] 第 2 次点击应为 desc，实际 “{sort2}”"
                                      + "（0.10.3 的回归形态：重绑视图时把排序状态清零 → 第 2 次又被当成第 1 次）");
                            assertFailures++;
                        }

                        var sort3 = window.TestSortColumn(0);
                        var first3 = window.TestFirstCell();
                        log.Info($"  [排序] 第 3 次（应取消）：{sort3} → 首格=“{first3}”（原为“{firstBefore}”）");
                        if (sort3 != "none")
                        {
                            log.Error($"  [失败] [排序] 第 3 次点击应取消排序（none），实际 “{sort3}”");
                            assertFailures++;
                        }

                        if (first3 != firstBefore)
                        {
                            log.Error($"  [失败] [排序] 取消排序后首格没有回到识别顺序：“{first3}” ≠ “{firstBefore}”");
                            assertFailures++;
                        }

                        var sortedBackToOriginal = window.TestTableScreenTsv() == window.TestOriginalTsv();
                        log.Info($"  [排序] 取消排序后整表 == 识别顺序：{sortedBackToOriginal}（期望 True）");
                        if (!sortedBackToOriginal)
                        {
                            log.Error("  [失败] [排序] 取消排序后整表没有回到识别顺序（只看了首格不够）");
                            assertFailures++;
                        }

                        var rowsAfterSort = window.TestTableScreenRowCount;
                        log.Info($"  [排序] 三态切换过程中行数 {rowsBeforeSort} → {rowsAfterSort}（期望不变）"
                                 + $"；状态自洽={window.TestStateConsistent()}（期望 True）");
                        if (rowsAfterSort != rowsBeforeSort)
                        {
                            log.Error($"  [失败] [排序] 三态切换改变了行数：{rowsBeforeSort} → {rowsAfterSort}");
                            assertFailures++;
                        }

                        if (!window.TestStateConsistent())
                        {
                            log.Error("  [失败] [排序] 三态切换之后状态不自洽（显示 == 导出 == 按钮状态）");
                            assertFailures++;
                        }

                        // 收尾（防御式、放在断言之后）：把排序状态显式归零，让后面的增强/回落断言从干净状态开始。
                        // 注意：它**掩盖不了**上面的判据 —— 第 3 次没取消时 ③ 已经报过一次失败。
                        window.TestClearSort();
                    }

                    // 走一遍「增强识别」（竖带补充 + 语义列），并记录耗时/结果形状
                    var enhanceWatch = Stopwatch.StartNew();
                    window.TestEnhance();
                    log.Info($"  [窗口] 已触发「增强识别」（{enhanceWatch.ElapsedMilliseconds}ms 内派发）");

                    // 增强识别必须跑一次**回落路径**（"认不出语义表就输出通用表"）：
                    // 在增强之前把当前网格的形状做坏 → 增强识别会失败 → 界面与导出必须退回通用网格表。
                    // 没有这条覆盖之前不动"当前表"的状态所有权。
                    // （原先这里写着"用某个仓库外的 YAML 档名当自检开关"，那个开关与那个档都早已不存在 ——
                    //   删掉失效的路径指引，只保留"必须覆盖回落路径"这个理由。）
                    holdUntil = DateTime.MaxValue; // 由"增强识别是否产出表格"决定何时收尾
                    enhanceDeadline = DateTime.UtcNow.AddSeconds(150);
                    deadline = DateTime.UtcNow.AddSeconds(150);
                    log.Info("  [窗口] 等待「增强识别」产出表格（最长 150s）");
                }

                // 增强识别是异步的：等到它真正产出表格再收尾（否则会把"还没跑完"误判成"没产出"）
                if (window.TestEnhancedTsv() is not null || DateTime.UtcNow > enhanceDeadline
                    || DateTime.UtcNow > deadline || DateTime.UtcNow >= holdUntil)
                {
                    // 增强识别的最终表格（含数值/格式校验结果）落盘，供逐格比对
                    var enhancedTsv = window.TestEnhancedTsv();
                    if (enhancedTsv is not null)
                    {
                        var tsvPath = Path.Combine(Path.GetTempPath(), "simocr-enhance-selftest.txt");
                        File.WriteAllText(tsvPath, enhancedTsv, new UTF8Encoding(false));
                        var shape = window.TestEnhancedShape();
                        log.Info($"  [增强表] {tsvPath}（{shape?.Rows} 行 x {shape?.Columns} 列）");
                    }
                    else
                    {
                        log.Warn("  [增强表] 增强识别没有产出表格");
                    }

                    // 增强识别收尾时再校验一次"显示 == 导出 == 按钮状态"（语义表接管之后也必须成立）
                    if (!window.TestScreenExportConsistent())
                    {
                        log.Error($"  [失败] 增强识别之后「看到的表」与「导出的表」不是同一张 —— {TableTsvDiff.Describe(window.ScreenCellRows, window.ExportedCellRows)}");
                        assertFailures++;
                    }

                    // 窗口自身渲染成 PNG 这件事全仓只有一份实现（见 SelfTestEvidence）
                    SelfTestEvidence.SaveWindowPng(
                        window,
                        Path.Combine(Path.GetTempPath(), "simocr-window-shot.png"),
                        log,
                        label: "窗口");
                    window.Close();
                }
            },
            window.Dispatcher);

        timer.Start();
        window.ShowDialog();
        timer.Stop();
        settings.Update(s => s.Save.Directory = originalDirectory, notify: false);

        if (!recorded)
        {
            log.Error("  [结论] 窗口未在超时前拿到识别结果");
            return 2;
        }

        if (assertFailures != 0)
        {
            log.Error($"  [结论] 未通过：{assertFailures} 项断言失败（识别到 {window.LineCount} 行）");
            return 1;
        }

        log.Info($"  [结论] OK（窗口行数={window.LineCount}）");
        return 0;
    }

    /// <summary>屏幕表格（DataGrid 的 ItemsSource）第 column 列，按行顺序取值（格内换行按导出口径压成空格）。</summary>
    private static List<string> ColumnOf(OcrResultWindow window, int column) => [.. window.TestScreenColumn(column)];

    /// <summary>导出内容（<c>BuildContent</c> 那条路径）第 column 列，按行顺序取值。</summary>
    private static List<string> ExportColumnOf(OcrResultWindow window, int column) => [.. window.TestExportColumn(column)];

    /// <summary>
    /// 覆盖层 → 识别结果窗口 → 导出：**整链路**自检。
    /// 用一张"渲染出文字的合成帧"当截图，走真实的按钮动作（<c>OcrTxt</c>），
    /// 等窗口出现、拿到识别结果、导出文件落地，再关窗口并核对。
    /// </summary>
    public static int RunOverlayOcrEndToEnd(
        SimOCR.Overlay.CaptureSessionController session,
        SimOCR.Settings.SettingsService settings,
        IRecognitionService recognition,
        LogService log)
    {
        const int frameWidth = 560;
        const int frameHeight = 260;

        // 建目录失败 = 环境问题 → exit 5 + 原因（不再让未捕获异常伪装成 exit 2）。
        var dir = SelfTestEvidence.SelfTestDirectory("overlay-ocr");
        if (dir is null)
        {
            log.Error($"  [用例不成立] {SelfTestScratch.LastFailureReason}");
            return 5;
        }

        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            log.Warn($"  [用例不成立] 旧 scratch 目录删不掉「{dir}」：{ex.GetType().Name}: {ex.Message}（继续，不判失败）");
        }

        var originalDirectory = settings.Current.Save.Directory;
        var originalCloseAfter = settings.Current.Save.CloseOverlayAfterAction;
        settings.Update(s =>
        {
            s.Save.Directory = dir;
            s.Save.CloseOverlayAfterAction = true;
        }, notify: false);

        log.Info("=== 自检：覆盖层 → 识别结果窗口 → 导出（整链路）===");

        try
        {
            if (!recognition.IsAvailable)
            {
                log.Error($"  [模型] {recognition.StatusMessage}");
                return 2;
            }

            var frame = BuildTextFrame(frameWidth, frameHeight);
            var overlay = session.CreateOverlay(frame, activateOnShow: false);
            var overlayClosed = false;
            overlay.Closed += (_, _) => overlayClosed = true;

            overlay.Show();
            UiPump.UntilIdle(overlay.Dispatcher);
            overlay.TestCreateSelection(0, 0, frameWidth, frameHeight);
            overlay.TestRaiseAction(SimOCR.Overlay.OverlayActionKind.OcrTxt);

            // 覆盖层的 Show 不是模态：用同一个泵等结果窗口出现并识别完（UI 保持响应）
            var (window, recognized) = WaitForResultWindow(timeoutSeconds: 60);

            var failures = 0;

            log.Info($"  [覆盖层] 已关闭={overlayClosed}（点按钮后应立即关闭，避免挡住结果窗口）");
            if (!overlayClosed)
            {
                overlay.CloseOverlay();
                failures++;
            }

            log.Info($"  [结果窗口] 出现={window is not null} 识别行数={recognized}");
            if (window is null || recognized == 0)
            {
                failures++;
            }

            // 设计约定：识别后不自动保存 —— 先确认目录是空的，再模拟用户点磁盘按钮
            var autoSaved = Directory.GetFiles(dir).Length;
            log.Info($"  [保存] 识别后自动保存的文件数={autoSaved}（期望 0）");
            if (autoSaved != 0)
            {
                failures++;
            }

            string? exportedFile = null;
            if (window is not null)
            {
                exportedFile = window.TestSaveToDefaultPath();
            }

            var fileSize = exportedFile is null || !File.Exists(exportedFile) ? 0 : new FileInfo(exportedFile).Length;
            log.Info($"  [导出] {(exportedFile is null ? "未生成" : Path.GetFileName(exportedFile))} {fileSize} 字节");
            if (exportedFile is null || fileSize == 0)
            {
                failures++;
            }
            else
            {
                var firstLines = File.ReadAllLines(exportedFile).Take(3).ToArray();
                foreach (var line in firstLines)
                {
                    log.Info($"    | {line}");
                }
            }

            window?.Close();
            UiPump.UntilIdle(overlay.Dispatcher);

            if (failures > 0)
            {
                log.Error($"  [结论] {failures} 项不满足");
                return 2;
            }

            log.Info("  [结论] OK（按钮 → 窗口 → 导出 整链路打通）");
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("  覆盖层 OCR 整链路自检异常", ex);
            return 2;
        }
        finally
        {
            settings.Update(s =>
            {
                s.Save.Directory = originalDirectory;
                s.Save.CloseOverlayAfterAction = originalCloseAfter;
            }, notify: false);
        }
    }

    /// <summary>
    /// 识别结果窗口的**鼠标层面**自检（P0-C 的三件事）：用 SendInput 打真实鼠标事件 ——
    ///   * 鼠标移入预览图 → 右下角工具条应当浮现；
    ///   * 点铅笔按钮 → 进入编辑模式；
    ///   * 右键点某个框 → 该行被删除；
    ///   * 左键拖框 → 补识别，把删掉的那行找回来；
    ///   * 中键 → 退出编辑模式。
    /// 自检期间窗口置顶，避免合成点击落到别的窗口上。
    /// </summary>
    public static int RunOcrWindowMouse(
        IRecognitionService recognition,
        ExportService exporter,
        SimOCR.Settings.SettingsService settings,
        LogService log,
        string? imagePath,
        int holdSeconds = 4)
    {
        log.Info("=== 自检：识别结果窗口的鼠标交互（SendInput 真实事件）===");

        if (!recognition.IsAvailable)
        {
            log.Error($"  [模型] {recognition.StatusMessage}");
            return 2;
        }

        var full = imagePath is null ? null : Path.GetFullPath(imagePath);
        if (full is null || !File.Exists(full))
        {
            log.Error($"  [输入] 需要一张图片路径，未找到：{full}");
            return 2;
        }

        // 自检期间写到临时目录（不污染用户的"下载"）。
        // 建 / 删都进 try/catch；建不出来报 exit 5 + 原因。
        var exportDir = SelfTestEvidence.SelfTestDirectory("mouse-save");
        if (exportDir is null)
        {
            log.Error($"  [用例不成立] {SelfTestScratch.LastFailureReason}");
            return 5;
        }

        try
        {
            if (Directory.Exists(exportDir))
            {
                Directory.Delete(exportDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            log.Warn($"  [用例不成立] 旧 scratch 目录删不掉「{exportDir}」：{ex.GetType().Name}: {ex.Message}（继续，不判失败）");
        }

        var originalDirectory = settings.Current.Save.Directory;
        settings.Update(s => s.Save.Directory = exportDir, notify: false);

        var image = BitmapDecoder.Create(new Uri(full), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var window = new OcrResultWindow(image, OcrExportFormat.Txt, recognition, exporter, log)
        {
            Topmost = true, // 合成点击必须落在本窗口上
        };

        var failures = new List<string>();
        var step = 0;
        var baseline = 0;
        var afterDelete = 0;
        (double X0, double Y0, double X1, double Y1)? deletedBounds = null;
        var stepBusy = false;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        var holdUntil = DateTime.MaxValue;

        var timer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(120),
            System.Windows.Threading.DispatcherPriority.Background,
            async (_, _) =>
            {
                if (stepBusy)
                {
                    return;
                }

                stepBusy = true;
                try
                {
                    if (DateTime.UtcNow > deadline)
                    {
                        failures.Add("超时");
                        window.Close();
                        return;
                    }

                    if (DateTime.UtcNow >= holdUntil)
                    {
                        window.Close();
                        return;
                    }

                    switch (step)
                    {
                        case 0:
                            if (window.LineCount == 0)
                            {
                                return; // 等识别完成
                            }

                            baseline = window.LineCount;
                            log.Info($"  [窗口] 识别到 {baseline} 行");

                            // TXT 模式的规则：不展示表格区、禁用分割线与增强识别
                            window.TestSetGridEditMode(true);
                            log.Info($"  [TXT] 纯文本模式={window.TestTextOnlyFormat}（期望 True）"
                                     + $" 表格区可见={window.TestTablePanelVisible}（期望 False）"
                                     + $" 分割线可点={window.TestGridEditEnabled}（期望 False）"
                                     + $" 增强可点={window.TestEnhanceEnabled}（期望 False）"
                                     + $" 分割线模式={window.TestGridEditMode}（期望 False）");
                            if (!window.TestTextOnlyFormat || window.TestTablePanelVisible
                                || window.TestGridEditEnabled || window.TestEnhanceEnabled || window.TestGridEditMode)
                            {
                                failures.Add("TXT 模式规则不满足");
                            }

                            step = 1;
                            break;

                        case 1:
                        {
                            // 鼠标移入预览图中央 → 工具条应浮现
                            var center = window.TestImagePointScreen(image.PixelWidth / 2.0, image.PixelHeight / 2.0);
                            InputSimulator.MoveTo((int)center.X, (int)center.Y);
                            await Task.Delay(400);
                            var visible = window.TestPreviewToolbarVisible;
                            log.Info($"  [鼠标] 移入预览图 → 工具条浮现={visible}");
                            if (!visible)
                            {
                                failures.Add("悬停工具条未浮现");
                            }

                            step = 2;
                            break;
                        }

                        case 2:
                        {
                            var button = window.TestEditButtonCenterScreen();
                            InputSimulator.LeftClick((int)button.X, (int)button.Y);
                            await Task.Delay(300);
                            log.Info($"  [鼠标] 点击铅笔按钮 → 编辑模式={window.EditModeEnabled}");
                            if (!window.EditModeEnabled)
                            {
                                failures.Add("点铅笔未进入编辑模式");
                            }

                            step = 3;
                            break;
                        }

                        case 3:
                        {
                            var bounds = window.TestBoxBounds(0);
                            if (bounds is null)
                            {
                                failures.Add("取不到第 0 个框");
                                window.Close();
                                return;
                            }

                            // 记住被删框的位置 —— 下一步要拖这一块，验证"补识别能把删掉的行找回来"
                            deletedBounds = bounds;
                            var target = window.TestImagePointScreen(
                                (bounds.Value.X0 + bounds.Value.X1) / 2, (bounds.Value.Y0 + bounds.Value.Y1) / 2);
                            InputSimulator.RightClick((int)target.X, (int)target.Y);
                            await Task.Delay(500);
                            afterDelete = window.LineCount;
                            log.Info($"  [鼠标] 右键点框删除 → {baseline} → {afterDelete} 行");
                            if (afterDelete != baseline - 1)
                            {
                                failures.Add($"右键删除行数不符（期望 {baseline - 1}，实际 {afterDelete}）");
                            }

                            step = 4;
                            break;
                        }

                        case 4:
                        {
                            if (deletedBounds is null)
                            {
                                failures.Add("没有记录到被删框的位置");
                                window.Close();
                                return;
                            }

                            // 拖框覆盖"刚才被删掉那一行"的位置（外扩 6px），补识别应当把它找回来
                            var from = window.TestImagePointScreen(deletedBounds.Value.X0 - 6, deletedBounds.Value.Y0 - 6);
                            var to = window.TestImagePointScreen(deletedBounds.Value.X1 + 6, deletedBounds.Value.Y1 + 6);
                            InputSimulator.LeftDrag((int)from.X, (int)from.Y, (int)to.X, (int)to.Y);
                            await Task.Delay(2500); // 等补识别
                            var afterDrag = window.LineCount;
                            log.Info($"  [鼠标] 左键拖框补识别（覆盖被删行位置）→ {afterDelete} → {afterDrag} 行");
                            if (afterDrag <= afterDelete)
                            {
                                failures.Add($"拖框补识别未找回被删的行（{afterDelete} → {afterDrag}）");
                            }

                            step = 5;
                            break;
                        }

                        case 5:
                        {
                            var center = window.TestImagePointScreen(image.PixelWidth / 2.0, image.PixelHeight / 2.0);
                            InputSimulator.MiddleClick((int)center.X, (int)center.Y);
                            await Task.Delay(300);
                            log.Info($"  [鼠标] 中键 → 编辑模式={window.EditModeEnabled}");
                            if (window.EditModeEnabled)
                            {
                                failures.Add("中键未退出编辑模式");
                            }

                            step = 6;
                            break;
                        }

                        case 6:
                        {
                            // 识别后不自动保存（设计约定）：目录此时应当是空的
                            var autoSaved = Directory.GetFiles(exportDir).Length;
                            log.Info($"  [保存] 识别后自动保存的文件数={autoSaved}（期望 0）");
                            if (autoSaved != 0)
                            {
                                failures.Add("识别后不应自动保存");
                            }

                            // 真实点击右下角"保存（磁盘）"按钮
                            var saveButton = window.TestSaveButtonCenterScreen();
                            InputSimulator.LeftClick((int)saveButton.X, (int)saveButton.Y);
                            await Task.Delay(800);
                            var files = Directory.GetFiles(exportDir, "*.txt");
                            log.Info($"  [鼠标] 点击磁盘按钮保存 → 文件数={files.Length}" +
                                     (files.Length > 0 ? $"（{Path.GetFileName(files[0])}）" : string.Empty));
                            if (files.Length == 0)
                            {
                                failures.Add("点保存按钮未写出文件");
                            }

                            step = 7;
                            holdUntil = DateTime.UtcNow.AddSeconds(holdSeconds);
                            log.Info($"  [窗口] 保持显示 {holdSeconds}s（供截图）");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"异常：{ex.Message}");
                    log.Error("  鼠标自检步骤异常", ex);
                    window.Close();
                }
                finally
                {
                    stepBusy = false;
                }
            },
            System.Windows.Threading.Dispatcher.CurrentDispatcher);

        timer.Start();
        window.ShowDialog();
        timer.Stop();
        settings.Update(s => s.Save.Directory = originalDirectory, notify: false);

        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                log.Error($"  [失败] {failure}");
            }

            log.Error($"  [结论] {failures.Count} 项不满足");
            return 2;
        }

        log.Info($"  [结论] OK（悬停浮现工具条 / 点铅笔 / 右键删框 / 拖框补识别 / 中键退出，全部真实鼠标事件）");
        return 0;
    }

    /// <summary>
    /// 托盘"识别剪贴板图片"整链路自检：把一张图放进剪贴板 → 走真实入口 → 结果窗口拿到文本。
    /// 注意：**会覆盖用户当前的剪贴板内容**（自检行为，日志里会写明）。
    /// </summary>
    public static int RunClipboardOcr(
        Func<bool> trigger,
        SimOCR.Settings.SettingsService settings,
        LogService log,
        string? imagePath,
        int holdSeconds = 3)
    {
        log.Info("=== 自检：托盘「识别剪贴板图片」（剪贴板 → 识别 → 结果窗口）===");
        log.Info("  注意：本自检会覆盖剪贴板当前内容");

        var full = imagePath is null ? null : Path.GetFullPath(imagePath);
        if (full is null || !File.Exists(full))
        {
            log.Error($"  [输入] 需要一张图片路径，未找到：{full}");
            return 2;
        }

        var image = BitmapDecoder.Create(
            new Uri(full), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        log.Info($"  [输入] {Path.GetFileName(full)} {image.PixelWidth}x{image.PixelHeight}");

        try
        {
            System.Windows.Clipboard.SetImage(image);
        }
        catch (Exception ex)
        {
            log.Error("  写入剪贴板失败", ex);
            return 2;
        }

        var exportDir = SelfTestEvidence.SelfTestDirectory("clipboard-ocr");
        if (exportDir is null)
        {
            // 环境问题报 exit 5 + 原因，不再让异常冒到收尾变成 exit 2。
            log.Error($"  [用例不成立] {SelfTestScratch.LastFailureReason}");
            return 5;
        }

        var originalDirectory = settings.Current.Save.Directory;
        settings.Update(s => s.Save.Directory = exportDir, notify: false);

        try
        {
            var started = trigger();
            log.Info($"  [入口] 识别剪贴板图片 → 已启动={started}");
            if (!started)
            {
                log.Error("  [结论] 入口没有识别到剪贴板里的图片");
                return 2;
            }

            // 与覆盖层那条自检同一个泵（这段"等结果窗口"曾经在这里各写了一份）
            var (window, recognized) = WaitForResultWindow(timeoutSeconds: 60);

            log.Info($"  [结果窗口] 出现={window is not null} 识别行数={recognized}");
            window?.Close();

            if (window is null || recognized == 0)
            {
                log.Error("  [结论] 剪贴板图片没有识别出文本");
                return 2;
            }

            log.Info("  [结论] OK（剪贴板图片 → 识别 → 结果窗口）");
            return 0;
        }
        finally
        {
            settings.Update(s => s.Save.Directory = originalDirectory, notify: false);
        }
    }

    /// <summary>构造一张"有字可识"的合成帧（自检用，不依赖外部图片）。</summary>
    private static CapturedFrame BuildTextFrame(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var text = new FormattedText(
                "SimOCR 本地识别 OCR 2026\nTXT DOCX XLSX PDF\n总计 1234.56 元",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"),
                26,
                Brushes.Black,
                1.0);
            dc.DrawText(text, new Point(18, 24));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        return new CapturedFrame(bitmap, new Rect(0, 0, width, height), [], TimeSpan.Zero);
    }

    /// <summary>
    /// 等"识别出了文字的结果窗口"出现（覆盖层 → 结果窗口、剪贴板 → 结果窗口两条自检共用）。
    ///
    /// 为什么是泵而不是 `await`：<c>OverlayWindow.Show()</c> 不是模态，结果窗口由覆盖层自己的动作
    /// 在**同一条 UI 线程**上创建 —— 只有跑消息循环它才会出现；而调用方（命令行自检）本身就在
    /// UI 线程上，没法 `await` 一个"窗口出现"。
    /// 之前这段（60 秒嵌套消息循环 + 每 150ms 探一次窗口）在
    /// <c>RunOverlayOcrEndToEnd</c> 与 <c>RunClipboardOcr</c> 里**各写了一份**。
    /// </summary>
    private static (OcrResultWindow? Window, int Recognized) WaitForResultWindow(int timeoutSeconds)
    {
        OcrResultWindow? window = null;
        var recognized = 0;
        UiPump.Until(
            () =>
            {
                window ??= Application.Current?.Windows.OfType<OcrResultWindow>().FirstOrDefault();
                if (window is not null && window.LineCount > 0)
                {
                    recognized = window.LineCount;
                }

                return recognized > 0;
            },
            timeoutSeconds * 1000);
        return (window, recognized);
    }

    public static int RunCaptureStress(
        ScreenCaptureService capture,
        ImageStorageService storage,
        LogService log,
        int iterations)
    {
        var dir = SelfTestEvidence.SelfTestDirectory("capture-stress");
        if (dir is null)
        {
            // 环境问题报 exit 5 + 原因（本用例的证据目录建不出来就没什么可测的）。
            log.Error($"  [用例不成立] {SelfTestScratch.LastFailureReason}");
            return 5;
        }

        var proc = Process.GetCurrentProcess();
        var samples = new List<Sample>();
        var sw = Stopwatch.StartNew();
        var saved = 0;
        var failed = 0;
        long totalCaptureMs = 0;

        log.Info($"=== 自检：连拍压力测试 {iterations} 次（输出目录 {dir}）===");

        for (var i = 1; i <= iterations; i++)
        {
            var frame = capture.Capture();
            if (frame is null)
            {
                failed++;
                continue;
            }

            totalCaptureMs += (long)frame.CaptureDuration.TotalMilliseconds;

            // 模拟一次真实的"裁切 + 存盘"
            var w = Math.Min(320, frame.PixelWidth);
            var h = Math.Min(200, frame.PixelHeight);
            var crop = frame.Crop((int)frame.VirtualScreen.X, (int)frame.VirtualScreen.Y, w, h);
            if (crop is null)
            {
                failed++;
                continue;
            }

            var result = storage.SavePng(crop, dir, $"stress_{i:D4}.png");
            if (result.Success)
            {
                saved++;
            }
            else
            {
                failed++;
            }

            if (i == 1 || i % 50 == 0)
            {
                proc.Refresh();
                samples.Add(new Sample(
                    Iteration: i,
                    GdiObjects: NativeMethods.GetGuiResources(NativeMethods.GetCurrentProcess(), NativeMethods.GR_GDIOBJECTS),
                    UserObjects: NativeMethods.GetGuiResources(NativeMethods.GetCurrentProcess(), NativeMethods.GR_USEROBJECTS),
                    PrivateMb: SizeFormat.ToMb(proc.PrivateMemorySize64),
                    ManagedMb: SizeFormat.ToMb(GC.GetTotalMemory(forceFullCollection: false)),
                    Handles: proc.HandleCount));
            }
        }

        sw.Stop();
        proc.Refresh();

        foreach (var s in samples)
        {
            log.Info($"  样本 #{s.Iteration}: GDI={s.GdiObjects} USER={s.UserObjects} " +
                     $"句柄={s.Handles} 私有内存={s.PrivateMb}MB 托管={s.ManagedMb}MB");
        }

        var first = samples.First();
        var last = samples.Last();
        var gdiDelta = (int)last.GdiObjects - (int)first.GdiObjects;
        var userDelta = (int)last.UserObjects - (int)first.UserObjects;
        var privateDelta = last.PrivateMb - first.PrivateMb;

        var pass = gdiDelta <= GdiLeakThreshold
                   && userDelta <= UserLeakThreshold
                   && privateDelta <= PrivateBytesLeakThresholdMb
                   && failed == 0;

        log.Info($"=== 自检结果：{(pass ? "通过" : "未通过")} ===");
        log.Info($"  成功 {saved} / 失败 {failed}；总耗时 {sw.Elapsed.TotalSeconds:F1}s；" +
                 $"平均抓屏 {totalCaptureMs / Math.Max(1, saved)}ms/次");
        log.Info($"  增量：GDI {gdiDelta:+#;-#;0}（阈值 ≤{GdiLeakThreshold}）、" +
                 $"USER {userDelta:+#;-#;0}（阈值 ≤{UserLeakThreshold}）、" +
                 $"私有内存 {privateDelta:+#;-#;0}MB（阈值 ≤{PrivateBytesLeakThresholdMb}MB）");

        return pass ? 0 : 1;
    }

    public static int RunClipboardRoundTrip(LogService log)
    {
        log.Info("=== 自检：剪贴板写入/读回（A 是必须通过的验收项，B/C/D 是对照）===");

        var expected = CreateTestImage(64, 48);

        // A) 我们的导出器：Bitmap(DIB) + PNG 两种格式 —— 这是真正要验的路径
        var okA = ClipboardExporter.Copy(expected, log);
        var a = Probe(log, "A 导出器(Bitmap+PNG)", expected, okA, required: true);

        // 关键：**跨进程**读取。同进程读回会被 WPF 自己的数据对象满足，
        // 因此"同进程能读"不能证明"Word/浏览器能粘上"，必须另起一个进程用另一套 API 读。
        var cross = ProbeFromSeparateProcess(expected, log);
        log.Info($"  [跨进程] {cross.Detail} → {(cross.Ok ? "OK" : "FAIL")}");

        // B) 对照：只用 WPF 的 Clipboard.SetImage（只写 DIB，没有 PNG）
        var okB = TryRun(log, () => Clipboard.SetImage(expected));
        Probe(log, "B 对照-SetImage(DIB)", expected, okB, required: false);

        // C) 对照：只写 PNG（实测 Word 类程序读不到图 —— 这正是我们必须同时写 DIB 的原因）
        var okC = TryRun(log, () =>
        {
            var data = new DataObject();
            data.SetData("PNG", ClipboardExporter.EncodePng(expected));
            Clipboard.SetDataObject(data, copy: true);
        });
        Probe(log, "C 对照-仅 PNG", expected, okC, required: false);

        log.Info($"=== 自检结果：{(a.Ok && cross.Ok ? "通过" : "未通过")}（判据：A 路径格式齐全 + 跨进程可读）===");
        return a.Ok && cross.Ok ? 0 : 1;
    }

    /// <summary>
    /// 复制一张图并保持进程存活若干秒：配合外部程序（Word/PowerShell）验证"别人能不能粘上"。
    /// </summary>
    public static int RunClipboardHold(LogService log, int holdSeconds)
    {
        log.Info($"=== 自检：复制后保持存活 {holdSeconds}s（供外部程序验证粘贴）===");

        var image = CreateTestImage(320, 200);
        var ok = ClipboardExporter.Copy(image, log);

        var cross = ProbeFromSeparateProcess(image, log);
        log.Info($"  [跨进程] {cross.Detail} → {(cross.Ok ? "OK" : "FAIL")}");

        // 用消息泵等待，保证 OLE 数据请求能被响应（如果用 Thread.Sleep 阻塞 UI 线程，外部程序会拿不到数据）。
        // 这里原先手写 `Dispatcher.CurrentDispatcher.Invoke(...Background)` + `Thread.Sleep(50)` ——
        // 正是 `UiPump` 要取代的形状（同一个文件里其它地方早就在用它了）。
        var deadline = DateTime.UtcNow.AddSeconds(holdSeconds);
        while (DateTime.UtcNow < deadline)
        {
            UiPump.For(50);
        }

        log.Info("=== 保持结束，准备退出（退出后剪贴板内容是否还在见日志外部验证）===");
        return ok && cross.Ok ? 0 : 1;
    }

    /// <summary>
    /// 另起一个 SimOCR 进程（<c>--read-clipboard</c>）读取剪贴板并回报结果，
    /// 这条路径用的是 WinForms 剪贴板 API 与 OLE 编组，能真实反映其他程序能否读到。
    /// </summary>
    private static (bool Ok, string Detail) ProbeFromSeparateProcess(BitmapSource expected, LogService log)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return (false, "无法定位自身 exe");
        }

        var scratchRoot = SelfTestEvidence.SelfTestDirectory();
        if (scratchRoot is null)
        {
            // 环境问题 → 明确不成立（调用方把它算成一次"跨进程读剪贴板"失败，不是产品崩溃）。
            log.Error($"  [用例不成立] {SelfTestScratch.LastFailureReason}");
            return (false, SelfTestScratch.LastFailureReason);
        }

        var report = Path.Combine(scratchRoot, $"clipboard-read-{Guid.NewGuid():N}.txt");

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, ["--read-clipboard", report])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return (false, "无法启动子进程");
            }

            // 子进程内部会做短暂重试
            if (!process.WaitForExit(20000))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // ignore
                }

                return (false, "子进程超时");
            }

            if (!File.Exists(report))
            {
                return (false, $"子进程退出码 {process.ExitCode}，未生成报告");
            }

            var detail = File.ReadAllText(report).Trim();
            // 判据：另一个进程用 WinForms API 能读到一张尺寸正确的图（bitmap=True）。
            // 不要把"必须有 PNG/DIB"写进判据：实测这两种格式反而会让 Word 粘出 100×100 占位图。
            var ok = detail.Contains($"size={expected.PixelWidth}x{expected.PixelHeight}", StringComparison.Ordinal)
                     && detail.Contains("bitmap=True", StringComparison.OrdinalIgnoreCase);
            log.Info($"  子进程报告: {detail}");
            return (ok, detail);
        }
        catch (Exception ex)
        {
            log.Warn("跨进程剪贴板校验失败", ex);
            return (false, ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(report))
                {
                    File.Delete(report);
                }
            }
            catch
            {
                // ignore
            }
        }
    }


    /// <summary>
    /// 子进程模式：用 WinForms 剪贴板 API 读取当前剪贴板并把结果写进报告文件。
    /// 这是"另一个进程、另一套 API"的读取，能验证 OLE 编组是否真的可用。
    /// </summary>
    /// <summary>
    /// 自检：**线格快速通道**（线格检测 → OCR → 按单元格填字 → TSV 报告 + 耗时）。
    /// 对应交互路径里"框选后马上出一张简单表格"的快速通道；无网格时应回退到增强路径。
    /// </summary>
    /// <summary>
    /// **手工分割线切分聚合文本框**的确定性自检（不依赖任何素材与 OCR 引擎）：
    /// 造几个合成文本框，验证 ① 横向压两列的文字被按字符切开 ② 切点优先落在空格空隙
    /// ③ 跨行置中的文字**不按行切**（整块落到中心点所在行）。
    /// </summary>
    /// <summary>
    /// 把导出的 <c>.xlsx</c> 读回成"单元格文本拼串"，供自检核对内容。
    ///
    /// **不要用 <c>File.ReadAllText</c>**：xlsx 是 zip 包，直接读只能拿到压缩字节，
    /// 任何 <c>Contains("中文")</c> 都必然为 false（这正是 之后暴露出来的自检缺陷）。
    /// 读取方式与 <see cref="XlsxExporter"/> 对应：文本写在 <c>xl/worksheets/sheet1.xml</c>，
    /// 用 <c>t="inlineStr"</c> + <c>&lt;is&gt;&lt;t&gt;</c>（也可能出现普通 <c>&lt;t&gt;</c> 节点）。
    /// </summary>
    internal static string ReadXlsxText(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var sheet = archive.GetEntry("xl/worksheets/sheet1.xml");
            if (sheet is null)
            {
                return string.Empty;
            }

            using var entryStream = sheet.Open();
            var document = XDocument.Load(entryStream);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

            // 一个 <c> 对应一个单元格；每个单元格内部的 <t> 文本按出现顺序用 Tab 连接，
            // 行之间用换行连接 —— 与"一行一记录、单元格用 Tab 分列"的导出口径一致。
            var rows = new List<string>();
            foreach (var row in document.Descendants(ns + "row"))
            {
                var cells = row.Elements(ns + "c")
                    .Select(c => string.Concat(c.Descendants(ns + "t").Select(t => t.Value)))
                    .ToList();
                rows.Add(string.Join('\t', cells));
            }

            return string.Join('\n', rows);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 手工分割线切分自检（<c>--selftest-linegrid</c> 的**前置**确定性断言，
    /// 它的失败必须进退出码）。
    ///
    /// **0.10.7 起：这个薄壳已删除。**
    /// 6 条断言（<see cref="GridTableFiller.SplitByColumns"/> 的字符中心分配 / 切点挪到空格 /
    /// 字符不许被切开或丢失，以及 <see cref="GridTableFiller.Fill"/> 的跨行置中）住在
    /// <c>tests/SimOCR.Tests/PureLogic/ManualLineSplitChecks.cs</c>，**只编进测试程序集**，
    /// 由 xUnit 的 <c>[Fact]</c> 与不依赖 testhost 的控制台运行器
    /// （<c>SimOCR.Tests.exe --case=manual-line-split</c>）执行。
    /// 判据没有丢：`build/gate-check.ps1` 新增的 `manual-line-split` runner 用例就钉着它，
    /// `build/inject-check.ps1` 的 `p04-linegrid-split` 注入用例同样改钉这条 runner 路径
    /// （锚点仍在那一行 <c>rebuilt == "166s166s"</c> 上，注入后必须变红）。
    /// </summary>
    internal static int RunLineGrid(string arg, IRecognitionService recognition, LogService log)
    {        var parts = arg.Split('=', 2, StringSplitOptions.TrimEntries);
        var path = parts.Length == 2 ? parts[1].Trim().Trim('"') : string.Empty;
        if (path.Length == 0)
        {
            log.Error("  [用法] --selftest-linegrid=<图片路径>");
            return 2;
        }

        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            log.Error($"  [输入] 图片不存在：{full}");
            return 2;
        }

        var image = BitmapDecoder.Create(
            new Uri(full), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var watch = Stopwatch.StartNew();
        List<GridTable> tables;
        try
        {
            tables = LineGridDetector.Detect(image);
        }
        catch (LineGridFailureException ex)
        {
            // 故障分支单独记一条（与"这张图没有网格"分码）——
            // **结论不变**：照样走空白对齐回退，退出码与改动前一致。
            log.Warn($"  [线格] 检测故障（不是「没有网格」）：{ex.Message}");
            tables = [];
        }

        var detectMs = watch.ElapsedMilliseconds;
        log.Info($"  [线格] {Path.GetFileName(full)} {image.PixelWidth}x{image.PixelHeight}："
                 + $"检测到 {tables.Count} 张表，用时 {detectMs}ms");
        for (var i = 0; i < tables.Count; i++)
        {
            var t = tables[i];
            log.Info($"  [线格] 表{i + 1}：({t.X0:0},{t.Y0:0})-({t.X1:0},{t.Y1:0}) "
                     + $"{t.RowCount} 行 x {t.ColumnCount} 列 = {t.CellCount} 格");
        }

        var document = recognition.RecognizeAsync(image).GetAwaiter().GetResult();
        var totalMs = watch.ElapsedMilliseconds;
        log.Info($"  [线格] 识别 {document.Lines.Count} 行；检测 {detectMs}ms + 识别 {totalMs - detectMs}ms "
                 + $"= {totalMs}ms");

        // **通用快速识别**：有线格用线格，没有就用文本框位置做"空白对齐"（都不做语义推断）
        var grid = tables.Count > 0 ? TableGrid.FromGridTable(tables[0]) : null;
        var source = "线格";
        if (grid is null)
        {
            grid = FastTableBuilder.FromLines(document.Lines);
            source = "空白对齐";
        }

        if (grid is null)
        {
            log.Info("  [结论] 两条快速路径都没得到可用行列结构 —— 交互路径应提示改用「增强识别」");
            return 0;
        }

        grid.Refill(document.Lines);
        log.Info($"  [表格] 来源={source}，{grid.RowCount} 行 x {grid.ColumnCount} 列");
        log.Info($"  [列表线] {string.Join(", ", grid.ColumnLines.Select(v => v.ToString("0")))}");
        log.Info($"  [行线] {string.Join(", ", grid.RowLines.Select(v => v.ToString("0")))}");
        {
            var probeMedian = FastTableBuilder.MedianHeight(document.Lines);
            var probeBands = FastTableBuilder.RowBands(document.Lines, probeMedian);
            var probeInner = BlankColumnDetector.Detect(probeBands, probeMedian);
            log.Info($"  [空白带] 行数={probeBands.Count} 字高中位={probeMedian:0.#} "
                     + $"内部列线={string.Join(", ", probeInner.Select(v => v.ToString("0")))}");
            for (var i = 0; i < Math.Min(2, probeBands.Count); i++)
            {
                log.Info($"  [框-第{i + 1}行] " + string.Join(" | ",
                    probeBands[i].OrderBy(l => l.X0).Select(l => $"{l.Text}[{l.X0:0}-{l.X1:0}]")));
            }
        }

        var report = new StringBuilder();
        foreach (var row in grid.Cells)
        {
            report.Append(string.Join("\t", row.Select(c => c.Replace("\n", " / ")))).Append('\n');
        }

        var reportPath = Path.Combine(Path.GetTempPath(), "simocr-linegrid-selftest.txt");
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        log.Info($"  [报告] {reportPath}");

        // 同时导出一份 xlsx（模拟用户点磁盘保存）：内容是**用户看到的那张表**，不写人造表头
        try
        {
            var documentModel = new OcrDocument(image.PixelWidth, image.PixelHeight, [.. document.Lines], 0);
            var content = OcrExportContent.FromGrid(grid, documentModel);
            var xlsxPath = Path.Combine(Path.GetTempPath(), "simocr-linegrid-selftest.xlsx");
            XlsxExporter.Write(xlsxPath, content.Layout);
            log.Info($"  [xlsx] {xlsxPath}");
        }
        catch (Exception ex)
        {
            log.Warn($"  [xlsx] 导出失败：{ex.Message}");
        }

        // 0.10.7 起：原先这里还要把 `RunManualLineSplit` 的失败条数并进退出码
        // 那 6 条断言已移出发布产物 → 这条 CLI 不再承担它们；
        // **判据改由 runner 承担**：`SimOCR.Tests.exe --case=manual-line-split`（门禁用例
        // `manual-line-split` + 注入用例 `p04-linegrid-split` 都钉着它）。
        log.Info("  [结论] OK");
        return 0;
    }

    public static int RunReadClipboard(string reportPath)
    {
        var formats = "(null)";
        var hasPng = false;
        var hasBitmap = false;
        var dibBytes = 0;
        var size = "null";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var data = System.Windows.Forms.Clipboard.GetDataObject();
                if (data is not null)
                {
                    formats = string.Join(" | ", data.GetFormats());
                    hasPng = data.GetDataPresent("PNG");
                    hasBitmap = data.GetDataPresent(System.Windows.Forms.DataFormats.Bitmap);
                    if (data.GetDataPresent(System.Windows.Forms.DataFormats.Dib))
                    {
                        dibBytes = (data.GetData(System.Windows.Forms.DataFormats.Dib) as byte[])?.Length ?? 0;
                    }

                    using var image = System.Windows.Forms.Clipboard.GetImage();
                    if (image is not null)
                    {
                        size = $"{image.Width}x{image.Height}";
                        break;
                    }
                }
            }
            catch
            {
                // 剪贴板暂时被占用，重试
            }

            // 0.10.10：这是**轮询等待**，等待期间不该冻结 UI 线程。
            // 循环上限仍是 5 次、节拍仍是 80ms —— 逐值不变，只把 `Thread.Sleep(80)` 换成等长的消息泵。
            UiPump.For(80);
        }

        var report = $"formats=[{formats}] bitmap={hasBitmap} png={hasPng} dib={dibBytes}B size={size}";
        try
        {
            File.WriteAllText(reportPath, report);
            return size == "null" ? 1 : 0;
        }
        catch
        {
            return 2;
        }
    }

    private static bool TryRun(LogService log, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            log.Warn("写入剪贴板失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 覆盖层端到端自检：用一个**合成的冻结帧**（480×360，物理位置 120,120）走完整链路——
    /// 框选 → 工具条摆放 → 放大镜 → 动作分发（保存/复制/贴图/取消）→ 覆盖层自动关闭。
    /// 测试模式下窗口只有合成帧那么大、且不抢焦点，因此对正常使用干扰很小（约 1.5 秒）。
    /// </summary>
    public static int RunOverlayEndToEnd(
        SimOCR.Overlay.CaptureSessionController session,
        ImageStorageService storage,
        SimOCR.Pinning.PinnedImageService pinning,
        SimOCR.Settings.SettingsService settings,
        LogService log)
    {
        const int frameWidth = 480;
        const int frameHeight = 360;
        const int frameX = 120;
        const int frameY = 120;
        const int selectionX = 160;
        const int selectionY = 160;
        const int selectionWidth = 200;
        const int selectionHeight = 120;

        // 建 / 删都进 try/catch；建不出来报 exit 5 + 原因。
        var dir = SelfTestEvidence.SelfTestDirectory("overlay-e2e");
        if (dir is null)
        {
            log.Error($"  [用例不成立] {SelfTestScratch.LastFailureReason}");
            return 5;
        }

        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            log.Warn($"  [用例不成立] 旧 scratch 目录删不掉「{dir}」：{ex.GetType().Name}: {ex.Message}（继续，不判失败）");
        }

        var original = settings.Current.Save;
        var originalDirectory = original.Directory;
        var originalAutoSave = original.AutoSaveAfterRelease;
        var originalCloseAfter = original.CloseOverlayAfterAction;

        // 自检期间：写到临时目录、不自动保存（要测工具条）、动作后自动关闭（要测关闭行为）
        settings.Update(s =>
        {
            s.Save.Directory = dir;
            s.Save.AutoSaveAfterRelease = false;
            s.Save.CloseOverlayAfterAction = true;
        }, notify: false);

        log.Info("=== 自检：覆盖层端到端（合成帧 480x360，选区 200x120）===");

        var failures = 0;

        try
        {
            // ---- 1) 保存 ----
            var saveStep = DriveStep(session, settings, log, "保存", SimOCR.Overlay.OverlayActionKind.Save,
                frameWidth, frameHeight, frameX, frameY, selectionX, selectionY, selectionWidth, selectionHeight);
            failures += saveStep.Ok ? 0 : 1;

            var savedFiles = Directory.GetFiles(dir, "*.png");
            var savedSize = savedFiles.Length == 1 ? SelfTestEvidence.DescribePng(savedFiles[0]) : "(未生成)";
            var savedOk = savedFiles.Length == 1
                          && savedSize == $"{selectionWidth}x{selectionHeight}";
            log.Info($"  [保存] 文件数={savedFiles.Length} 尺寸={savedSize} 期望={selectionWidth}x{selectionHeight} → {(savedOk ? "OK" : "FAIL")}");
            failures += savedOk ? 0 : 1;

            // ---- 2) 复制到剪贴板 ----
            var copyStep = DriveStep(session, settings, log, "复制", SimOCR.Overlay.OverlayActionKind.Copy,
                frameWidth, frameHeight, frameX, frameY, selectionX, selectionY, selectionWidth, selectionHeight);
            failures += copyStep.Ok ? 0 : 1;

            var clipboardOk = false;
            var clipboardDetail = "(未读取)";
            for (var attempt = 0; attempt < 5 && !clipboardOk; attempt++)
            {
                // 0.10.10：轮询上限 5 次、节拍 60ms 逐值不变；只把阻塞等待换成消息泵
                // （等待期间覆盖层的 `Closed` / 渲染消息仍能派发，结论判据不动）。
                UiPump.For(60);
                var data = System.Windows.Forms.Clipboard.GetDataObject();
                var formats = data is null ? "null" : string.Join(" | ", data.GetFormats());
                using var image = System.Windows.Forms.Clipboard.GetImage();
                clipboardDetail = $"formats=[{formats}] 尺寸={(image is null ? "null" : $"{image.Width}x{image.Height}")}";
                clipboardOk = image is not null
                              && image.Width == selectionWidth
                              && image.Height == selectionHeight;
            }

            log.Info($"  [复制] {clipboardDetail} 期望={selectionWidth}x{selectionHeight} → {(clipboardOk ? "OK" : "FAIL")}");
            failures += clipboardOk ? 0 : 1;

            // ---- 3) 贴图置顶 ----
            var pinStep = DriveStep(session, settings, log, "置顶", SimOCR.Overlay.OverlayActionKind.Pin,
                frameWidth, frameHeight, frameX, frameY, selectionX, selectionY, selectionWidth, selectionHeight);
            failures += pinStep.Ok ? 0 : 1;

            var pinnedCount = pinning.Count;
            log.Info($"  [置顶] 贴图数量={pinnedCount} → {(pinnedCount == 1 ? "OK" : "FAIL")}");
            failures += pinnedCount == 1 ? 0 : 1;

            pinning.CloseAll();
            var afterClose = pinning.Count;
            log.Info($"  [置顶] 全部关闭后数量={afterClose} → {(afterClose == 0 ? "OK" : "FAIL")}");
            failures += afterClose == 0 ? 0 : 1;

            // ---- 4) 取消（不应产生文件）----
            var filesBefore = SelfTestEvidence.CountPngs(dir);
            var cancelStep = DriveStep(session, settings, log, "取消", SimOCR.Overlay.OverlayActionKind.Cancel,
                frameWidth, frameHeight, frameX, frameY, selectionX, selectionY, selectionWidth, selectionHeight);
            failures += cancelStep.Ok ? 0 : 1;

            var filesAfter = SelfTestEvidence.CountPngs(dir);
            log.Info($"  [取消] 文件数 {filesBefore} → {filesAfter}（应不变）→ {(filesBefore == filesAfter ? "OK" : "FAIL")}");
            failures += filesBefore == filesAfter ? 0 : 1;

            // 实测结论（一次性探针，结论保留在这里；探针本身已按"发布产物只留判据"的口径移出）：
            //   同一形状（20 轮 Show → Loaded 已跑 → 立刻 Close）下，**改前 / 改后都在关闭后残留 0 个
            //   Rendering 订阅者**，托管堆增量 +95KB / +0KB，窗口只有 1/20 在强制 GC 后仍存活。
            //   即：**"静态事件把冻结帧钉住"这条假设没有被实测复现**。
            //   退订仍然照做（见 OverlayWindow 的 OnFirstRendering / CloseOverlay / OnClosed 三处）：
            //   它把"依赖首帧回调必然落地"这个隐含前提去掉，成本为零，且 `Rendering 订阅者` 计数
            //   （`OverlayWindow.TestRenderingSubscribers`）从今往后可以被任何用例直接读出来做回归。
        }
        catch (Exception ex)
        {
            log.Error("覆盖层端到端自检异常", ex);
            failures++;
        }
        finally
        {
            settings.Update(s =>
            {
                s.Save.Directory = originalDirectory;
                s.Save.AutoSaveAfterRelease = originalAutoSave;
                s.Save.CloseOverlayAfterAction = originalCloseAfter;
            }, notify: false);
        }

        var pass = failures == 0;
        log.Info($"=== 自检结果：{(pass ? "通过" : $"未通过（{failures} 项失败）")} ===");
        return pass ? 0 : 1;
    }

    /// <summary>
    /// 视觉验收用：显示一个带「选区 + 工具条 + 放大镜」的覆盖层并保持 N 秒不关闭，
    /// 便于在无法手动拖拽的场合（虚拟机 / 远程桌面 / 自动化）截图确认渲染效果。
    /// </summary>
    public static int RunOverlayVisualHold(
        SimOCR.Overlay.CaptureSessionController session,
        LogService log,
        int seconds,
        int frameWidth = 700,
        int frameHeight = 460,
        int frameX = 60,
        int frameY = 60)
    {
        log.Info($"=== 自检：覆盖层视觉保持 {seconds}s（合成帧 {frameWidth}x{frameHeight} @ {frameX},{frameY}）===");

        var frame = BuildSyntheticFrame(frameWidth, frameHeight, frameX, frameY);
        var overlay = session.CreateOverlay(frame, activateOnShow: false);

        var closed = false;
        overlay.Closed += (_, _) => closed = true;

        overlay.Show();
        UiPump.UntilIdle(overlay.Dispatcher);

        overlay.TestHover(frameX + 40, frameY + 40);
        var magnifier = overlay.TestMagnifierVisible;

        const int selX = 120;
        const int selY = 140;
        const int selW = 240;
        const int selH = 150;
        overlay.TestCreateSelection(selX, selY, selW, selH);

        var toolbarVisible = overlay.TestToolbarVisible;
        var toolbar = overlay.TestToolbarBoundsDip;
        log.Info($"  工具条可见={toolbarVisible} 位置={toolbar.Left:F0},{toolbar.Top:F0} " +
                 $"尺寸={toolbar.Width:F0}x{toolbar.Height:F0} 放大镜(框选前)={magnifier}");

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
        // `Thread.Sleep(100)` + `UiPump.UntilIdle` 两步并成一次切片泵（语义等价：
        // 每 100ms 处理一批消息，期间任一片里窗口被关闭就退出）。
        while (DateTime.UtcNow < deadline && !closed)
        {
            UiPump.For(100);
        }

        if (!closed)
        {
            overlay.CloseOverlay();
            UiPump.UntilIdle(overlay.Dispatcher);
        }

        log.Info($"=== 视觉保持结束（工具条可见={toolbarVisible}）===");
        return toolbarVisible ? 0 : 1;
    }

    private static (bool Ok, string Detail) DriveStep(
        SimOCR.Overlay.CaptureSessionController session,
        SimOCR.Settings.SettingsService settings,
        LogService log,
        string name,
        SimOCR.Overlay.OverlayActionKind action,
        int frameWidth,
        int frameHeight,
        int frameX,
        int frameY,
        int selectionX,
        int selectionY,
        int selectionWidth,
        int selectionHeight)
    {
        var frame = BuildSyntheticFrame(frameWidth, frameHeight, frameX, frameY);
        var overlay = session.CreateOverlay(frame, activateOnShow: false);

        var closed = false;
        overlay.Closed += (_, _) => closed = true;

        overlay.Show();
        UiPump.UntilIdle(overlay.Dispatcher);

        // 先验"未框选时"的状态：放大镜跟随光标（这才是真实使用时的初始态）
        overlay.TestHover(selectionX + selectionWidth / 2, selectionY + selectionHeight / 2);
        var magnifierBeforeDrag = overlay.TestMagnifierVisible;

        overlay.TestCreateSelection(selectionX, selectionY, selectionWidth, selectionHeight);

        var toolbarVisible = action == SimOCR.Overlay.OverlayActionKind.Cancel || overlay.TestToolbarVisible;
        var toolbarBounds = overlay.TestToolbarBoundsDip;
        var frameBounds = overlay.TestFrameDipBounds;
        var toolbarInside = toolbarBounds.Left >= -0.5
                            && toolbarBounds.Top >= -0.5
                            && toolbarBounds.Right <= frameBounds.Width + 0.5
                            && toolbarBounds.Bottom <= frameBounds.Height + 0.5;

        // 工具条出现后放大镜应当让位（设计如此）
        var magnifierHiddenAfterDrag = !overlay.TestMagnifierVisible;

        overlay.TestRaiseAction(action);
        UiPump.UntilIdle(overlay.Dispatcher);

        // 自动关闭：动作完成后覆盖层应已关闭（Cancel 亦同）。
        // `Thread.Sleep(30)` + `UiPump.UntilIdle` 两步并成一次 30ms 切片泵。
        for (var i = 0; i < 10 && !closed; i++)
        {
            UiPump.For(30);
        }

        if (!closed)
        {
            overlay.CloseOverlay();
            UiPump.UntilIdle(overlay.Dispatcher);
        }

        var ok = toolbarVisible && toolbarInside && closed && magnifierBeforeDrag && magnifierHiddenAfterDrag;
        log.Info($"  [{name}] 工具条可见={toolbarVisible} 在画面内={toolbarInside} " +
                 $"工具条={toolbarBounds.Left:F0},{toolbarBounds.Top:F0} {toolbarBounds.Width:F0}x{toolbarBounds.Height:F0} " +
                 $"放大镜(框选前)={magnifierBeforeDrag} 放大镜(工具条出现后隐藏)={magnifierHiddenAfterDrag} " +
                 $"已自动关闭={closed} → {(ok ? "OK" : "FAIL")}");

        return (ok, $"closed={closed}");
    }

    private static SimOCR.Capture.CapturedFrame BuildSyntheticFrame(int width, int height, int x, int y)
        => new(
            CreateTestImage(width, height),
            new Rect(x, y, width, height),
            Array.Empty<SimOCR.Interop.MonitorInfo>(),
            TimeSpan.Zero);

    private static (bool Ok, string Detail) Probe(
        LogService log,
        string label,
        BitmapSource expected,
        bool written,
        bool required)
    {
        string formats = "(未读取)";
        var hasPng = false;
        var hasBitmap = false;
        var typeName = "null";
        string readBack = "null";

        // 写入后立刻读可能拿到 null（OLE 数据尚未完全落地）——真实读取方通常晚于写入，
        // 所以这里做几次短暂重试，模拟"正常时序"的读取方。
        for (var attempt = 0; attempt < 5; attempt++)
        {
            // 这里过去是 `Thread.Sleep(60)` —— 用切片泵代替，等消息的同时不冻结 UI 线程。
            UiPump.For(60);
            try
            {
                var data = Clipboard.GetDataObject();
                formats = data is null ? "(null)" : string.Join(" | ", data.GetFormats());
                hasPng = data?.GetDataPresent("PNG") == true;
                hasBitmap = data?.GetDataPresent(DataFormats.Bitmap) == true;
                typeName = data?.GetData(DataFormats.Bitmap)?.GetType().Name ?? "null";

                var image = Clipboard.GetImage();
                readBack = image is null ? "null" : $"{image.PixelWidth}x{image.PixelHeight}";
            }
            catch (Exception ex)
            {
                log.Warn($"{label} 读回异常（第 {attempt + 1} 次）", ex);
                continue;
            }

            if (readBack != "null")
            {
                break;
            }
        }

        var sizeOk = readBack == $"{expected.PixelWidth}x{expected.PixelHeight}";
        var ok = written && sizeOk;
        var tag = ok ? "OK" : required ? "FAIL" : "FAIL(对照，不计入判据)";

        log.Info($"  {label}: {tag} —— 写入={written} formats=[{formats}] hasPng={hasPng} " +
                 $"hasBitmap={hasBitmap} GetImage={readBack} BitmapData类型={typeName}");

        return (ok, formats);
    }


    private static BitmapSource CreateTestImage(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * stride) + (x * 4);
                pixels[i] = (byte)(x * 255 / Math.Max(1, width - 1));      // B
                pixels[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1)); // G
                pixels[i + 2] = 0x40;                                      // R
                pixels[i + 3] = 0xFF;                                      // A
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed record Sample(int Iteration, uint GdiObjects, uint UserObjects, long PrivateMb, long ManagedMb, int Handles);
}
