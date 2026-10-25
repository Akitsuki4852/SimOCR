using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SimOCR.Export;
using SimOCR.Ocr;

namespace SimOCR.PureLogic;

/// <summary>
/// **表格还原与导出核心**的纯逻辑断言（<c>Export/</c> 下 22 个源文件原来在 xUnit 里一条覆盖都没有，
/// 只能靠需要真实桌面的 <c>--selftest-linegrid</c> 端到端跑）。
///
/// 为什么挑这五组：
///   * <see cref="IntervalMerge"/> —— 列切分与逐行闸门的公共地基，改一个阈值会同时影响
///     "切几列"与"这张表是否被否决"，是最该有单元断言的地方；
///   * <see cref="RowClustering"/> / <see cref="OcrDocument"/> —— 行分组决定"一行是不是一行"，
///     纯几何/纯文本，不需要桌面；
///   * <see cref="CellValidator"/> / <see cref="FieldSemantics"/> —— 全角/千分位/货币符号的归一，
///     错了会让数字变成文本（导出的 Excel 求和为 0，而界面看不出来）；
///   * 四种导出器 —— 断言的是**产物结构**（xlsx/docx 是 zip 包、pdf 有自己的头尾），
///     以及"整数写成真数字而不是文本"这条"能计算"的承诺。
///
/// 不在这里覆盖的：需要识别模型或真实桌面的链路（线格检测、OCR、结果窗口），
/// 那些仍由 <c>--selftest-*</c> / <c>--accept-*</c> 承担。
/// </summary>
internal static class ExportCoreChecks
{
    internal static PureCheckOutcome Run(PureCheckContext ctx)
    {
        ctx.Info("=== 自检：表格还原与导出核心（区间合并 / 行聚类 / 阅读顺序 / 单元格归一 / 四种导出）===");

        IntervalMergeSection(ctx);
        RowClusteringSection(ctx);
        DocumentTextSection(ctx);
        CellNormalizationSection(ctx);
        ExporterSection(ctx);

        return ctx.Outcome();
    }

    // ------------------------------------------------------------------ 区间合并

    /// <summary>列切分与"≥3px 分割线间距"闸门的公共地基。</summary>
    private static void IntervalMergeSection(PureCheckContext ctx)
    {
        var merged = IntervalMerge.Union([(0.0, 10.0), (12.0, 20.0)], 5.0);
        ctx.Check(
            "缝小于 minGap 并成一段",
            merged.Count == 1 && merged[0] == (0.0, 20.0),
            $"→ {Describe(merged)}");

        // 判据是**严格小于**：gap == minGap 时必须保持两段（0.8.x 起的既有口径）
        var kept = IntervalMerge.Union([(0.0, 10.0), (15.0, 20.0)], 5.0);
        ctx.Check("缝等于 minGap 不并", kept.Count == 2, $"→ {Describe(kept)}");

        var shuffled = IntervalMerge.Union([(12.0, 20.0), (0.0, 10.0), (13.0, 15.0)], 5.0);
        var sorted = IntervalMerge.Union([(0.0, 10.0), (12.0, 20.0), (13.0, 15.0)], 5.0);
        ctx.Check(
            "与输入顺序无关",
            shuffled.SequenceEqual(sorted) && sorted.Count == 1,
            $"→ {Describe(shuffled)}");

        var adjacent = IntervalMerge.UnionSpans([(0, 5), (6, 9)]);
        ctx.Check(
            "像素区间相邻即并",
            adjacent.Count == 1 && adjacent[0] == (0, 9),
            $"→ {DescribeSpans(adjacent)}");

        var separated = IntervalMerge.UnionSpans([(0, 5), (7, 9)]);
        ctx.Check("像素区间隔一像素不并", separated.Count == 2, $"→ {DescribeSpans(separated)}");

        ctx.Check(
            "分割线间距正好 3px 合格",
            IntervalMerge.HasMinimumSpacing([0.0, 3.0]),
            "（阈值是严格小于）");
        ctx.Check(
            "分割线间距 2.9px 否决",
            !IntervalMerge.HasMinimumSpacing([0.0, 2.9]),
            "（宁可不出表，也不出畸形表）");
    }

