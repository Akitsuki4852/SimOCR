using System.Globalization;
using System.Text;

namespace SimOCR.Export;

/// <summary>
/// Excel（.xlsx）导出：把 <see cref="OcrTableLayout"/> 还原出的行列写成单元格。
///
/// 最小合法包结构：[Content_Types].xml + _rels/.rels + xl/workbook.xml +
/// xl/_rels/workbook.xml.rels + xl/worksheets/sheet1.xml
/// 字符串用 inlineStr（避免 sharedStrings 的额外文件与索引维护），纯整数字段写成数字（可参与计算）。
/// </summary>
internal static class XlsxExporter
{
    private const string SheetName = "OCR";

    private const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
        </Types>
        """;

    private const string RootRels = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    private const string WorkbookRels = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
        </Relationships>
        """;

    internal static void Write(string path, OcrTableLayout layout)
    {
        using var package = new OoxmlWriter(path);
        package.WriteXml("[Content_Types].xml", ContentTypes);
        package.WriteXml("_rels/.rels", RootRels);
        package.WriteXml("xl/workbook.xml", BuildWorkbook());
        package.WriteXml("xl/_rels/workbook.xml.rels", WorkbookRels);
        package.WriteXml("xl/worksheets/sheet1.xml", BuildSheet(layout));
    }

    private static string BuildWorkbook()
        => string.Concat(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""",
            """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" """,
            """xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""",
            "<sheets><sheet name=\"", SheetName, "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");

    private static string BuildSheet(OcrTableLayout layout)
    {
        var builder = new StringBuilder(4096);
        builder.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""")
            .Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");

        // 列宽：第一列宽一些（通常是说明文字），其余按需；简单固定值，后续可按内容自适应
        if (layout.ColumnCount > 0)
        {
            builder.Append("<cols>");
            for (var c = 1; c <= layout.ColumnCount; c++)
            {
                var width = c == 1 ? 46 : 14;
                builder.Append(CultureInfo.InvariantCulture, $"<col min=\"{c}\" max=\"{c}\" width=\"{width}\" customWidth=\"1\"/>");
            }

            builder.Append("</cols>");
        }

        builder.Append("<sheetData>");

        // 表头行：把配置档给出的语义列名（显示名）写出来，
        // 让导出的表格自描述，读到的人不必回头猜每一列是什么。
        // 注意：**通用快速识别**产出的表格没有"语义列名"，此时 ColumnLabels 为空，
        // 数据从第 1 行开始 —— 保证"导出的内容与框选到的内容逐格一致"（不多一行人造表头）。
        var hasHeader = layout.ColumnLabels.Count > 0;
        var startRow = hasHeader ? 2 : 1;
        if (hasHeader)
        {
            builder.Append("<row r=\"1\">");
            for (var c = 0; c < layout.ColumnCount && c < layout.ColumnLabels.Count; c++)
            {
                var label = layout.ColumnLabels[c];
                if (string.IsNullOrEmpty(label))
                {
                    continue;
                }

                builder.Append(CultureInfo.InvariantCulture, $"<c r=\"{ColumnName(c)}1\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                    .Append(OoxmlWriter.Escape(label))
                    .Append("</t></is></c>");
            }

            builder.Append("</row>");
        }

        for (var r = 0; r < layout.RowCount; r++)
        {
            var rowNumber = r + startRow;
            builder.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowNumber}\">");
            for (var c = 0; c < layout.ColumnCount; c++)
            {
                var value = layout.Cells[r][c];
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                var reference = ColumnName(c) + rowNumber;
                var numeric = layout.ColumnNumeric.Count > c && layout.ColumnNumeric[c];
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    builder.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"><v>{number}</v></c>");
                }
                else if (numeric && FieldSemantics.TryParseNumber(value, out var decimalValue))
                {
                    // 数值列的小数：写成真数字（可直接筛选、透视、求和）
                    builder.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"><v>{decimalValue}</v></c>");
                }
                else
                {
                    builder.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                        .Append(OoxmlWriter.Escape(value))
                        .Append("</t></is></c>");
                }
            }

            builder.Append("</row>");
        }

        builder.Append("</sheetData>");

        // 合并单元格（标题跨列 / 表头跨列）：schema 里 mergeCells 紧跟在 sheetData 之后
        if (layout.Merges.Count > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $"<mergeCells count=\"{layout.Merges.Count}\">");
            foreach (var merge in layout.Merges)
            {
                var first = ColumnName(merge.Column) + (merge.Row + startRow);
                var last = ColumnName(merge.Column + merge.ColumnSpan - 1) + (merge.Row + merge.RowSpan + startRow - 1);
                builder.Append(CultureInfo.InvariantCulture, $"<mergeCell ref=\"{first}:{last}\"/>");
            }

            builder.Append("</mergeCells>");
        }

        builder.Append("</worksheet>");
        return builder.ToString();
    }

    /// <summary>0 → A、25 → Z、26 → AA。</summary>
    private static string ColumnName(int index)
    {
        var name = string.Empty;
        var value = index;
        while (true)
        {
            name = (char)('A' + value % 26) + name;
            value = value / 26 - 1;
            if (value < 0)
            {
                return name;
            }
        }
    }
}
