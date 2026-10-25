using System.IO;
using System.Text;

namespace SimOCR.Export;

/// <summary>
/// Word（.docx）导出：每行一段落。
///
/// 最小合法包结构（LibreOffice/Word 均能打开）：
///   [Content_Types].xml + _rels/.rels + word/document.xml
/// 表格区域暂时仍按段落导出（保留制表符），"表格 → Word 表格"随 P2 的版面算法一起做。
/// </summary>
internal static class DocxExporter
{
    private const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
        </Types>
        """;

    private const string RootRels = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
        </Relationships>
        """;

    internal static void Write(string path, OcrExportContent content)
    {
        using var package = new OoxmlWriter(path);
        package.WriteXml("[Content_Types].xml", ContentTypes);
        package.WriteXml("_rels/.rels", RootRels);
        package.WriteXml("word/document.xml", BuildDocument(content));
    }

    private static string BuildDocument(OcrExportContent content)
    {
        var body = new StringBuilder();
        foreach (var paragraph in content.Lines)
        {
            if (paragraph.Length == 0)
            {
                // 空行用一个空段落表示，保留原始版面的段落间隔
                body.Append("<w:p/>");
                continue;
            }

            body.Append("<w:p><w:r><w:t xml:space=\"preserve\">")
                .Append(OoxmlWriter.Escape(paragraph))
                .Append("</w:t></w:r></w:p>");
        }

        return string.Concat(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""",
            """<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>""",
            body,
            "<w:sectPr/></w:body></w:document>");
    }
}