    // ------------------------------------------------------------------ 行聚类

    /// <summary>行分组：同一视觉行必须聚在一起，不同行必须分开；中位数取上中位。</summary>
    private static void RowClusteringSection(PureCheckContext ctx)
    {
        // 高度 10 / 20 / 30 / 40 → 排序后取 [Count/2] = 30（上中位，不是插值中位）
        var heights = new List<OcrLine>
        {
            Line("a", 0, 0, 10, 10),
            Line("b", 0, 20, 10, 40),
            Line("c", 0, 50, 10, 80),
            Line("d", 0, 90, 10, 130),
        };
        ctx.Check(
            "字高中位数取上中位",
            Math.Abs(RowClustering.MedianHeight(heights) - 30) < 1e-9,
            $"→ {RowClustering.MedianHeight(heights)}");

        // 零高度框不参与统计（否则一两个退化的框会把整张表的容差拉低）
        var withZero = new List<OcrLine>
        {
            Line("a", 0, 0, 10, 0),
            Line("b", 0, 0, 10, 10),
            Line("c", 0, 20, 10, 30),
        };
        ctx.Check(
            "零高度框不计入中位",
            Math.Abs(RowClustering.MedianHeight(withZero) - 10) < 1e-9,
            $"→ {RowClustering.MedianHeight(withZero)}");

        // 同一行：中心 y 差 1px，容差 = max(0.6×20, 6) = 12
        var sameRow = new List<OcrLine> { Line("A", 0, 10, 40, 30), Line("B", 60, 11, 100, 31) };
        var oneBand = RowClustering.Bands(sameRow, RowClustering.MedianHeight(sameRow));
        ctx.Check("y 相近聚成一行", oneBand.Count == 1 && oneBand[0].Count == 2, $"→ {oneBand.Count} 行");

        // 不同行：中心 y 差 40px，远大于容差 12
        var twoRows = new List<OcrLine> { Line("A", 0, 10, 40, 30), Line("B", 60, 50, 100, 70) };
        var bands = RowClustering.Bands(twoRows, RowClustering.MedianHeight(twoRows));
        ctx.Check("y 相差超过容差就分行", bands.Count == 2, $"→ {bands.Count} 行");
    }

    // ------------------------------------------------------------------ 阅读顺序与纯文本

    /// <summary>阅读顺序 = 先行后列；纯文本的分隔符按水平间隙分档。</summary>
    private static void DocumentTextSection(PureCheckContext ctx)
    {
        // 输入故意乱序：B 在前、A 在后，C 在下一行
        var document = new OcrDocument(
            300,
            100,
            [
                Line("B", 120, 10, 200, 30),
                Line("A", 10, 10, 100, 30),
                Line("C", 10, 60, 100, 80),
            ],
            0);

        var order = string.Join(",", document.Lines.Select(line => line.Text));
        ctx.Check("阅读顺序：先按行再按 x", order == "A,B,C", $"→ {order}");

        // 小间隙（2px < 0.35×20）→ 直接相连；被切开的单词/中文续行就是这个形态
        var tight = new OcrDocument(300, 100, [Line("Gear", 10, 10, 90, 30), Line("C", 92, 10, 120, 30)], 0);
        ctx.Check("同一行小间隙直接相连", tight.ToPlainText() == "GearC", $"→ 「{tight.ToPlainText()}」");

        // 中等间隙（10px，介于 0.35×20 与 1.5×20 之间）→ 补一个空格
        var medium = new OcrDocument(300, 100, [Line("Gear", 10, 10, 90, 30), Line("C", 100, 10, 130, 30)], 0);
        ctx.Check("同一行中间隙补空格", medium.ToPlainText() == "Gear C", $"→ 「{medium.ToPlainText()}」");

        // 大间隙（40px > 1.5×20）→ 制表符（粘进 Excel 能直接分列）
        var wide = new OcrDocument(300, 100, [Line("Gear", 10, 10, 90, 30), Line("C", 130, 10, 160, 30)], 0);
        ctx.Check("同一行大间隙给制表符", wide.ToPlainText() == "Gear\tC", $"→ 「{wide.ToPlainText()}」");

        var twoLines = new OcrDocument(300, 100, [Line("A", 10, 10, 40, 30), Line("B", 10, 60, 40, 80)], 0);
        ctx.Check("不同行给换行", twoLines.ToPlainText() == "A\nB", $"→ 「{twoLines.ToPlainText().Replace("\n", "\\n")}」");

        // 压印章上的文字不进纯文本（否则印章上的字会混进正文）
        var stamped = new OcrDocument(
            300,
            100,
            [Line("A", 10, 10, 40, 30), Line("SEAL", 60, 10, 100, 30).MarkOnStamp()],
            0);
        ctx.Check("印章文字不进纯文本", stamped.ToPlainText() == "A", $"→ 「{stamped.ToPlainText()}」");

        // 段落下标必须与文本对得上（识别结果窗口的"点框定位文本"就靠它）
        var (text, spans) = medium.ToPlainTextWithSpans();
        ctx.Check(
            "纯文本下标可回切",
            text == "Gear C"
            && spans.Count == 2
            && text.Substring(spans[0].Start, spans[0].Length) == "Gear"
            && text.Substring(spans[1].Start, spans[1].Length) == "C",
            $"→ {spans.Count} 段");
    }

