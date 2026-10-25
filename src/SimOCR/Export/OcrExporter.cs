using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using SimOCR.Ocr;

namespace SimOCR.Export;

/// <summary>V1 支持的导出格式。</summary>
internal enum OcrExportFormat
{
    Txt,
    Docx,
    Xlsx,
    Pdf,
}

/// <summary>
/// 一次导出的**内容**：把"文本"与"版面（框）"分开携带。
///
/// 为什么需要这一层：识别结果窗口的下半部是**可编辑**的，用户在文本框里改了错字、
/// 删了多余空格之后点"保存"，导出的必须是他改过的内容；但 XLSX 的表格结构来自识别框的坐标、
/// PDF 的隐形文本层需要文字**位置**，这些又只有模型（识别行）知道。于是：
///   * <see cref="Text"/> / <see cref="Lines"/> —— 供 TXT / DOCX（以及被改过时的 XLSX）使用；
///   * <see cref="Layout"/> —— 表格结构：未被编辑时用识别框坐标还原，被编辑过时按文本里的制表符切分；
///   * <see cref="Model"/> —— 供 PDF 的隐形文本层定位（按行对应，行数一致时用编辑后的文字）。
/// </summary>
internal sealed class OcrExportContent
{
    private OcrExportContent(
        string text,
        IReadOnlyList<string> lines,
        OcrTableLayout layout,
        OcrDocument model,
        bool textEdited)
    {
        Text = text;
        Lines = lines;
        Layout = layout;
        Model = model;
        TextEdited = textEdited;
    }

    /// <summary>导出用的整段文本（可能是用户编辑后的）。</summary>
    internal string Text { get; }

    /// <summary>按行拆开（保留空行）。</summary>
    internal IReadOnlyList<string> Lines { get; }

    /// <summary>表格结构。</summary>
    internal OcrTableLayout Layout { get; }

    /// <summary>识别模型（框坐标 —— PDF 文本层定位用）。</summary>
    internal OcrDocument Model { get; }

    /// <summary>文本是否被用户改过。</summary>
    internal bool TextEdited { get; }

    /// <summary>直接用识别结果（用户没动过文本）。</summary>
    internal static OcrExportContent FromDocument(OcrDocument document)
    {
        var text = document.ToPlainText();
        return new OcrExportContent(text, SplitLines(text), OcrTableLayout.Build(document), document, false);
    }

    /// <summary>用"编辑后的文本"+ 原识别模型（框坐标仍在）构造。</summary>
    internal static OcrExportContent FromEditedText(string text, OcrDocument model)
        => new(text, SplitLines(text), OcrTableLayout.FromText(text), model, true);

    /// <summary>用"已经算好的语义表"构造（增强识别做完校验后回写，保证校验结果进导出）。</summary>
    internal static OcrExportContent FromLayout(OcrTableLayout layout, OcrDocument model)
    {
        var rows = new List<string>(layout.RowCount);
        for (var r = 0; r < layout.RowCount; r++)
        {
            var cells = new List<string>(layout.ColumnCount);
            for (var c = 0; c < layout.ColumnCount; c++)
            {
                var cell = r < layout.Cells.Count && c < layout.Cells[r].Count ? layout.Cells[r][c] : string.Empty;
                cells.Add(cell.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' '));
            }

            rows.Add(string.Join('\t', cells));
        }

        var text = string.Join('\n', rows);
        return new OcrExportContent(text, SplitLines(text), layout, model, false);
    }

    /// <summary>
    /// 用**快速识别的可编辑网格**构造（通用识别：不做任何语义推断，导出内容 = 用户看到的那张表）。
    /// 网格没有"语义列名"，因此 Layout 的 ColumnLabels 为空 → XlsxExporter 不写人造表头，
    /// 数据从第 1 行开始，保证"导出的内容与框选到的内容逐格一致"。
    /// </summary>
    internal static OcrExportContent FromGrid(TableGrid grid, OcrDocument model)
    {
        var rows = new List<string>(grid.RowCount);
        for (var r = 0; r < grid.RowCount; r++)
        {
            var cells = new List<string>(grid.ColumnCount);
            for (var c = 0; c < grid.ColumnCount; c++)
            {
                var cell = r < grid.Cells.Count && c < grid.Cells[r].Count ? grid.Cells[r][c] : string.Empty;
                // 单元格内的换行/制表符会破坏"一行一记录"的结构，导出前压成空格
                cells.Add(cell.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' '));
            }

            rows.Add(string.Join('\t', cells));
        }

        var text = string.Join('\n', rows);
        return new OcrExportContent(text, SplitLines(text), OcrTableLayout.FromText(text), model, false);
    }

    private static IReadOnlyList<string> SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}

internal static class OcrExportFormatExtensions
{
    /// <summary>扩展名（含点）。</summary>
    internal static string Extension(this OcrExportFormat format) => format switch
    {
        OcrExportFormat.Txt => ".txt",
        OcrExportFormat.Docx => ".docx",
        OcrExportFormat.Xlsx => ".xlsx",
        OcrExportFormat.Pdf => ".pdf",
        _ => ".txt",
    };

    /// <summary>工具条按钮上的短标签。</summary>
    internal static string ShortLabel(this OcrExportFormat format) => format switch
    {
        OcrExportFormat.Txt => "TXT",
        OcrExportFormat.Docx => "DOCX",
        OcrExportFormat.Xlsx => "XLSX",
        OcrExportFormat.Pdf => "PDF",
        _ => "TXT",
    };

    internal static string DisplayName(this OcrExportFormat format) => format switch
    {
        OcrExportFormat.Txt => "纯文本",
        OcrExportFormat.Docx => "Word 文档",
        OcrExportFormat.Xlsx => "Excel 表格",
        OcrExportFormat.Pdf => "PDF（图片 + 隐形文本层）",
        _ => "文件",
    };

    internal static string FileDialogFilter(this OcrExportFormat format) => format switch
    {
        OcrExportFormat.Txt => "文本文件 (*.txt)|*.txt",
        OcrExportFormat.Docx => "Word 文档 (*.docx)|*.docx",
        OcrExportFormat.Xlsx => "Excel 工作簿 (*.xlsx)|*.xlsx",
        OcrExportFormat.Pdf => "PDF 文件 (*.pdf)|*.pdf",
        _ => "所有文件 (*.*)|*.*",
    };
}

/// <summary>导出入口：把导出内容按格式写盘。</summary>
internal static class OcrExporter
{
    /// <summary>写文件。返回写出的完整路径。</summary>
    internal static string Write(
        OcrExportFormat format,
        string path,
        OcrExportContent content,
        BitmapSource? image = null)
    {
        switch (format)
        {
            case OcrExportFormat.Txt:
                File.WriteAllText(path, content.Text, new UTF8Encoding(true));
                break;
            case OcrExportFormat.Docx:
                DocxExporter.Write(path, content);
                break;
            case OcrExportFormat.Xlsx:
                XlsxExporter.Write(path, content.Layout);
                break;
            case OcrExportFormat.Pdf:
                PdfExporter.Write(path, content, image);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "未知的导出格式");
        }

        return path;
    }
}