    // ------------------------------------------------------------------ 单元格归一

    /// <summary>全角/千分位/货币符号：归一错了，数字就会变成文本（Excel 里求和变 0）。</summary>
    private static void CellNormalizationSection(PureCheckContext ctx)
    {
        ctx.Check(
            "全角数字拉回半角",
            CellValidator.Normalize("１２３．４５") == "123.45",
            $"→ 「{CellValidator.Normalize("１２３．４５")}」");

        ctx.Check(
            "全角空格归一",
            CellValidator.Normalize("　A　B　") == "A B",
            $"→ 「{CellValidator.Normalize("　A　B　")}」");

        ctx.Check(
            "千分位可解析",
            FieldSemantics.TryParseNumber("1,234.5", out var thousand) && Math.Abs(thousand - 1234.5) < 1e-9,
            "→ 1234.5");

        ctx.Check(
            "非数值不可解析",
            !FieldSemantics.TryParseNumber("12a", out _),
            "（不能把带字母的货号当数字）");

        ctx.Check(
            "非数值列原样保留",
            FieldSemantics.CleanCell("  abc  ", numeric: false) == "  abc  ",
            "（只净化数值列，不擅自 trim 文本列）");
    }

    // ------------------------------------------------------------------ 四种导出

    /// <summary>
    /// 导出产物必须"结构合法 + 数字是真数字"。三条关键承诺：
    ///   * xlsx / docx 是 OOXML 包（zip），缺一个部件 Office 就打不开；
    ///   * 整数单元格写成 <c>&lt;v&gt;</c> 而不是文本，否则 Excel 里求和恒为 0；
    ///   * pdf 有自己的头尾，截断的文件任何阅读器都拒绝打开。
    /// </summary>
    private static void ExporterSection(PureCheckContext ctx)
    {
        var grid = new TableGrid([0.0, 30.0, 60.0], [0.0, 100.0, 200.0, 300.0], "合成用例");
        grid.SetCells(
        [
            ["Item", "Qty", "Price"],
            ["Gear C", "12", "89"],
        ]);

        var model = new OcrDocument(
            300,
            60,
            [
                Line("Item", 5, 5, 80, 25),
                Line("Qty", 105, 5, 180, 25),
                Line("Price", 205, 5, 290, 25),
                Line("Gear C", 5, 35, 80, 55),
                Line("12", 105, 35, 180, 55),
                Line("89", 205, 35, 290, 55),
            ],
            0);

        var content = OcrExportContent.FromGrid(grid, model);

        // 临时目录放在**测试输出目录**下（AppContext.BaseDirectory），不放 %TEMP%：
        // 受限环境（CI 沙箱 / 受策略收紧的机器）里 %TEMP% 不一定可写，而测试输出目录
        // 一定是本次构建刚写过的、可写的。目录名带 GUID，两个测试进程并行也不打架。
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "export-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // ---- TXT：UTF-8 BOM + 内容逐字一致（Windows 记事本靠 BOM 认编码）
            var txtPath = Path.Combine(directory, "table.txt");
            OcrExporter.Write(OcrExportFormat.Txt, txtPath, content);
            var txtBytes = File.ReadAllBytes(txtPath);
            var hasBom = txtBytes.Length >= 3
                && txtBytes[0] == 0xEF && txtBytes[1] == 0xBB && txtBytes[2] == 0xBF;
            ctx.Check(
                "TXT：UTF-8 BOM + 内容逐字一致",
                hasBom && File.ReadAllText(txtPath) == "Item\tQty\tPrice\nGear C\t12\t89",
                $"→ {txtBytes.Length} 字节");

            // ---- XLSX：最小合法包 + 整数写成数字 + 文本写成 inlineStr
            var xlsxPath = Path.Combine(directory, "table.xlsx");
            OcrExporter.Write(OcrExportFormat.Xlsx, xlsxPath, content);
            using (var xlsx = ZipFile.OpenRead(xlsxPath))
            {
                var parts = xlsx.Entries.Select(entry => entry.FullName).ToList();
                ctx.Check(
                    "XLSX：五个必需部件齐全",
                    parts.Count == 5
                    && parts.Contains("xl/workbook.xml")
                    && parts.Contains("xl/worksheets/sheet1.xml"),
                    $"→ {parts.Count} 个部件");

                var sheet = ReadEntry(xlsx, "xl/worksheets/sheet1.xml");
                ctx.Check(
                    "XLSX：整数写成数字（可求和）",
                    sheet.Contains("<v>12</v>", StringComparison.Ordinal)
                    && sheet.Contains("<v>89</v>", StringComparison.Ordinal),
                    "（不是 inlineStr）");
                ctx.Check(
                    "XLSX：文本写成 inlineStr",
                    sheet.Contains("t=\"inlineStr\"", StringComparison.Ordinal)
                    && sheet.Contains("Gear C", StringComparison.Ordinal),
                    string.Empty);
                ctx.Check(
                    "XLSX：两行数据、无额外表头",
                    sheet.Split("<row ").Length - 1 == 2,
                    $"→ {sheet.Split("<row ").Length - 1} 行");
            }

            // ---- DOCX：document.xml 里必须有正文段落
            var docxPath = Path.Combine(directory, "table.docx");
            OcrExporter.Write(OcrExportFormat.Docx, docxPath, content);
            using (var docx = ZipFile.OpenRead(docxPath))
            {
                var document = ReadEntry(docx, "word/document.xml");
                ctx.Check(
                    "DOCX：document.xml 含导出文本",
                    document.Contains("Gear C", StringComparison.Ordinal)
                    && document.Contains("</w:p>", StringComparison.Ordinal),
                    $"→ {document.Length} 字符");
            }

            // ---- PDF：头尾合法（截断的文件阅读器直接拒绝）
            var pdfPath = Path.Combine(directory, "table.pdf");
            OcrExporter.Write(OcrExportFormat.Pdf, pdfPath, content);
            var pdf = Encoding.ASCII.GetString(File.ReadAllBytes(pdfPath));
            ctx.Check(
                "PDF：以 %PDF- 开头",
                pdf.StartsWith("%PDF-1.4", StringComparison.Ordinal),
                string.Empty);
            ctx.Check(
                "PDF：以 %%EOF 收尾",
                pdf.Contains("%%EOF", StringComparison.Ordinal),
                $"→ {pdf.Length} 字符");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录删不掉不影响断言结论
            }
        }
    }

    // ------------------------------------------------------------------ 辅助

    private static OcrLine Line(string text, double x0, double y0, double x1, double y1)
        => new(text, 0.99f, [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]);

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null)
        {
            return string.Empty;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Describe(List<(double Min, double Max)> intervals)
        => string.Join(" ", intervals.Select(interval => $"[{interval.Min:0.#},{interval.Max:0.#}]"));

    private static string DescribeSpans(List<(int Start, int End)> spans)
        => string.Join(" ", spans.Select(span => $"[{span.Start},{span.End}]"));
}
